using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Vector.Services;

internal enum ClipboardReadStatus
{
    Ok,
    Empty,
    Busy,
    Failed
}

internal enum ClipboardPayloadKind
{
    None,
    Bgra,
    Png
}

internal readonly record struct ClipboardPayload(
    ClipboardReadStatus Status,
    ClipboardPayloadKind Kind,
    string Detail,
    byte[]? Data,
    int Width,
    int Height)
{
    public static ClipboardPayload Empty() => new(ClipboardReadStatus.Empty, ClipboardPayloadKind.None, "", null, 0, 0);
    public static ClipboardPayload Busy() => new(ClipboardReadStatus.Busy, ClipboardPayloadKind.None, "", null, 0, 0);
    public static ClipboardPayload Failed(string detail) => new(ClipboardReadStatus.Failed, ClipboardPayloadKind.None, detail, null, 0, 0);
    public static ClipboardPayload Bgra(int width, int height, byte[] pixels) =>
        new(ClipboardReadStatus.Ok, ClipboardPayloadKind.Bgra, "", pixels, width, height);
    public static ClipboardPayload Png(byte[] png) =>
        new(ClipboardReadStatus.Ok, ClipboardPayloadKind.Png, "", png, 0, 0);
}

/// <summary>
/// 直接用 Win32 剪贴板读写图片，不经过 WPF/OLE 的 Clipboard.GetImage/SetImage。
/// 调用方必须先卸下低级鼠标钩子：钩子还挂着时，GetClipboardData 的同步渲染会卡住全系统鼠标。
/// </summary>
internal static class ClipboardImageIo
{
    internal const int MaxDimension = 8192;
    internal const int MaxBytes = 48 * 1024 * 1024;

    private const uint CfBitmap = 2;
    private const uint CfDib = 8;
    private const uint CfDibV5 = 17;
    private const uint GmemMoveable = 0x0002;
    private const uint DibRgbColors = 0;
    private const uint BiRgb = 0;

    internal static ClipboardPayload TryRead()
    {
        try
        {
            if (!TryOpen(TimeSpan.FromMilliseconds(180)))
                return ClipboardPayload.Busy();

            try
            {
                if (TryCopyFormat(CfDib, out byte[] dib) || TryCopyFormat(CfDibV5, out dib))
                {
                    if (!TryDecodeDib(dib, out int width, out int height, out byte[] pixels))
                        return ClipboardPayload.Failed("剪贴板图片格式无法识别");
                    return ClipboardPayload.Bgra(width, height, pixels);
                }

                uint png = RegisterClipboardFormat("PNG");
                if (png != 0 && TryCopyFormat(png, out byte[] pngBytes))
                    return ClipboardPayload.Png(pngBytes);

                if (TryReadHBitmap(out int bitmapWidth, out int bitmapHeight, out byte[] bitmapPixels))
                    return ClipboardPayload.Bgra(bitmapWidth, bitmapHeight, bitmapPixels);

                return ClipboardPayload.Empty();
            }
            finally
            {
                _ = CloseClipboard();
            }
        }
        catch (Exception ex)
        {
            return ClipboardPayload.Failed(ex.Message);
        }
    }

    internal static bool TryWriteDib(byte[] dib)
    {
        ArgumentNullException.ThrowIfNull(dib);
        if (dib.Length < 40 || dib.Length > MaxBytes)
            return false;
        if (!TryOpen(TimeSpan.FromMilliseconds(180)))
            return false;

        nint memory = 0;
        bool handedOff = false;
        try
        {
            if (!EmptyClipboard())
                return false;

            memory = GlobalAlloc(GmemMoveable, (nuint)dib.Length);
            if (memory == 0)
                return false;
            nint pointer = GlobalLock(memory);
            if (pointer == 0)
                return false;
            try
            {
                Marshal.Copy(dib, 0, pointer, dib.Length);
            }
            finally
            {
                _ = GlobalUnlock(memory);
            }

            if (SetClipboardData(CfDib, memory) == 0)
                return false;
            handedOff = true;
            return true;
        }
        finally
        {
            if (memory != 0 && !handedOff)
                _ = GlobalFree(memory);
            _ = CloseClipboard();
        }
    }

