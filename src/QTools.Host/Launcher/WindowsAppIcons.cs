using System.IO.Compression;
using System.Runtime.InteropServices;
using QTools.Host.Logging;

namespace QTools.Host.Launcher;

/// <summary>
/// 提取本机程序的真实图标（Windows：shell32 SHGetFileInfo → GDI 位图 → 自编码 PNG）。
/// 手动启动项缓存为 apps/{id}/icon.png，索引程序缓存为 data/icon-cache/{id}.png；
/// 任何一步失败都静默回退（前端用字母头像），绝不影响启动器可用性。
/// 非 Windows 平台直接返回 false（代码未真机验证，不假装支持）。
/// </summary>
internal static class WindowsAppIcons
{
    private const uint SHGFI_ICON = 0x100;
    private const uint SHGFI_LARGEICON = 0x0;
    private const uint DIB_RGB_COLORS = 0;

    /// <summary>确保启动项目录里有 icon.png；返回文件是否可用。</summary>
    public static bool EnsureIcon(LoadedApp app)
        => EnsureIconAt(app.Entry.Target, Path.Combine(app.Directory, "icon.png"));

    /// <summary>提取目标程序图标到指定缓存路径（索引程序用 data/icon-cache/）；已存在直接复用。</summary>
    public static bool EnsureIconAt(string target, string iconPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        if (File.Exists(iconPath))
        {
            return true;
        }

        try
        {
            var pixels = ExtractRgba(target, out var width, out var height);
            if (pixels is null || width <= 0 || height <= 0)
            {
                return false;
            }

            if (Path.GetDirectoryName(iconPath) is { Length: > 0 } directory)
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllBytes(iconPath, PngEncoder.Encode(pixels, width, height));
            return true;
        }
        catch (Exception ex)
        {
            Log.Debug($"提取程序图标失败 {target}：{ex.Message}");
            return false;
        }
    }

    /// <summary>取目标路径的大图标，转成 RGBA 像素（BGRA→RGBA，无 alpha 的老图标用 AND 掩码补透明）。</summary>
    private static byte[]? ExtractRgba(string target, out int width, out int height)
    {
        width = 0;
        height = 0;

        var info = new SHFILEINFO();
        var result = SHGetFileInfo(target, 0, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), SHGFI_ICON | SHGFI_LARGEICON);
        if (result == 0 || info.hIcon == IntPtr.Zero)
        {
            return null;
        }

        var icon = info.hIcon;
        try
        {
            if (!GetIconInfo(icon, out var iconInfo))
            {
                return null;
            }

            var colorBitmap = iconInfo.hbmColor;
            var maskBitmap = iconInfo.hbmMask;
            try
            {
                if (colorBitmap == IntPtr.Zero)
                {
                    return null; // 单色图标：回退字母头像
                }

                if (GetObject(colorBitmap, Marshal.SizeOf<BITMAP>(), out var bitmap) == 0 ||
                    bitmap.bmWidth <= 0 || bitmap.bmHeight <= 0 || bitmap.bmWidth > 256 || bitmap.bmHeight > 256)
                {
                    return null;
                }

                width = bitmap.bmWidth;
                height = bitmap.bmHeight;

                var screenDc = GetDC(IntPtr.Zero);
                if (screenDc == IntPtr.Zero)
                {
                    return null;
                }

                try
                {
                    var pixels = ReadBitmap32(screenDc, colorBitmap, width, height);
                    if (pixels is null)
                    {
                        return null;
                    }

                    // BGRA → RGBA；整图 alpha 全 0 说明是不带透明通道的老图标，用 AND 掩码还原透明
                    var hasAlpha = false;
                    for (var i = 3; i < pixels.Length; i += 4)
                    {
                        if (pixels[i] != 0) { hasAlpha = true; break; }
                    }

                    if (!hasAlpha && maskBitmap != IntPtr.Zero)
                    {
                        var mask = ReadBitmap32(screenDc, maskBitmap, width, height);
                        if (mask is not null)
                        {
                            for (var p = 0; p < width * height; p++)
                            {
                                pixels[p * 4 + 3] = mask[p * 4] != 0 ? (byte)0 : (byte)255;
                            }
                        }
                    }
                    else if (!hasAlpha)
                    {
                        for (var i = 3; i < pixels.Length; i += 4)
                        {
                            pixels[i] = 255;
                        }
                    }

                    for (var p = 0; p < width * height; p++)
                    {
                        var offset = p * 4;
                        (pixels[offset], pixels[offset + 2]) = (pixels[offset + 2], pixels[offset]);
                    }

                    return pixels;
                }
                finally
                {
                    ReleaseDC(IntPtr.Zero, screenDc);
                }
            }
            finally
            {
                if (colorBitmap != IntPtr.Zero) { DeleteObject(colorBitmap); }
                if (maskBitmap != IntPtr.Zero) { DeleteObject(maskBitmap); }
            }
        }
        finally
        {
            DestroyIcon(icon);
        }
    }

    private static byte[]? ReadBitmap32(IntPtr dc, IntPtr bitmap, int width, int height)
    {
        var bmi = new BITMAPINFO
        {
            bmiHeader = new BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = width,
                biHeight = -height, // 负值 = 自上而下
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0,
            },
        };

        var pixels = new byte[width * height * 4];
        var rows = GetDIBits(dc, bitmap, 0, (uint)height, pixels, ref bmi, DIB_RGB_COLORS);
        return rows == height ? pixels : null;
    }

    // ---------------- P/Invoke ----------------

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        public int fIcon;
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
        public uint biSize;
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
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        public int bmiColors;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO piconinfo);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern int GetObject(IntPtr hgdiobj, int cbBuffer, out BITMAP lpvObject);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern int GetDIBits(IntPtr hdc, IntPtr hbmp, uint uStartScan, uint cScanLines, byte[] lpvBits, ref BITMAPINFO lpbi, uint uUsage);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
}

