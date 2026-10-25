/*
 * Copyright (C) 2006-2021  Music Technology Group - Universitat Pompeu Fabra
 *
 * This file is part of Essentia
 *
 * Essentia is free software: you can redistribute it and/or modify it under
 * the terms of the GNU Affero General Public License as published by the Free
 * Software Foundation (FSF), either version 3 of the License, or (at your
 * option) any later version.
 *
 * This program is distributed in the hope that it will be useful, but WITHOUT
 * ANY WARRANTY; without even the implied warranty of MERCHANTABILITY or FITNESS
 * FOR A PARTICULAR PURPOSE.  See the GNU General Public License for more
 * details.
 *
 * You should have received a copy of the Affero GNU General Public License
 * version 3 along with this program.  If not, see http://www.gnu.org/licenses/
 */

#include "audioloader.h"
#include "algorithmfactory.h"
#include <iomanip>  //  setw()

using namespace std;

namespace essentia {
namespace streaming {

const char* AudioLoader::name = essentia::standard::AudioLoader::name;
const char* AudioLoader::category = essentia::standard::AudioLoader::category;
const char* AudioLoader::description = essentia::standard::AudioLoader::description;


AudioLoader::~AudioLoader() {
    closeAudioFile();

    av_freep(&_md5Encoded);
    av_frame_free(&_decodedFrame);   // an AVFrame, not a plain allocation
}

void AudioLoader::configure() {
    // set ffmpeg to be silent by default, so we don't have these annoying
    // "invalid new backstep" messages anymore, when everything is actually fine
    av_log_set_level(AV_LOG_QUIET);   // choices: {AV_LOG_VERBOSE, AV_LOG_QUIET}
    _computeMD5 = parameter("computeMD5").toBool();
    _selectedStream = parameter("audioStream").toInt();
    reset();
}


void AudioLoader::openAudioFile(const string& filename) {
    E_DEBUG(EAlgorithm, "AudioLoader: opening file: " << filename);

    // Open file
    int errnum;
    if ((errnum = avformat_open_input(&_demuxCtx, filename.c_str(), NULL, NULL)) != 0) {
        char errorstr[128];
        string error = "Unknown error";
        if (av_strerror(errnum, errorstr, 128) == 0) error = errorstr;
        throw EssentiaException("AudioLoader: Could not open file \"", filename, "\", error = ", error);
    }

    // Retrieve stream information
    if ((errnum = avformat_find_stream_info(_demuxCtx, NULL)) < 0) {
        char errorstr[128];
        string error = "Unknown error";
        if (av_strerror(errnum, errorstr, 128) == 0) error = errorstr;
        avformat_close_input(&_demuxCtx);
        _demuxCtx = 0;
        throw EssentiaException("AudioLoader: Could not find stream information, error = ", error);
    }

    // Check that we have only 1 audio stream in the file
    _streams.clear();
    for (int i=0; i<(int)_demuxCtx->nb_streams; i++) {
        // Use modern API to get codec parameters
        const AVCodecParameters* codecParams = _demuxCtx->streams[i]->codecpar;
        if (codecParams->codec_type == AVMEDIA_TYPE_AUDIO) {
            _streams.push_back(i);
        }
    }
    int nAudioStreams = _streams.size();
    
    if (nAudioStreams == 0) {
        avformat_close_input(&_demuxCtx);
        _demuxCtx = 0;
        throw EssentiaException("AudioLoader ERROR: found 0 streams in the file, expecting one or more audio streams");
    }

    if (_selectedStream >= nAudioStreams) {
        avformat_close_input(&_demuxCtx);
        _demuxCtx = 0;
        throw EssentiaException("AudioLoader ERROR: 'audioStream' parameter set to ", _selectedStream ,". It should be smaller than the audio streams count, ", nAudioStreams);
    }

    _streamIdx = _streams[_selectedStream];

    // Create codec context from stream parameters (modern approach)
    const AVCodecParameters* codecParams = _demuxCtx->streams[_streamIdx]->codecpar;
    _audioCodec = avcodec_find_decoder(codecParams->codec_id);

    if (!_audioCodec) {
        throw EssentiaException("AudioLoader: Unsupported codec!");
    }

    _audioCtx = avcodec_alloc_context3(_audioCodec);
    if (!_audioCtx) {
        throw EssentiaException("AudioLoader: Could not allocate codec context");
    }

    // Copy parameters from stream to codec context
    if (avcodec_parameters_to_context(_audioCtx, codecParams) < 0) {
        avcodec_free_context(&_audioCtx);
        throw EssentiaException("AudioLoader: Could not copy codec parameters");
    }

    if (avcodec_open2(_audioCtx, _audioCodec, NULL) < 0) {
        avcodec_free_context(&_audioCtx);
        throw EssentiaException("AudioLoader: Unable to instantiate codec...");
    }
  
    // Configure format conversion (no samplerate conversion yet)
    // Use modern channel layout API
    AVChannelLayout layout;
    if (_audioCtx->ch_layout.nb_channels > 0) {
        layout = _audioCtx->ch_layout;
    } else {
        // Fallback for older codecs that might not have channel layout set
        av_channel_layout_default(&layout, _audioCtx->ch_layout.nb_channels);
    }

    E_DEBUG(EAlgorithm, "AudioLoader: using sample format conversion from libswresample");
    _convertCtxAv = swr_alloc();
        
    // Use modern channel layout API for swresample configuration
    av_opt_set_chlayout(_convertCtxAv, "in_chlayout", &layout, 0);
    av_opt_set_chlayout(_convertCtxAv, "out_chlayout", &layout, 0);
    av_opt_set_int(_convertCtxAv, "in_sample_rate", _audioCtx->sample_rate, 0);
    av_opt_set_int(_convertCtxAv, "out_sample_rate", _audioCtx->sample_rate, 0);
    av_opt_set_int(_convertCtxAv, "in_sample_fmt", _audioCtx->sample_fmt, 0);
    av_opt_set_int(_convertCtxAv, "out_sample_fmt", AV_SAMPLE_FMT_FLT, 0);

    if (swr_init(_convertCtxAv) < 0) {
        throw EssentiaException("AudioLoader: Could not initialize swresample context");
    }

    av_init_packet(&_packet);

    _decodedFrame = av_frame_alloc();
    if (!_decodedFrame) {
        throw EssentiaException("AudioLoader: Could not allocate audio frame");
    }

    av_md5_init(_md5Encoded);
}


void AudioLoader::closeAudioFile() {
    if (!_demuxCtx) {
        return;
    }

    if (_convertCtxAv) {
        swr_close(_convertCtxAv);
        swr_free(&_convertCtxAv);
    }

    // Close the codec using modern API
    if (_audioCtx) {
        avcodec_free_context(&_audioCtx);
    }
    // Close the audio file
    if (_demuxCtx) avformat_close_input(&_demuxCtx);

    // free AVPacket using modern API
    // TODO: use a variable for whether _packet is initialized or not
    av_packet_unref(&_packet);
    _demuxCtx = 0;
    _audioCtx = 0;
    _streams.clear();
}


void AudioLoader::pushChannelsSampleRateInfo(int nChannels, Real sampleRate) {
    if (nChannels > 2) {
        throw EssentiaException("AudioLoader: could not load audio. Audio file has more than 2 channels.");
    }
    if (sampleRate <= 0) {
        throw EssentiaException("AudioLoader: could not load audio. Audio sampling rate must be greater than 0.");
    }

    _nChannels = nChannels;

    _channels.push(nChannels);
    _sampleRate.push(sampleRate);
}


void AudioLoader::pushCodecInfo(std::string codec, int bit_rate) {
    _codec.push(codec);
    _bit_rate.push(bit_rate);
}


string uint8_t_to_hex(uint8_t* input, int size) {
    ostringstream result;
    for(int i=0; i<size; ++i) {
        result << setw(2) << setfill('0') << hex << (int) input[i];
    }
    return result.str();
}


AlgorithmStatus AudioLoader::process() {
    if (!parameter("filename").isConfigured()) {
        throw EssentiaException("AudioLoader: Trying to call process() on an AudioLoader algo which hasn't been correctly configured.");
    }

    // read frames until we get a good one
    do {
        // frees the previous iteration's packet when it belonged to another
        // stream (e.g. cover art); a no-op on the blank packet we enter with
        av_packet_unref(&_packet);
        int result = av_read_frame(_demuxCtx, &_packet);
        //E_DEBUG(EAlgorithm, "AudioLoader: called av_read_frame(), got result = " << result);
        if (result != 0) {
            // 0 = OK, < 0 = error or EOF
            if (result != AVERROR_EOF) {
                char errstring[1204];
                av_strerror(result, errstring, sizeof(errstring));
                ostringstream msg;
                msg << "AudioLoader: Error reading frame: " << errstring;
                E_WARNING(msg.str());
            }
            shouldStop(true);
            flushPacket();
            closeAudioFile();
            if (_computeMD5) {
                av_md5_final(_md5Encoded, _checksum);
                _md5.push(uint8_t_to_hex(_checksum, 16));
            }
            else {
                string md5 = "";
                _md5.push(md5);
            }
            return FINISHED;
        }
    } while (_packet.stream_index != _streamIdx);

    // compute md5 first
    if (_computeMD5) {
        av_md5_update(_md5Encoded, _packet.data, _packet.size);
    }
    
    // decode this packet; every frame the decoder hands back is pushed by
    // emitDecodedFrame() before decodePacket() returns
    decodePacket();

    // needs to be freed using modern API !!
    av_packet_unref(&_packet);
    
    return OK;
}


int AudioLoader::decode_audio_frame(AVCodecContext* audioCtx,
                                    float* output,
                                    int* outputSize,
                                    AVPacket* packet) {

    // _dataSize  input = number of bytes available for write in buff
    //           output = number of bytes actually written (actual: FLT data)
    //E_DEBUG(EAlgorithm, "decode_audio_frame, available bytes in buffer = " << _dataSize);
    int gotFrame = 0;
    av_frame_unref(_decodedFrame);

    // Use modern decoding API: send packet to decoder
    int send_result = avcodec_send_packet(audioCtx, packet);
    if (send_result < 0) return send_result; // error handling should be done outside

    // Receive decoded frame from decoder
    int receive_result = avcodec_receive_frame(audioCtx, _decodedFrame);
    if (receive_result == AVERROR(EAGAIN) || receive_result == AVERROR_EOF) {
        gotFrame = 0;
        // Return the number of bytes that would have been consumed
        // For flush packets (empty packets), return 0
        return (packet->size > 0) ? packet->size : 0;
    } else if (receive_result < 0) {
        return receive_result; // error handling should be done outside
    }
    gotFrame = 1;

    if (gotFrame) {
        int inputSamples = _decodedFrame->nb_samples;
        int inputPlaneSize = av_samples_get_buffer_size(NULL, _nChannels, inputSamples,
                                                        audioCtx->sample_fmt, 1);
        int outputPlaneSize = av_samples_get_buffer_size(NULL, _nChannels, inputSamples,
                                                        AV_SAMPLE_FMT_FLT, 1);
        // the size of the output buffer in samples
        int outputBufferSamples = *outputSize / 
                (av_get_bytes_per_sample(AV_SAMPLE_FMT_FLT) * _nChannels);

        if (outputBufferSamples < inputSamples) { 
            // this should never happen, throw exception here
            throw EssentiaException("AudioLoader: Insufficient buffer size for format conversion");
        }

        if (audioCtx->sample_fmt == AV_SAMPLE_FMT_FLT) {
            // TODO: no need in this check? Not many of common formats support FLT
            // no conversion needed, direct copy from our frame to output buffer
            memcpy(output, _decodedFrame->data[0], inputPlaneSize);
        }
        else {
          int samplesWrittern = swr_convert(_convertCtxAv,
                                          (uint8_t**) &output, 
                                          outputBufferSamples, 
                                          (const uint8_t**)_decodedFrame->data,
                                          inputSamples);

          if (samplesWrittern < inputSamples) {
              // TODO: there may be data remaining in the internal FIFO buffer
              // to get this data: call swr_convert() with NULL input
              // Test if this happens in practice
              ostringstream msg;
              msg << "AudioLoader: Incomplete format conversion (some samples missing)"
                  << " from " << av_get_sample_fmt_name(_audioCtx->sample_fmt)
                  << " to "   << av_get_sample_fmt_name(AV_SAMPLE_FMT_FLT);
              throw EssentiaException(msg);
          }
        }
        *outputSize = outputPlaneSize;
    }
    else {
      E_DEBUG(EAlgorithm, "AudioLoader: tried to decode packet but didn't get any frame...");
      *outputSize = 0;
    }

    // Return the number of bytes consumed from the packet
    // For the modern API, we consume the entire packet when we send it
    return packet->size;
}

void AudioLoader::flushPacket() {
    // End of stream: the drain packet (NULL) tells the decoder to release
    // the frames it is holding back; see audioloader_decode.h
    av_packet_unref(&_packet);
    audioloader_detail::drain(
        [this] { return avcodec_send_packet(_audioCtx, NULL); },
        [this] { return avcodec_receive_frame(_audioCtx, _decodedFrame); },
        [this] { emitDecodedFrame(); });
}


/**
 * Sends the AVPacket stored in _packet to the decoder and pushes every frame
 * the send/receive loop (audioloader_decode.h) hands back, via emitDecodedFrame().
 */

int AudioLoader::decodePacket() {
    int result = audioloader_detail::decodePacket(
        [this] { return avcodec_send_packet(_audioCtx, &_packet); },
        [this] { return avcodec_receive_frame(_audioCtx, _decodedFrame); },
        [this] { emitDecodedFrame(); });
    if (result < 0) {
        char errstring[1204];
        av_strerror(result, errstring, sizeof(errstring));
        E_WARNING("AudioLoader: decoding error: " << errstring);
    }
    return result;
}


/**
 * Converts the frame in _decodedFrame to interleaved float in _convert (sized
 * from this frame, see audioloader_decode.h) and pushes it downstream.
 */

void AudioLoader::emitDecodedFrame() {
    int bytes = audioloader_detail::convertFrame(_convertCtxAv, _decodedFrame,
                                                 _audioCtx->sample_fmt, _nChannels, _convert);
    av_frame_unref(_decodedFrame);   // the samples now live in _convert
    if (bytes < 0) {
        char errstring[1204];
        av_strerror(bytes, errstring, sizeof(errstring));
        E_WARNING("AudioLoader: could not convert decoded audio: " << errstring);
        return;
    }

    _dataSize = bytes;
    copyFFmpegOutput();
    _dataSize = 0;
}


void AudioLoader::copyFFmpegOutput() {
    int bytesPerSample = av_get_bytes_per_sample(AV_SAMPLE_FMT_FLT);
    int nsamples = _dataSize / (bytesPerSample * _nChannels);
    const float* fbuf = (const float*)_convert.data(); // interpret buffer as floats for copying

    // Push in chunks: a frame is pushed whole now (output-x64.5 clamped it to
    // its fixed buffer), and one request above the source's phantom size
    // (262144 tokens for forLargeAudioStream) throws.
    const int maxChunk = 65536;
    for (int done = 0; done < nsamples; ) {
        int n = std::min(maxChunk, nsamples - done);

        // acquire necessary data
        bool ok = _audio.acquire(n);
        if (!ok) {
            throw EssentiaException("AudioLoader: could not acquire output for audio");
        }

        vector<StereoSample>& audio = *((vector<StereoSample>*)_audio.getTokens());

        if (_nChannels == 1) {
            for (int i=0; i<n; i++) {
              audio[i].left() = fbuf[done + i];
            }
        }
        else { // _nChannels == 2
          for (int i=0; i<n; i++) {
            audio[i].left() = fbuf[2*(done + i)];
            audio[i].right() = fbuf[2*(done + i)+1];
          }
        }

        _audio.release(n);
        done += n;
    }
}


void AudioLoader::reset() {
    Algorithm::reset();

    if (!parameter("filename").isConfigured()) return;

    string filename = parameter("filename").toString();

    closeAudioFile();
    openAudioFile(filename);

    pushChannelsSampleRateInfo(_audioCtx->ch_layout.nb_channels, _audioCtx->sample_rate);
    pushCodecInfo(_audioCodec->name, _audioCtx->bit_rate);
}

} // namespace streaming
} // namespace essentia


