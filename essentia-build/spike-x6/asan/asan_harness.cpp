// x6 B1 ASan harness (native Linux, never shipped).
//
// The AudioLoader's decode path outside Essentia: the same open sequence as
// AudioLoader::openAudioFile (avformat_open_input, avformat_find_stream_info,
// first audio stream, avcodec_parameters_to_context, avcodec_open2), the same
// format-only swr setup, and the SAME send/receive loop and frame conversion -
// essentia/utils/audioloader_decode.h, included, not copied. Built against an
// FFmpeg compiled with --toolchain=gcc-asan, so the decoders and
// libswresample are instrumented too (see build-ffmpeg-asan.sh).
//
//   asan_harness <input> <output.f32> [--legacy-capacity]
//
// Writes the PCM exactly as essentia_standard_audioloader_dump does
// (interleaved f32le, native rate and channel count) so the run joins the
// parity table. --legacy-capacity is an INSTRUMENT CHECK only: it converts the
// way output-x64.5 did (fixed 384,000-byte buffer, frame sample count passed
// as capacity), to prove this ASan build reports that overflow.
// Exit: 0 ok, 1 usage, 2 open/decoder failure, 3 write failure.

#include <cstdio>
#include <cstring>
#include <vector>

extern "C" {
#include <libavcodec/avcodec.h>
#include <libavformat/avformat.h>
#include <libavutil/opt.h>
#include <libswresample/swresample.h>
}

#include "audioloader_decode.h"

using namespace essentia::streaming::audioloader_detail;

static int fail(int code, const char* what, int err = 0) {
  char buf[256] = "";
  if (err) av_strerror(err, buf, sizeof buf);
  fprintf(stderr, "ERROR: %s %s\n", what, buf);
  return code;
}

int main(int argc, char** argv) {
  if (argc < 3 || argc > 4 || (argc == 4 && strcmp(argv[3], "--legacy-capacity") != 0)) {
    fprintf(stderr, "usage: %s <input> <output.f32> [--legacy-capacity]\n", argv[0]);
    return 1;
  }
  const bool legacy = argc == 4;
  av_log_set_level(AV_LOG_QUIET);   // as AudioLoader::configure

  AVFormatContext* demux = NULL;
  AVCodecContext* ctx = NULL;
  // error paths release what is open, as the loader's destructor does, so
  // LeakSanitizer reports only real leaks
  auto bail = [&](const char* what, int err = 0) {
    avcodec_free_context(&ctx);
    avformat_close_input(&demux);
    return fail(2, what, err);
  };

  int e = avformat_open_input(&demux, argv[1], NULL, NULL);
  if (e != 0) return bail("open", e);
  if ((e = avformat_find_stream_info(demux, NULL)) < 0) return bail("stream info", e);

  int streamIdx = -1;
  for (unsigned i = 0; i < demux->nb_streams; ++i)
    if (demux->streams[i]->codecpar->codec_type == AVMEDIA_TYPE_AUDIO) { streamIdx = (int)i; break; }
  if (streamIdx < 0) return bail("no audio stream");

  const AVCodecParameters* par = demux->streams[streamIdx]->codecpar;
  const AVCodec* codec = avcodec_find_decoder(par->codec_id);
  if (!codec) return bail("Unsupported codec!");
  ctx = avcodec_alloc_context3(codec);
  if (!ctx || avcodec_parameters_to_context(ctx, par) < 0) return bail("codec context");
  if ((e = avcodec_open2(ctx, codec, NULL)) < 0) return bail("avcodec_open2", e);

  const int channels = ctx->ch_layout.nb_channels;
  if (channels > 2) return bail("more than 2 channels");

  AVChannelLayout layout;
  if (ctx->ch_layout.nb_channels > 0) layout = ctx->ch_layout;
  else av_channel_layout_default(&layout, ctx->ch_layout.nb_channels);
  SwrContext* swr = swr_alloc();
  av_opt_set_chlayout(swr, "in_chlayout", &layout, 0);
  av_opt_set_chlayout(swr, "out_chlayout", &layout, 0);
  av_opt_set_int(swr, "in_sample_rate", ctx->sample_rate, 0);
  av_opt_set_int(swr, "out_sample_rate", ctx->sample_rate, 0);
  av_opt_set_int(swr, "in_sample_fmt", ctx->sample_fmt, 0);
  av_opt_set_int(swr, "out_sample_fmt", AV_SAMPLE_FMT_FLT, 0);
  if (swr_init(swr) < 0) { swr_free(&swr); return bail("swr_init"); }

  FILE* out = fopen(argv[2], "wb");
  if (!out) { swr_free(&swr); bail("cannot open output"); return 3; }

  AVPacket* pkt = av_packet_alloc();
  AVFrame* frame = av_frame_alloc();
  ConvertBuffer buf;
  uint8_t* legacyBuf = legacy ? (uint8_t*)av_malloc(384000) : NULL;
  unsigned long long frames = 0;
  bool writeFailed = false;

  auto onFrame = [&] {
    int bytes;
    const uint8_t* data;
    if (legacy) {   // output-x64.5's conversion, instrument check only
      uint8_t* o = legacyBuf;
      int w = swr_convert(swr, &o, frame->nb_samples, (const uint8_t**)frame->data, frame->nb_samples);
      bytes = w > 0 ? w * channels * 4 : 0;
      if (bytes > 384000) bytes = 384000;
      data = legacyBuf;
    }
    else {
      bytes = convertFrame(swr, frame, ctx->sample_fmt, channels, buf);
      data = buf.data();
    }
    av_frame_unref(frame);
    if (bytes <= 0) return;
    if (fwrite(data, 1, bytes, out) != (size_t)bytes) writeFailed = true;
    frames += bytes / (4 * channels);
  };
  auto receive = [&] { return avcodec_receive_frame(ctx, frame); };

  while (av_read_frame(demux, pkt) == 0) {
    if (pkt->stream_index == streamIdx)
      decodePacket([&] { return avcodec_send_packet(ctx, pkt); }, receive, onFrame);
    av_packet_unref(pkt);
  }
  drain([&] { return avcodec_send_packet(ctx, NULL); }, receive, onFrame);

  if (fclose(out) != 0) writeFailed = true;
  printf("samples=%llu rate=%d channels=%d codec=%s\n", frames, ctx->sample_rate, channels, codec->name);

  av_free(legacyBuf);
  av_frame_free(&frame);
  av_packet_free(&pkt);
  swr_free(&swr);
  avcodec_free_context(&ctx);
  avformat_close_input(&demux);
  return writeFailed ? 3 : 0;
}
