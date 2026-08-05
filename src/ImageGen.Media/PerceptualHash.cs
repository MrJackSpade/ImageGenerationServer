using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace ImageGen.Media;

/// <summary>Perceptual-hash (pHash) difference between two images, used to detect a silent "no-op" edit.
/// A safety-aligned editor that declines an instruction does NOT return a pixel clone — it RE-RENDERS the
/// same scene (every hair/texture regenerated, often at a different resolution). A per-pixel diff mistakes
/// that for a real change; a DCT perceptual hash compares low-frequency STRUCTURE, so a re-render reads as
/// "unchanged" while a genuine edit moves enough coefficients to cross the line.
///
/// The 64x64 reduction is a DETERMINISTIC integer-floor area-average (NOT a library resampler) so this
/// reproduces byte-for-byte across the validation harness (PIL), this server (ImageSharp) and the SPA
/// (canvas) — the 2-bit class margin is thinner than the difference between resamplers, so reproducibility
/// matters. Measured: silent declines &lt;= 0.039, smallest real edit (glasses) 0.047 — 0.043 splits them.</summary>
internal static class PerceptualHash
{
    /// <summary>Reduce each image to 64x64 luma.</summary>
    private const int N = 64;
    /// <summary>Keep the low-frequency 16x16 DCT block -> 256-bit hash.</summary>
    private const int Low = 16;

    /// <summary>Normalized Hamming distance (0 = identical structure, 1 = fully different) between the two
    /// images' perceptual hashes.</summary>
    public static double Difference(byte[] a, byte[] b)
    {
        bool[] x = PHash(a), y = PHash(b);
        int d = 0;
        for (int i = 0; i < x.Length; i++) if (x[i] != y[i]) d++;
        return d / (double)x.Length;
    }

    /// <summary>1-D DCT-II basis, cached: cos(pi*(2x+1)*k / 2N).</summary>
    private static readonly double[,] Cos = BuildCos();
    private static double[,] BuildCos()
    {
        double[,] m = new double[N, N];
        for (int k = 0; k < N; k++)
            for (int x = 0; x < N; x++)
                m[k, x] = Math.Cos(Math.PI * (2 * x + 1) * k / (2.0 * N));
        return m;
    }

    /// <summary>Maps each source index [0..len) to its N-cell bucket using floor(i*len/N) boundaries (matches PIL/JS).</summary>
    private static int[] CellMap(int len)
    {
        int[] b = new int[N + 1];
        for (int i = 0; i <= N; i++) b[i] = (int)((long)i * len / N);
        int[] map = new int[len];
        for (int i = 0; i < N; i++)
            for (int y = b[i]; y < b[i + 1]; y++) map[y] = i;
        return map;
    }

    private static bool[] PHash(byte[] png)
    {
        double[,] g = new double[N, N];
        using (Image<Rgba32> im = Image.Load<Rgba32>(png))
        {
            int H = im.Height, W = im.Width;
            int[] rowCell = CellMap(H), colCell = CellMap(W);
            double[,] sum = new double[N, N];
            int[,] cnt = new int[N, N];
            for (int y = 0; y < H; y++)
            {
                int ci = rowCell[y];
                for (int x = 0; x < W; x++)
                {
                    Rgba32 p = im[x, y];
                    int cj = colCell[x];
                    sum[ci, cj] += (p.R * 299 + p.G * 587 + p.B * 114) / 1000.0;  // ITU-R 601 luma
                    cnt[ci, cj]++;
                }
            }
            for (int i = 0; i < N; i++)
                for (int j = 0; j < N; j++)
                    g[i, j] = cnt[i, j] > 0 ? sum[i, j] / cnt[i, j] : 0;
        }
        // separable DCT-II: rows then columns; keep the top-left Low x Low block
        double[,] tmp = new double[N, N];
        for (int r = 0; r < N; r++)
            for (int k = 0; k < N; k++)
            {
                double s = 0;
                for (int x = 0; x < N; x++) s += g[r, x] * Cos[k, x];
                tmp[r, k] = 2 * s;
            }
        double[] dct = new double[Low * Low];
        for (int k = 0; k < Low; k++)
            for (int col = 0; col < Low; col++)
            {
                double s = 0;
                for (int y = 0; y < N; y++) s += tmp[y, col] * Cos[k, y];
                dct[k * Low + col] = 2 * s;
            }
        // median of all but the DC coefficient
        double[] sorted = new double[dct.Length - 1];
        Array.Copy(dct, 1, sorted, 0, dct.Length - 1);
        Array.Sort(sorted);
        int n = sorted.Length;
        double med = (n % 2 == 1) ? sorted[n / 2] : (sorted[n / 2 - 1] + sorted[n / 2]) / 2.0;
        bool[] bits = new bool[dct.Length];
        for (int i = 0; i < dct.Length; i++) bits[i] = dct[i] > med;
        return bits;
    }
}