/// <summary>最小 PNG 编码器（RGBA8、无滤镜、Deflate 压缩）——不依赖 System.Drawing，全平台可运行。</summary>
internal static class PngEncoder
{
    private static readonly uint[] CrcTable = BuildCrcTable();

    public static byte[] Encode(byte[] rgba, int width, int height)
    {
        using var output = new MemoryStream();
        output.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

        var ihdr = new byte[13];
        WriteInt32BE(ihdr, 0, width);
        WriteInt32BE(ihdr, 4, height);
        ihdr[8] = 8;  // 位深
        ihdr[9] = 6;  // RGBA
        WriteChunk(output, "IHDR", ihdr);

        // 原始扫描线：每行前置滤镜字节 0
        var raw = new byte[height * (1 + width * 4)];
        for (var y = 0; y < height; y++)
        {
            raw[y * (1 + width * 4)] = 0;
            Buffer.BlockCopy(rgba, y * width * 4, raw, y * (1 + width * 4) + 1, width * 4);
        }

        using (var compressed = new MemoryStream())
        {
            compressed.WriteByte(0x78);
            compressed.WriteByte(0x01);
            using (var deflate = new DeflateStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            {
                deflate.Write(raw);
            }

            var adler = Adler32(raw);
            compressed.WriteByte((byte)(adler >> 24));
            compressed.WriteByte((byte)(adler >> 16));
            compressed.WriteByte((byte)(adler >> 8));
            compressed.WriteByte((byte)adler);
            WriteChunk(output, "IDAT", compressed.ToArray());
        }

        WriteChunk(output, "IEND", []);
        return output.ToArray();
    }

    private static void WriteChunk(Stream output, string type, byte[] data)
    {
        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        var length = new byte[4];
        WriteInt32BE(length, 0, data.Length);
        output.Write(length);
        output.Write(typeBytes);
        output.Write(data);

        var crc = Crc32(typeBytes, data);
        var crcBytes = new byte[4];
        WriteInt32BE(crcBytes, 0, unchecked((int)crc));
        output.Write(crcBytes);
    }

    private static void WriteInt32BE(byte[] buffer, int offset, int value)
    {
        buffer[offset] = (byte)(value >> 24);
        buffer[offset + 1] = (byte)(value >> 16);
        buffer[offset + 2] = (byte)(value >> 8);
        buffer[offset + 3] = (byte)value;
    }

    private static uint Adler32(byte[] data)
    {
        uint a = 1, b = 0;
        foreach (var value in data)
        {
            a = (a + value) % 65521;
            b = (b + a) % 65521;
        }

        return (b << 16) | a;
    }

    private static uint Crc32(byte[] type, byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var value in type)
        {
            crc = CrcTable[(crc ^ value) & 0xFF] ^ (crc >> 8);
        }

        foreach (var value in data)
        {
            crc = CrcTable[(crc ^ value) & 0xFF] ^ (crc >> 8);
        }

        return crc ^ 0xFFFFFFFF;
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }
}