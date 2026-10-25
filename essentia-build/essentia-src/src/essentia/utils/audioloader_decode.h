/*
 * truedat x6: the AudioLoader's libavcodec send/receive loop and frame
 * conversion, factored out of algorithms/io/audioloader.cpp so the same code
 * is driven by libavcodec in the loader, by a scripted fake decoder in
 * examples/test_audioloader_decodeloop.cpp, and by the native ASan harness in
 * essentia-build/spike-x6/asan/. It lives in utils/, not algorithms/io/,
 * because the build turns every header under algorithms/ into an algorithm
 * registration expecting a matching .cpp.
 *
 * The loop's callables:
 *   send()      -> avcodec_send_packet() result for the current packet
 *   sendDrain() -> avcodec_send_packet(ctx, NULL) result
 *   receive()   -> avcodec_receive_frame() result
 *   onFrame()   -> consume the frame receive() just returned
 *
 * output-x64.5 sent each packet once and received ONE frame: a packet the
 * decoder refused with EAGAIN was dropped, and the frames of a multi-frame
 * packet after the first were left behind (making the next send refuse too).
 * At end of stream it re-sent the drain packet every iteration, so the second
 * send's AVERROR_EOF ended the drain after ONE delayed frame. It converted
 * into a fixed 384,000-byte buffer, passing swr_convert the frame's sample
 * count as capacity and clamping only after the write: a 65535-sample stereo
 * FLAC frame (524,280 bytes) overflowed the heap.
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

// Receives every frame the decoder has ready, counting them in *frames.
// Returns 0 once the decoder wants input (EAGAIN), AVERROR_EOF once it is
// fully drained, or the first other error.
template <class Receive, class OnFrame>
int receiveAll(Receive receive, OnFrame onFrame, int* frames) {
  for (;;) {
    int r = receive();
    if (r == 0) {
      onFrame();
      ++*frames;
      continue;
    }
    return r == AVERROR(EAGAIN) ? 0 : r;
  }
}

// One packet. Sends it; on EAGAIN receives the pending output and resends the
// SAME packet; once it is accepted, receives until the decoder wants input.
// Returns 0, or the first error (the caller unrefs the packet either way).
template <class Send, class Receive, class OnFrame>
int decodePacket(Send send, Receive receive, OnFrame onFrame) {
  for (;;) {
    int s = send();
    int frames = 0;
    if (s == 0) {
      int r = receiveAll(receive, onFrame, &frames);
      return r == AVERROR_EOF ? 0 : r;
    }
    if (s != AVERROR(EAGAIN)) return s;

    int r = receiveAll(receive, onFrame, &frames);
    if (r < 0) return r;
    // EAGAIN from send AND receive breaks the API contract; give up on this
    // packet rather than spin
    if (frames == 0) return AVERROR(EAGAIN);
  }
}

// End of stream. Sends the drain packet ONCE (resending only if the decoder
// refused it with EAGAIN while output was pending) and receives until
// AVERROR_EOF. Returns 0, or the first error.
template <class SendDrain, class Receive, class OnFrame>
int drain(SendDrain sendDrain, Receive receive, OnFrame onFrame) {
  for (;;) {
    int s = sendDrain();
    int frames = 0;
    if (s == 0 || s == AVERROR_EOF) {   // AVERROR_EOF: already draining
      int r = receiveAll(receive, onFrame, &frames);
      // receiveAll returns 0 on EAGAIN, which a draining decoder must not
      // answer; stop rather than spin
      return r == AVERROR_EOF ? 0 : r;
    }
    if (s != AVERROR(EAGAIN)) return s;

    int r = receiveAll(receive, onFrame, &frames);
    if (r < 0) return r == AVERROR_EOF ? 0 : r;
    if (frames == 0) return AVERROR(EAGAIN);
  }
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
