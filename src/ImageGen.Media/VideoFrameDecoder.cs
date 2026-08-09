using ImageGen.Domain.CodeAnalysis;
using Loxifi.FFmpeg.Helpers;
using Loxifi.FFmpeg.Native;
using Loxifi.FFmpeg.Native.Types;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace ImageGen.Media;

/// <summary>
/// First-frame extraction from a video container (mp4/webm/avi — whatever ffmpeg demuxes). Uploaded clips are stored
/// verbatim, so their poster thumbnail has to come from an actual decode; ImageSharp cannot read any video container.
///
/// <para>Runs on the same in-process ffmpeg (Loxifi.FFmpeg) as <see cref="WebpTranscoder"/>, via the raw native
/// bindings: the package ships no managed frame <em>decoder</em>, only the encoder, so the demux → decode → RGBA
/// conversion is done here directly against libavformat/libavcodec/libswscale.</para>
/// </summary>
[AllowMagicStrings("ffmpeg call-site names carried on error messages")]
internal static unsafe class VideoFrameDecoder
{
    /// <summary>ffmpeg's <c>AVFMT_FLAG_CUSTOM_IO</c> (absent from the binding's <see cref="AVFormatFlags"/>): tells
    /// avformat the AVIO context is caller-owned, so <c>avformat_close_input</c> leaves it for
    /// <see cref="StreamIOContext.Dispose"/> to free instead of double-freeing it.</summary>
    private const int AvfmtFlagCustomIo = 0x0080;

    /// <summary>Decode the first video frame of <paramref name="video"/> to an RGBA image. Throws when the bytes are
    /// not a demuxable container, carry no video stream, or no frame decodes — the caller routed here off a sniffed
    /// <c>video/*</c> MIME, so any of those is a real fault to surface, not a shape to absorb.</summary>
    public static Image<Rgba32> DecodeFirstFrame(byte[] video)
    {
        LibraryLoader.Initialize();
        using MemoryStream input = new(video, writable: false);
        using StreamIOContext io = StreamIOContext.ForReading(input);

        AVFormatContext* fmt = AVFormat.avformat_alloc_context();
        if (fmt is null)
        {
            throw new InvalidOperationException("avformat_alloc_context returned null.");
        }

        fmt->Pb = io.Context;
        fmt->Flags |= AvfmtFlagCustomIo;

        // On open failure ffmpeg frees the context and nulls the pointer itself — only a successful open leaves
        // something for the finally below to close.
        FFmpegException.ThrowIfError(AVFormat.avformat_open_input(&fmt, null, IntPtr.Zero, null), "avformat_open_input");
        try
        {
            FFmpegException.ThrowIfError(AVFormat.avformat_find_stream_info(fmt, null), "avformat_find_stream_info");

            int streamIndex = AVFormat.av_find_best_stream(fmt, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, null, 0);
            FFmpegException.ThrowIfError(streamIndex, "av_find_best_stream(video)");

            AVStream* stream = fmt->Streams[streamIndex];
            IntPtr codec = AVCodec.avcodec_find_decoder(stream->Codecpar->CodecId);
            if (codec == IntPtr.Zero)
            {
                throw new InvalidOperationException($"No decoder for codec id {stream->Codecpar->CodecId}.");
            }

            AVCodecContext* dec = AVCodec.avcodec_alloc_context3(codec);
            if (dec is null)
            {
                throw new InvalidOperationException("avcodec_alloc_context3 returned null.");
            }

            try
            {
                FFmpegException.ThrowIfError(AVCodec.avcodec_parameters_to_context(dec, stream->Codecpar), "avcodec_parameters_to_context");
                FFmpegException.ThrowIfError(AVCodec.avcodec_open2(dec, codec, null), "avcodec_open2");
                return DecodeFirstFrameFrom(fmt, dec, streamIndex);
            }
            finally
            {
                AVCodec.avcodec_free_context(&dec);
            }
        }
        finally
        {
            AVFormat.avformat_close_input(&fmt);
        }
    }

