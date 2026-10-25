/*
 * truedat x6: the AudioLoader's libavcodec decode step and frame conversion,
 * factored out of algorithms/io/audioloader.cpp so the same code is driven by
 * libavcodec in the loader, by a scripted fake decoder in
 * examples/test_audioloader_decodeloop.cpp, and by the native ASan harness in
 * essentia-build/spike-x6/asan/. It lives in utils/, not algorithms/io/,
 * because the build turns every header under algorithms/ into an algorithm
 * registration expecting a matching .cpp.
 *
 * ONE DECODED FRAME PER SCHEDULER STEP. The loader is the network's generator:
 * the scheduler runs it once, then every downstream algorithm, per step. The
 * graph downstream was sized for that rhythm - StereoDemuxer, for one, has a
 * 65,536-token output buffer. A packet can hold far more than one frame (APE:
 * 294,912 samples per packet), so emitting a whole packet per step overran it
 * (acquireForWrite ... > phantom size). step() therefore emits at most one
 * frame per call and lets libavcodec's own decoder hold the rest; it feeds
 * the decoder a packet only when it asks (EAGAIN) and sends the drain packet
 * once at end of stream. For a codec with one frame per packet this is
 * exactly output-x64.5's rhythm, so their outputs do not move.
 *
 * output-x64.5 sent each packet once and received ONE frame: a packet the
 * decoder refused with EAGAIN was dropped, and the frames of a multi-frame
 * packet after the first were left behind (making the next send refuse too) -
 * 84-93 % of the audio of WMA Lossless, WMA Pro and APE files. At end of
 * stream it re-sent the drain packet every iteration, so the second send's
 * AVERROR_EOF ended the drain after ONE delayed frame. It converted into a
 * fixed 384,000-byte buffer, passing swr_convert the frame's sample count as
 * capacity and clamping only after the write: a 65535-sample stereo FLAC
 * frame (524,280 bytes) overflowed the heap.
 *
 * The callables of step():
 *   readPacket() -> 0 with the next AUDIO packet ready to send, AVERROR_EOF
 *                   at end of stream, or a negative error (treated as EOF)
 *   send()       -> avcodec_send_packet() of that packet; the caller unrefs it
 *   sendDrain()  -> avcodec_send_packet(ctx, NULL)
 *   receive()    -> avcodec_receive_frame()
 *   onFrame()    -> consume the frame receive() just returned
 */

#ifndef ESSENTIA_AUDIOLOADER_DECODE_H
#define ESSENTIA_AUDIOLOADER_DECODE_H

#include <cstring>

extern "C" {
#include <libavutil/error.h>
#include <libavutil/frame.h>
#include <libavutil/mem.h>
#include <libavutil/samplefmt.h>
#include <libswresample/swresample.h>
}