    internal static byte[] EncodeFromSource(BitmapSource image)
    {
        ArgumentNullException.ThrowIfNull(image);
        BitmapSource source = image;
        if (source.Format != PixelFormats.Bgra32)
        {
            source = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
            source.Freeze();
        }

        int width = source.PixelWidth;
        int height = source.PixelHeight;
        if (width <= 0 || height <= 0 || width > MaxDimension || height > MaxDimension)
            throw new InvalidOperationException("图片尺寸超出剪贴板写入范围");

        byte[] pixels = new byte[width * height * 4];
        source.CopyPixels(pixels, width * 4, 0);
        return EncodeDib(width, height, pixels);
    }

    internal static byte[] EncodeDib(int width, int height, ReadOnlySpan<byte> bgra)
    {
        if (width <= 0 || height <= 0 || width > MaxDimension || height > MaxDimension)
            throw new ArgumentOutOfRangeException(nameof(width));
        int stride = width * 4;
        if (bgra.Length < stride * height)
            throw new ArgumentException("像素缓冲区长度不足。", nameof(bgra));

        byte[] dib = new byte[40 + stride * height];
        BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(0), 40);
        BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(4), width);
        BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(8), height);
        BinaryPrimitives.WriteInt16LittleEndian(dib.AsSpan(12), 1);
        BinaryPrimitives.WriteInt16LittleEndian(dib.AsSpan(14), 32);
        for (int y = 0; y < height; y++)
        {
            int sourceY = height - 1 - y;
            bgra.Slice(sourceY * stride, stride).CopyTo(dib.AsSpan(40 + y * stride, stride));
        }
        return dib;
    }

    internal static bool TryDecodeDib(ReadOnlySpan<byte> dib, out int width, out int height, out byte[] bgra)
    {
        width = 0;
        height = 0;
        bgra = Array.Empty<byte>();
        if (dib.Length < 40)
            return false;

        int headerSize = BinaryPrimitives.ReadInt32LittleEndian(dib);
        if (headerSize < 40 || headerSize > dib.Length)
            return false;

        int signedWidth = BinaryPrimitives.ReadInt32LittleEndian(dib.Slice(4));
        int signedHeight = BinaryPrimitives.ReadInt32LittleEndian(dib.Slice(8));
        short planes = BinaryPrimitives.ReadInt16LittleEndian(dib.Slice(12));
        short bitCount = BinaryPrimitives.ReadInt16LittleEndian(dib.Slice(14));
        int compression = BinaryPrimitives.ReadInt32LittleEndian(dib.Slice(16));
        int colorsUsed = BinaryPrimitives.ReadInt32LittleEndian(dib.Slice(32));
        if (planes != 1 || signedWidth <= 0 || signedWidth > MaxDimension)
            return false;
        if (bitCount is not (24 or 32) || compression is not (0 or 3))
            return false;

        bool topDown = signedHeight < 0;
        int absHeight = Math.Abs(signedHeight);
        if (absHeight <= 0 || absHeight > MaxDimension)
            return false;

        if (compression == 3)
        {
            if (dib.Length < 52)
                return false;
            uint red = BinaryPrimitives.ReadUInt32LittleEndian(dib.Slice(40));
            uint green = BinaryPrimitives.ReadUInt32LittleEndian(dib.Slice(44));
            uint blue = BinaryPrimitives.ReadUInt32LittleEndian(dib.Slice(48));
            if (red != 0x00FF0000 || green != 0x0000FF00 || blue != 0x000000FF)
                return false;
        }

        int pixelOffset = headerSize;
        if (compression == 3 && headerSize == 40)
            pixelOffset += 12;
        if (colorsUsed > 0)
            pixelOffset += colorsUsed * 4;

        int stride = ((signedWidth * bitCount + 31) / 32) * 4;
        long needed = (long)pixelOffset + (long)stride * absHeight;
        if (pixelOffset < 40 || needed > dib.Length || needed > MaxBytes)
            return false;

        byte[] pixels = new byte[signedWidth * absHeight * 4];
        bool anyAlpha = false;
        for (int y = 0; y < absHeight; y++)
        {
            int sourceY = topDown ? y : absHeight - 1 - y;
            int source = pixelOffset + sourceY * stride;
            int destination = y * signedWidth * 4;
            if (bitCount == 32)
            {
                for (int x = 0; x < signedWidth; x++)
                {
                    pixels[destination] = dib[source];
                    pixels[destination + 1] = dib[source + 1];
                    pixels[destination + 2] = dib[source + 2];
                    byte alpha = dib[source + 3];
                    pixels[destination + 3] = alpha;
                    if (alpha != 0)
                        anyAlpha = true;
                    source += 4;
                    destination += 4;
                }
            }
            else
            {
                for (int x = 0; x < signedWidth; x++)
                {
                    pixels[destination] = dib[source];
                    pixels[destination + 1] = dib[source + 1];
                    pixels[destination + 2] = dib[source + 2];
                    pixels[destination + 3] = 255;
                    source += 3;
                    destination += 4;
                }
            }
        }

        if (bitCount == 32 && !anyAlpha)
        {
            for (int i = 3; i < pixels.Length; i += 4)
                pixels[i] = 255;
        }

        width = signedWidth;
        height = absHeight;
        bgra = pixels;
        return true;
    }

    private static bool TryOpen(TimeSpan budget)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        do
        {
            if (OpenClipboard(0))
                return true;
            if (watch.Elapsed >= budget)
                return false;
            Thread.Sleep(15);
        }
        while (true);
    }

    private static bool TryCopyFormat(uint format, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        nint handle = GetClipboardData(format);
        if (handle == 0)
            return false;
        nuint size = GlobalSize(handle);
        if (size == 0 || size > MaxBytes)
            return false;
        nint pointer = GlobalLock(handle);
        if (pointer == 0)
            return false;
        try
        {
            bytes = new byte[(int)size];
            Marshal.Copy(pointer, bytes, 0, bytes.Length);
            return true;
        }
        finally
        {
            _ = GlobalUnlock(handle);
        }
    }

    private static bool TryReadHBitmap(out int width, out int height, out byte[] bgra)
    {
        width = 0;
        height = 0;
        bgra = Array.Empty<byte>();
        nint bitmap = GetClipboardData(CfBitmap);
        if (bitmap == 0)
            return false;
        if (GetObject(bitmap, Marshal.SizeOf<NativeBitmap>(), out NativeBitmap info) == 0)
            return false;
        if (info.Width <= 0 || info.Height <= 0 || info.Width > MaxDimension || info.Height > MaxDimension)
            return false;

        int stride = info.Width * 4;
        long byteCount = (long)stride * info.Height;
        if (byteCount <= 0 || byteCount > MaxBytes)
            return false;

        byte[] pixels = new byte[byteCount];
        var header = new BitmapInfo
        {
            Header = new BitmapInfoHeader
            {
                Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
                Width = info.Width,
                Height = -info.Height,
                Planes = 1,
                BitCount = 32,
                Compression = BiRgb
            }
        };
        nint dc = CreateCompatibleDC(0);
        if (dc == 0)
            return false;
        try
        {
            int copied = GetDIBits(dc, bitmap, 0, (uint)info.Height, pixels, ref header, DibRgbColors);
            if (copied != info.Height)
                return false;
        }
        finally
        {
            _ = DeleteDC(dc);
        }

        if (!HasVisibleAlpha(pixels))
        {
            for (int i = 3; i < pixels.Length; i += 4)
                pixels[i] = 255;
        }

        width = info.Width;
        height = info.Height;
        bgra = pixels;
        return true;
    }

    private static bool HasVisibleAlpha(byte[] pixels)
    {
        for (int i = 3; i < pixels.Length; i += 4)
        {
            if (pixels[i] != 0)
                return true;
        }
        return false;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeBitmap
    {
        public int Type;
        public int Width;
        public int Height;
        public int WidthBytes;
        public ushort Planes;
        public ushort BitCount;
        public nint Bits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ColorsUsed;
        public uint ColorsImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public BitmapInfoHeader Header;
        public uint Red;
        public uint Green;
        public uint Blue;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenClipboard(nint owner);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint GetClipboardData(uint format);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetClipboardData(uint format, nint memory);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterClipboardFormat(string name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GlobalAlloc(uint flags, nuint bytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GlobalLock(nint memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(nint memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nuint GlobalSize(nint memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GlobalFree(nint memory);

    [DllImport("gdi32.dll", EntryPoint = "GetObjectW", SetLastError = true)]
    private static extern int GetObject(nint obj, int size, out NativeBitmap bitmap);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern nint CreateCompatibleDC(nint dc);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(nint dc);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern int GetDIBits(
        nint dc,
        nint bitmap,
        uint start,
        uint lines,
        [Out] byte[] bits,
        ref BitmapInfo info,
        uint usage);
}