    /// <summary>The demux/decode loop: feed packets of the chosen stream until the decoder yields its first frame.</summary>
    private static Image<Rgba32> DecodeFirstFrameFrom(AVFormatContext* fmt, AVCodecContext* dec, int streamIndex)
    {
        AVPacket* pkt = AVCodec.av_packet_alloc();
        AVFrame* frame = AVUtil.av_frame_alloc();
        if (pkt is null || frame is null)
        {
            AVCodec.av_packet_free(&pkt);
            AVUtil.av_frame_free(&frame);
            throw new InvalidOperationException("av_packet_alloc/av_frame_alloc returned null.");
        }

        try
        {
            bool flushed = false;
            while (true)
            {
                int received = AVCodec.avcodec_receive_frame(dec, frame);
                if (received >= 0)
                {
                    return ToRgbaImage(frame);
                }

                if (received == AVErrors.AVERROR_EOF || (flushed && received == AVErrors.AVERROR_EAGAIN))
                {
                    throw new InvalidOperationException("The video's stream ended without a single decodable frame.");
                }

                if (received != AVErrors.AVERROR_EAGAIN)
                {
                    FFmpegException.ThrowIfError(received, "avcodec_receive_frame");
                }

                // The decoder wants more input: pump packets of our stream until one is accepted or the file ends.
                while (true)
                {
                    int read = AVFormat.av_read_frame(fmt, pkt);
                    if (read == AVErrors.AVERROR_EOF)
                    {
                        FFmpegException.ThrowIfError(AVCodec.avcodec_send_packet(dec, null), "avcodec_send_packet(flush)");
                        flushed = true;
                        break;
                    }

                    FFmpegException.ThrowIfError(read, "av_read_frame");
                    if (pkt->StreamIndex != streamIndex)
                    {
                        AVCodec.av_packet_unref(pkt);
                        continue;
                    }

                    int sent = AVCodec.avcodec_send_packet(dec, pkt);
                    AVCodec.av_packet_unref(pkt);
                    FFmpegException.ThrowIfError(sent, "avcodec_send_packet");
                    break;
                }
            }
        }
        finally
        {
            AVUtil.av_frame_free(&frame);
            AVCodec.av_packet_free(&pkt);
        }
    }

    /// <summary>Convert a decoded frame (whatever pixel format the codec produced, typically yuv420p) to RGBA via
    /// libswscale and wrap it in an ImageSharp image at the frame's native size.</summary>
    private static Image<Rgba32> ToRgbaImage(AVFrame* frame)
    {
        int w = frame->Width, h = frame->Height;
        IntPtr sws = SWScale.sws_getContext(w, h, (AVPixelFormat)frame->Format, w, h,
            AVPixelFormat.AV_PIX_FMT_RGBA, SwsFlags.SWS_BILINEAR, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (sws == IntPtr.Zero)
        {
            throw new InvalidOperationException($"sws_getContext returned null for pixel format {frame->Format}.");
        }

        try
        {
            byte[] rgba = new byte[w * h * 4];
            fixed (byte* dst = rgba)
            {
                byte** src = stackalloc byte*[8]
                {
                    (byte*)frame->Data0, (byte*)frame->Data1, (byte*)frame->Data2, (byte*)frame->Data3,
                    (byte*)frame->Data4, (byte*)frame->Data5, (byte*)frame->Data6, (byte*)frame->Data7,
                };
                byte** dstData = stackalloc byte*[4] { dst, null, null, null };
                int* dstStride = stackalloc int[4] { w * 4, 0, 0, 0 };
                int scaled = SWScale.sws_scale(sws, src, frame->Linesize, 0, h, dstData, dstStride);
                if (scaled != h)
                {
                    throw new InvalidOperationException($"sws_scale produced {scaled} rows; expected {h}.");
                }
            }

            return Image.LoadPixelData<Rgba32>(rgba, w, h);
        }
        finally
        {
            SWScale.sws_freeContext(sws);
        }
    }
}