namespace essentia {
namespace streaming {
namespace audioloader_detail {

// What one step() did. Negative values are AVERROR codes: a packet the
// decoder rejected or a frame it could not decode - log and step again.
enum StepResult {
  STEP_EMITTED = 0,    // one frame was handed to onFrame()
  STEP_FED = 1,        // a packet was sent; nothing emitted yet
  STEP_DRAINING = 2,   // end of stream reached, the drain packet is in
  STEP_FINISHED = 3    // the decoder is drained: nothing more will come
};

struct DecodeState {
  bool draining;     // the drain packet has been sent
  bool pending;      // a packet was read and not yet accepted by send() (refused with EAGAIN)
  int sendRetries;   // consecutive EAGAINs from send() with no frame emitted in between
  DecodeState() : draining(false), pending(false), sendRetries(0) {}
};

// A packet refused with EAGAIN stays pending and is re-sent after the decoder's
// output has been received - the libavcodec contract (the caller's send() must
// keep the packet on EAGAIN). Two refusals with nothing received in between
// break that contract; the packet is then dropped so the loop cannot spin.
template <class ReadPacket, class Send, class SendDrain, class Receive, class OnFrame>
int step(DecodeState& st, ReadPacket readPacket, Send send, SendDrain sendDrain,
         Receive receive, OnFrame onFrame) {
  int r = receive();
  if (r == 0) {
    onFrame();
    st.sendRetries = 0;
    return STEP_EMITTED;
  }
  if (r == AVERROR_EOF) return STEP_FINISHED;
  if (r != AVERROR(EAGAIN)) return r;          // a frame failed to decode

  // the decoder wants input
  if (st.draining) return STEP_FINISHED;       // EAGAIN while draining breaks the API: stop, never spin

  if (!st.pending) {
    int p = readPacket();
    if (p != 0) {                              // end of stream, or a read error treated as one
      st.draining = true;
      int s = sendDrain();
      if (s < 0 && s != AVERROR(EAGAIN) && s != AVERROR_EOF) return s;
      return STEP_DRAINING;
    }
    st.pending = true;
  }

  int s = send();
  if (s == 0) {
    st.pending = false;
    st.sendRetries = 0;
    return STEP_FED;
  }
  if (s == AVERROR(EAGAIN)) {
    if (++st.sendRetries < 2) return STEP_FED;  // output must be pending: the next receive() gets it, then we re-send
    st.pending = false;                         // refused twice with nothing to receive: API violation, packet lost
    st.sendRetries = 0;
    return s;
  }
  st.pending = false;                          // e.g. invalid data: the packet is dropped
  return s;
}

// Conversion output buffer, grown on demand and reused across frames.
// av_malloc'd, so SIMD-aligned like the fixed buffer it replaces.
class ConvertBuffer {
 public:
  ConvertBuffer() : _data(0), _capacity(0) {}
  ~ConvertBuffer() { av_freep(&_data); }

  uint8_t* data() const { return _data; }
  int capacity() const { return _capacity; }

  // Ensures room for `bytes`; existing contents are not preserved.
  bool reserve(int bytes) {
    if (bytes <= _capacity) return true;
    uint8_t* grown = (uint8_t*)av_malloc(bytes);
    if (!grown) return false;
    av_freep(&_data);
    _data = grown;
    _capacity = bytes;
    return true;
  }

 private:
  ConvertBuffer(const ConvertBuffer&);
  ConvertBuffer& operator=(const ConvertBuffer&);
  uint8_t* _data;
  int _capacity;
};

// Converts one decoded frame to interleaved float in `buf`. Returns the bytes
// written (>= 0) or a negative AVERROR. The capacity comes from the frame
// actually received and from the resampler's own bound (swr_get_out_samples),
// never from a fixed maximum: AVCodecContext.frame_size is not a maximum (it
// is set only for constant-size frames). swr_convert is told the real
// capacity; nothing is clamped after a write.
inline int convertFrame(SwrContext* swr, const AVFrame* frame, AVSampleFormat inFormat,
                        int channels, ConvertBuffer& buf) {
  if (frame->nb_samples <= 0) return 0;

  if (inFormat == AV_SAMPLE_FMT_FLT) {
    // already packed float: copy exactly the frame's samples
    int bytes = av_samples_get_buffer_size(NULL, channels, frame->nb_samples, AV_SAMPLE_FMT_FLT, 1);
    if (bytes < 0) return bytes;
    if (!buf.reserve(bytes)) return AVERROR(ENOMEM);
    memcpy(buf.data(), frame->data[0], bytes);
    return bytes;
  }

  int outSamples = swr_get_out_samples(swr, frame->nb_samples);
  if (outSamples < 0) return outSamples;
  int capacity = av_samples_get_buffer_size(NULL, channels, outSamples, AV_SAMPLE_FMT_FLT, 1);
  if (capacity < 0) return capacity;
  if (!buf.reserve(capacity)) return AVERROR(ENOMEM);

  uint8_t* out = buf.data();
  int written = swr_convert(swr, &out, outSamples,
                            (const uint8_t**)frame->extended_data, frame->nb_samples);
  if (written < 0) return written;
  return written * channels * av_get_bytes_per_sample(AV_SAMPLE_FMT_FLT);
}

} // namespace audioloader_detail
} // namespace streaming
} // namespace essentia

#endif // ESSENTIA_AUDIOLOADER_DECODE_H
