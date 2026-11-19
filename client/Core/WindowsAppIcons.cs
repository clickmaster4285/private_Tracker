using System.IO.Compression;
using System.Runtime.InteropServices;

namespace client.Core;

/// <summary>
/// App icons for the admin allowlist picker (Windows only). No System.Drawing and no
/// PowerShell (PowerShell is SYSTEM-only under app lockdown): an EXE's own icon is read
/// with ExtractIconEx → GetDIBits and encoded to a small PNG in-process; a packaged app's
/// logo is already a PNG on disk. Every failure returns an empty string — an icon is
/// cosmetic and must never break the inventory scan.
/// </summary>
internal static class WindowsAppIcons
{
    /// <summary>Server cap for a stored icon (decoded bytes).</summary>
    public const int MaxIconBytes = 64 * 1024;

    /// <summary>Base64 PNG of the EXE's first icon (32×32), or "".</summary>
    public static string FromExe(string? exePath)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(exePath))
            return string.Empty;
        try
        {
            var path = exePath.Trim().Trim('"');
            if (!File.Exists(path))
                return string.Empty;
            var large = new IntPtr[1];
            if (ExtractIconEx(path, 0, large, null, 1) < 1 || large[0] == IntPtr.Zero)
                return string.Empty;
            try
            {
                var png = IconToPng(large[0]);
                return png is { Length: > 0 and <= MaxIconBytes } ? Convert.ToBase64String(png) : string.Empty;
            }
            finally
            {
                DestroyIcon(large[0]);
            }
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>Base64 of a PNG file already on disk (packaged-app logo), or "".</summary>
    public static string FromPngFile(string? pngPath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(pngPath) || !File.Exists(pngPath))
                return string.Empty;
            var info = new FileInfo(pngPath);
            if (info.Length < 8 || info.Length > MaxIconBytes)
                return string.Empty;
            var bytes = File.ReadAllBytes(pngPath);
            return IsPng(bytes) ? Convert.ToBase64String(bytes) : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    internal static bool IsPng(byte[] b) =>
        b.Length >= 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47 &&
        b[4] == 0x0D && b[5] == 0x0A && b[6] == 0x1A && b[7] == 0x0A;

    // ── HICON → RGBA → PNG ────────────────────────────────────────────────

    private static byte[]? IconToPng(IntPtr hIcon)
    {
        if (!GetIconInfo(hIcon, out var ii))
            return null;
        try
        {
            if (ii.hbmColor == IntPtr.Zero)
                return null; // monochrome icon — not worth drawing
            if (GetObject(ii.hbmColor, Marshal.SizeOf<BITMAP>(), out var bm) == 0)
                return null;
            int w = bm.bmWidth, h = Math.Abs(bm.bmHeight);
            if (w is <= 0 or > 256 || h is <= 0 or > 256)
                return null;

            var bgra = ReadBits(ii.hbmColor, w, h);
            if (bgra is null)
                return null;

            // 24-bit era icons carry no alpha: take transparency from the AND mask.
            var anyAlpha = false;
            for (var i = 3; i < bgra.Length; i += 4)
            {
                if (bgra[i] != 0) { anyAlpha = true; break; }
            }
            if (!anyAlpha)
            {
                var mask = ii.hbmMask != IntPtr.Zero ? ReadBits(ii.hbmMask, w, h) : null;
                for (var p = 0; p < w * h; p++)
                {
                    var transparent = mask is not null && mask[p * 4] != 0; // white = transparent
                    bgra[p * 4 + 3] = transparent ? (byte)0 : (byte)255;
                }
            }

            return EncodePng(bgra, w, h);
        }
        finally
        {
            if (ii.hbmColor != IntPtr.Zero) DeleteObject(ii.hbmColor);
            if (ii.hbmMask != IntPtr.Zero) DeleteObject(ii.hbmMask);
        }
    }

    private static byte[]? ReadBits(IntPtr hbm, int w, int h)
    {
        var hdc = GetDC(IntPtr.Zero);
        if (hdc == IntPtr.Zero)
            return null;
        try
        {
            var bi = new BITMAPINFOHEADER
            {
                biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = w,
                biHeight = -h, // top-down rows
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0, // BI_RGB
            };
            var buf = new byte[w * h * 4];
            var lines = GetDIBits(hdc, hbm, 0, (uint)h, buf, ref bi, 0);
            return lines == h ? buf : null;
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, hdc);
        }
    }

    /// <summary>Minimal RGBA8 PNG encoder (filter 0 per row, zlib IDAT).</summary>
    internal static byte[] EncodePng(byte[] bgra, int w, int h)
    {
        var raw = new byte[(w * 4 + 1) * h];
        for (var y = 0; y < h; y++)
        {
            var row = y * (w * 4 + 1);
            raw[row] = 0;
            for (var x = 0; x < w; x++)
            {
                var s = (y * w + x) * 4;
                var d = row + 1 + x * 4;
                raw[d] = bgra[s + 2];     // R
                raw[d + 1] = bgra[s + 1]; // G
                raw[d + 2] = bgra[s];     // B
                raw[d + 3] = bgra[s + 3]; // A
            }
        }

        byte[] idat;
        using (var ms = new MemoryStream())
        {
            using (var z = new ZLibStream(ms, CompressionLevel.SmallestSize, leaveOpen: true))
                z.Write(raw, 0, raw.Length);
            idat = ms.ToArray();
        }

        using var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var ihdr = new byte[13];
        WriteBe(ihdr, 0, (uint)w);
        WriteBe(ihdr, 4, (uint)h);
        ihdr[8] = 8;  // bit depth
        ihdr[9] = 6;  // RGBA
        WriteChunk(png, "IHDR", ihdr);
        WriteChunk(png, "IDAT", idat);
        WriteChunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void WriteChunk(Stream s, string type, byte[] data)
    {
        var len = new byte[4];
        WriteBe(len, 0, (uint)data.Length);
        s.Write(len);
        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        s.Write(typeBytes);
        s.Write(data);
        var crc = Crc32(typeBytes, data);
        var c = new byte[4];
        WriteBe(c, 0, crc);
        s.Write(c);
    }

    private static void WriteBe(byte[] b, int o, uint v)
    {
        b[o] = (byte)(v >> 24);
        b[o + 1] = (byte)(v >> 16);
        b[o + 2] = (byte)(v >> 8);
        b[o + 3] = (byte)v;
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[n] = c;
        }
        return t;
    }

    private static uint Crc32(byte[] a, byte[] b)
    {
        var c = 0xFFFFFFFFu;
        foreach (var x in a) c = CrcTable[(c ^ x) & 0xFF] ^ (c >> 8);
        foreach (var x in b) c = CrcTable[(c ^ x) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }

    // ── Win32 ─────────────────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        public bool fIcon;
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int bmType;
        public int bmWidth;
        public int bmHeight;
        public int bmWidthBytes;
        public ushort bmPlanes;
        public ushort bmBitsPixel;
        public IntPtr bmBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
        // BITMAPINFO.bmiColors is unused for 32-bpp BI_RGB; pad for GetDIBits safety.
        public uint pad0, pad1, pad2, pad3;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(string file, int index, IntPtr[]? large, IntPtr[]? small, uint count);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll")]
    private static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO info);

    [DllImport("gdi32.dll")]
    private static extern int GetObject(IntPtr h, int size, out BITMAP bm);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(IntPtr hdc, IntPtr hbm, uint start, uint lines, byte[] bits, ref BITMAPINFOHEADER bi, uint usage);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr h);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
}
