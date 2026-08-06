using ImageGen.Domain.CodeAnalysis;

namespace ImageGen.Media;

/// <summary>
/// Reads the coded pixel dimensions of an MP4 (ISO-BMFF) clip straight from its container boxes, no decode. ImageSharp
/// cannot read an mp4, and the only stored clip that isn't an animated webp is a MiniMax-H3 mp4 (video + a baked-in
/// audio track). The video track's sample entry (avc1/hvc1/av01/…) carries width/height as two 16-bit fields — the
/// authoritative coded size. Throws when the file carries no video sample entry: an output whose header will not read
/// is a FAILED render, not a 0×0 clip (the same contract ImageSharp's Identify holds for a still).
/// </summary>
internal static class Mp4Probe
{
    /// <summary>Boxes whose payload is a sequence of child boxes we recurse into to reach the sample description.</summary>
    private static readonly HashSet<string> Containers = new(StringComparer.Ordinal)
        { "moov", "trak", "mdia", "minf", "stbl", "edts", "mvex" };

    /// <summary>FourCCs of a VISUAL sample entry (VisualSampleEntry layout: width @ +32, height @ +34). Excludes audio (mp4a).</summary>
    private static readonly HashSet<string> VideoSampleEntries = new(StringComparer.Ordinal)
        { "avc1", "avc3", "hvc1", "hev1", "av01", "vp09", "vp08", "mp4v", "encv" };

    /// <summary>ISO-BMFF box FourCCs recognised by name.</summary>
    private static class Boxes
    {
        /// <summary>The sample-description box whose entries carry the visual sample entry.</summary>
        public const string StsdBox = "stsd";
    }

    [AllowMagicStrings("exception message")]
    public static (int Width, int Height) GetDimensions(ReadOnlySpan<byte> b)
    {
        if (TryFindVideoSize(b, 0, b.Length, out int w, out int h))
        {
            return (w, h);
        }

        throw new InvalidOperationException("MP4 has no readable video sample entry — cannot determine clip dimensions.");
    }

    private static bool TryFindVideoSize(ReadOnlySpan<byte> b, int start, int end, out int w, out int h)
    {
        w = h = 0;
        int pos = start;
        while (pos + 8 <= end)
        {
            long size = ReadU32(b, pos);
            int header = 8;
            if (size == 1)                              // 64-bit largesize follows the type
            {
                if (pos + 16 > end)
                {
                    break;
                }

                long large = 0;
                for (int i = 0; i < 8; i++)
                {
                    large = (large << 8) | b[pos + 8 + i];
                }

                size = large;
                header = 16;
            }
            else if (size == 0)
            {
                size = end - pos;       // last box: extends to the end of the range
            }

            if (size < header)
            {
                break;
            }

            long boxEnd = pos + size;
            if (boxEnd > end || boxEnd <= pos)
            {
                break;
            }

            string type = ReadType(b, pos + 4);
            int contentStart = pos + header;

            if (type == Boxes.StsdBox)
            {
                if (TryReadStsd(b, contentStart, (int)boxEnd, out w, out h))
                {
                    return true;
                }
            }
            else if (Containers.Contains(type))
            {
                if (TryFindVideoSize(b, contentStart, (int)boxEnd, out w, out h))
                {
                    return true;
                }
            }

            pos = (int)boxEnd;
        }

        return false;
    }

    private static bool TryReadStsd(ReadOnlySpan<byte> b, int start, int end, out int w, out int h)
    {
        w = h = 0;
        int pos = start + 8;                            // version+flags (4) + entry_count (4)
        while (pos + 8 <= end)
        {
            long size = ReadU32(b, pos);
            if (size < 8)
            {
                break;
            }

            long entryEnd = pos + size;
            if (entryEnd > end || entryEnd <= pos)
            {
                break;
            }

            string type = ReadType(b, pos + 4);
            if (VideoSampleEntries.Contains(type) && pos + 36 <= end)
            {
                w = (b[pos + 32] << 8) | b[pos + 33];   // VisualSampleEntry.width  (uint16 BE)
                h = (b[pos + 34] << 8) | b[pos + 35];   // VisualSampleEntry.height (uint16 BE)
                if (w > 0 && h > 0)
                {
                    return true;
                }
            }

            pos = (int)entryEnd;
        }

        return false;
    }

    private static long ReadU32(ReadOnlySpan<byte> b, int o) =>
        ((long)b[o] << 24) | ((long)b[o + 1] << 16) | ((long)b[o + 2] << 8) | b[o + 3];

    private static string ReadType(ReadOnlySpan<byte> b, int o) =>
        new([(char)b[o], (char)b[o + 1], (char)b[o + 2], (char)b[o + 3]]);
}