namespace essentia {
namespace standard {

const char* AudioLoader::name = "AudioLoader";
const char* AudioLoader::category = "Input/output";
const char* AudioLoader::description = DOC("This algorithm loads the single audio stream contained in a given audio or video file. Supported formats are all those supported by the FFmpeg library including wav, aiff, flac, ogg and mp3.\n"
"\n"
"This algorithm will throw an exception if it was not properly configured which is normally due to not specifying a valid filename. Invalid names comprise those with extensions different than the supported  formats and non existent files. If using this algorithm on Windows, you must ensure that the filename is encoded as UTF-8\n\n"
"Note: ogg files are decoded in reverse phase, due to be using ffmpeg library.\n"
"\n"
"References:\n"
"  [1] WAV - Wikipedia, the free encyclopedia,\n"
"      http://en.wikipedia.org/wiki/Wav\n"
"  [2] Audio Interchange File Format - Wikipedia, the free encyclopedia,\n"
"      http://en.wikipedia.org/wiki/Aiff\n"
"  [3] Free Lossless Audio Codec - Wikipedia, the free encyclopedia,\n"
"      http://en.wikipedia.org/wiki/Flac\n"
"  [4] Vorbis - Wikipedia, the free encyclopedia,\n"
"      http://en.wikipedia.org/wiki/Vorbis\n"
"  [5] MP3 - Wikipedia, the free encyclopedia,\n"
"      http://en.wikipedia.org/wiki/Mp3");


void AudioLoader::createInnerNetwork() {
    _loader = streaming::AlgorithmFactory::create("AudioLoader");
    _audioStorage = new streaming::VectorOutput<StereoSample>();

    _loader->output("audio")           >>  _audioStorage->input("data");
    _loader->output("sampleRate")      >>  PC(_pool, "internal.sampleRate");
    _loader->output("numberChannels")  >>  PC(_pool, "internal.numberChannels");
    _loader->output("md5")             >>  PC(_pool, "internal.md5");
    _loader->output("codec")           >>  PC(_pool, "internal.codec");
    _loader->output("bit_rate")        >>  PC(_pool, "internal.bit_rate");
    _network = new scheduler::Network(_loader);
}

void AudioLoader::configure() {
    _loader->configure(INHERIT("filename"),
                       INHERIT("computeMD5"),
                       INHERIT("audioStream"));
}

void AudioLoader::compute() {
    if (!parameter("filename").isConfigured()) {
        throw EssentiaException("AudioLoader: Trying to call compute() on an "
                                "AudioLoader algo which hasn't been correctly configured.");
    }

    Real& sampleRate = _sampleRate.get();
    int& numberChannels = _channels.get();
    string& md5 = _md5.get();
    int& bit_rate = _bit_rate.get();
    string& codec = _codec.get();
    vector<StereoSample>& audio = _audio.get();

    _audioStorage->setVector(&audio);
    // TODO: is using VectorInput indeed faster than using Pool?

    // FIXME:
    // _audio.reserve(sth_meaningful);

    _network->run();

    sampleRate = _pool.value<Real>("internal.sampleRate");
    numberChannels = (int) _pool.value<Real>("internal.numberChannels");
    md5 = _pool.value<std::string>("internal.md5");
    bit_rate = (int) _pool.value<Real>("internal.bit_rate");
    codec = _pool.value<std::string>("internal.codec");

    // reset, so it is ready to load audio again
    reset();
}

void AudioLoader::reset() {
    _network->reset();
    _pool.remove("internal.md5");
    _pool.remove("internal.sampleRate");
    _pool.remove("internal.numberChannels");
    _pool.remove("internal.codec");
    _pool.remove("internal.bit_rate");
}

} // namespace standard
} // namespace essentia
