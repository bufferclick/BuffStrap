using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace BuffStrap.Cursors;

/// <summary>An RGBA image decoded from a Windows cursor file.</summary>
public sealed class CursorImage
{
    public int Width { get; }
    public int Height { get; }
    public byte[] Rgba { get; }

    /// <summary>The click point inside the image, in source pixels.</summary>
    public int HotspotX { get; }
    public int HotspotY { get; }

    public CursorImage(int width, int height, byte[] rgba, int hotspotX = 0, int hotspotY = 0)
    {
        if (width <= 0 || height <= 0 || rgba.Length != width * height * 4)
            throw new ArgumentException("The decoded cursor image is invalid.");
        Width = width;
        Height = height;
        Rgba = rgba;
        HotspotX = Math.Clamp(hotspotX, 0, width - 1);
        HotspotY = Math.Clamp(hotspotY, 0, height - 1);
    }

    /// <summary>
    /// Distance in pixels from the hotspot to the furthest opaque pixel.
    /// </summary>
    public int PointerExtent()
    {
        int extent = 0;
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                if (Rgba[(y * Width + x) * 4 + 3] == 0) continue;
                extent = Math.Max(extent, Math.Max(Math.Abs(x - HotspotX), Math.Abs(y - HotspotY)));
            }
        }
        return extent;
    }

    /// <summary>
    /// Prepares a Roblox pointer texture. Roblox draws the image at its own pixel
    /// size and treats the middle of the texture as the click point, which is why
    /// its own cursor art sits centred with the arrow tip in the middle of the
    /// canvas. So the artwork keeps its native size (a 32px Windows cursor stays
    /// 32px instead of being blown up) and the hotspot is placed exactly in the
    /// middle. Only oversized art is scaled down.
    /// </summary>
    public CursorImage ToPointer(int maximumSize)
    {
        if (maximumSize <= 0) throw new ArgumentOutOfRangeException(nameof(maximumSize));

        int extent = Math.Max(1, PointerExtent());
        int canvas = Math.Max(64, extent * 2);
        double scale = 1.0;

        if (canvas > maximumSize)
        {
            scale = (double)maximumSize / canvas;
            canvas = maximumSize;
        }

        double half = canvas / 2.0;
        var output = new byte[canvas * canvas * 4];

        for (int y = 0; y < canvas; y++)
        {
            int sourceY = (int)Math.Floor((y + 0.5 - half) / scale + HotspotY);
            if (sourceY < 0 || sourceY >= Height) continue;

            for (int x = 0; x < canvas; x++)
            {
                int sourceX = (int)Math.Floor((x + 0.5 - half) / scale + HotspotX);
                if (sourceX < 0 || sourceX >= Width) continue;

                int from = (sourceY * Width + sourceX) * 4;
                int to = (y * canvas + x) * 4;
                output[to] = Rgba[from];
                output[to + 1] = Rgba[from + 1];
                output[to + 2] = Rgba[from + 2];
                output[to + 3] = Rgba[from + 3];
            }
        }

        return new CursorImage(canvas, canvas, output);
    }

    /// <summary>
    /// Nearest-neighbour resize. Cursor artwork is pixel art, so keeping the
    /// original pixels crisp is better than smoothing them.
    /// </summary>
    public CursorImage Resize(int size)
    {
        if (size <= 0) throw new ArgumentOutOfRangeException(nameof(size));
        var output = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
        {
            int sourceY = y * Height / size;
            for (int x = 0; x < size; x++)
            {
                int sourceX = x * Width / size;
                int from = (sourceY * Width + sourceX) * 4;
                int to = (y * size + x) * 4;
                output[to] = Rgba[from];
                output[to + 1] = Rgba[from + 1];
                output[to + 2] = Rgba[from + 2];
                output[to + 3] = Rgba[from + 3];
            }
        }
        return new CursorImage(size, size, output);
    }
}

/// <summary>
/// Reads Windows cursor files (.cur/.ani, and the icon container they use) into
/// plain RGBA data. Only public file formats are handled; nothing is executed.
/// </summary>
public static class CursorImageReader
{
    private const int MaximumImageSize = 512;

    public static CursorImage Read(byte[] data)
    {
        if (data is null || data.Length < 6)
            throw new InvalidDataException("The cursor file is empty or incomplete.");

        if (IsAnimated(data))
            return ReadAnimated(data);

        return ReadIconFile(data, 0, data.Length);
    }

    private static bool IsAnimated(byte[] data) =>
        data.Length >= 12 && data[0] == 'R' && data[1] == 'I' && data[2] == 'F' && data[3] == 'F' &&
        data[8] == 'A' && data[9] == 'C' && data[10] == 'O' && data[11] == 'N';

    private static CursorImage ReadAnimated(byte[] data)
    {
        // RIFF/ACON: an 'anih' header and a LIST of 'fram' chunks, each holding a
        // complete icon/cursor file. The first frame is the resting cursor art.
        int offset = 12;
        while (offset + 8 <= data.Length)
        {
            string id = Encoding.ASCII.GetString(data, offset, 4);
            int size = BitConverter.ToInt32(data, offset + 4);
            if (size < 0 || offset + 8 + size > data.Length)
                throw new InvalidDataException("The animated cursor is malformed.");

            if (id == "LIST" && size >= 4)
            {
                string listType = Encoding.ASCII.GetString(data, offset + 8, 4);
                if (listType == "fram")
                {
                    int inner = offset + 12;
                    int end = offset + 8 + size;
                    while (inner + 8 <= end)
                    {
                        string frameId = Encoding.ASCII.GetString(data, inner, 4);
                        int frameSize = BitConverter.ToInt32(data, inner + 4);
                        if (frameSize < 0 || inner + 8 + frameSize > end)
                            throw new InvalidDataException("The animated cursor frame is malformed.");
                        if (frameId == "icon")
                        {
                            var frame = new byte[frameSize];
                            Array.Copy(data, inner + 8, frame, 0, frameSize);
                            return ReadIconFile(frame, 0, frame.Length);
                        }
                        inner += 8 + frameSize + (frameSize & 1);
                    }
                }
            }

            offset += 8 + size + (size & 1);
        }

        throw new InvalidDataException("The animated cursor has no usable frame.");
    }

    /// <summary>Reads an ICO or CUR container and returns its largest image.</summary>
    private static CursorImage ReadIconFile(byte[] data, int start, int length)
    {
        if (length < 6)
            throw new InvalidDataException("The cursor file is incomplete.");

        int type = BitConverter.ToUInt16(data, start + 2);
        int count = BitConverter.ToUInt16(data, start + 4);
        if ((type != 1 && type != 2) || count < 1 || count > 512 || start + 6 + count * 16 > length)
            throw new InvalidDataException("The file is not a Windows cursor or icon.");

        long bestArea = 0;
        int bestOffset = -1, bestSize = 0, bestHotspotX = 0, bestHotspotY = 0;
        bool cursor = type == 2;
        for (int index = 0; index < count; index++)
        {
            int entry = start + 6 + index * 16;
            int width = data[entry] == 0 ? 256 : data[entry];
            int height = data[entry + 1] == 0 ? 256 : data[entry + 1];
            int hotspotX = cursor ? BitConverter.ToUInt16(data, entry + 4) : 0;
            int hotspotY = cursor ? BitConverter.ToUInt16(data, entry + 6) : 0;
            int size = BitConverter.ToInt32(data, entry + 8);
            int offset = BitConverter.ToInt32(data, entry + 12);
            if (size <= 0 || offset < 0 || start + offset + size > length)
                continue;
            long area = (long)width * height;
            if (area > bestArea)
            {
                bestArea = area;
                bestOffset = start + offset;
                bestSize = size;
                bestHotspotX = hotspotX;
                bestHotspotY = hotspotY;
            }
        }

        if (bestOffset < 0)
            throw new InvalidDataException("The cursor file has no usable image.");

        var payload = new byte[bestSize];
        Array.Copy(data, bestOffset, payload, 0, bestSize);

        CursorImage image = IsPng(payload) ? ReadPng(payload) : ReadBitmap(payload);
        return new CursorImage(image.Width, image.Height, image.Rgba, bestHotspotX, bestHotspotY);
    }

    /// <summary>Decodes a standalone PNG image (used to verify exported pointers).</summary>
    public static CursorImage ReadPngImage(byte[] data)
    {
        if (!IsPng(data)) throw new InvalidDataException("The file is not a PNG image.");
        return ReadPng(data);
    }

    public static bool IsPngFile(byte[] data) =>
        data is not null && data.Length >= 8 && data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47;

    private static bool IsPng(byte[] data) =>
        data.Length > 8 && data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47;

    private static CursorImage ReadPng(byte[] data)
    {
        if (data.Length < 8 + 25 || data[12] != 'I' || data[13] != 'H' || data[14] != 'D' || data[15] != 'R')
            throw new InvalidDataException("The cursor image is not a valid PNG.");

        int width = ReadBigEndian(data, 16);
        int height = ReadBigEndian(data, 20);
        int bitDepth = data[24];
        int colorType = data[25];
        int interlace = data[28];
        if (width <= 0 || height <= 0 || width > MaximumImageSize || height > MaximumImageSize)
            throw new InvalidDataException("The cursor image has an unsupported size.");
        if (bitDepth != 8 || interlace != 0 || (colorType != 6 && colorType != 2 && colorType != 4 && colorType != 0 && colorType != 3))
            throw new InvalidDataException("The cursor image uses an unsupported PNG format.");

        byte[]? palette = null;
        using var compressed = new MemoryStream();
        int offset = 8;
        while (offset + 8 <= data.Length)
        {
            int size = ReadBigEndian(data, offset);
            string id = Encoding.ASCII.GetString(data, offset + 4, 4);
            if (size < 0 || offset + 12 + size > data.Length)
                throw new InvalidDataException("The cursor image is truncated.");
            if (id == "IDAT")
                compressed.Write(data, offset + 8, size);
            else if (id == "PLTE")
                palette = data.Skip(offset + 8).Take(size).ToArray();
            else if (id == "IEND")
                break;
            offset += 12 + size;
        }

        int channels = colorType switch { 6 => 4, 2 => 3, 4 => 2, 0 => 1, _ => 1 };
        int stride = width * channels;
        var raw = Inflate(compressed.ToArray(), (stride + 1) * height);

        var pixels = new byte[width * height * 4];
        var previous = new byte[stride];
        var line = new byte[stride];
        for (int y = 0; y < height; y++)
        {
            int rowStart = y * (stride + 1);
            byte filter = raw[rowStart];
            Array.Copy(raw, rowStart + 1, line, 0, stride);
            Unfilter(filter, line, previous, channels);
            for (int x = 0; x < width; x++)
            {
                int to = (y * width + x) * 4;
                switch (colorType)
                {
                    case 6:
                        pixels[to] = line[x * 4];
                        pixels[to + 1] = line[x * 4 + 1];
                        pixels[to + 2] = line[x * 4 + 2];
                        pixels[to + 3] = line[x * 4 + 3];
                        break;
                    case 2:
                        pixels[to] = line[x * 3];
                        pixels[to + 1] = line[x * 3 + 1];
                        pixels[to + 2] = line[x * 3 + 2];
                        pixels[to + 3] = 255;
                        break;
                    case 4:
                        pixels[to] = pixels[to + 1] = pixels[to + 2] = line[x * 2];
                        pixels[to + 3] = line[x * 2 + 1];
                        break;
                    case 0:
                        pixels[to] = pixels[to + 1] = pixels[to + 2] = line[x];
                        pixels[to + 3] = 255;
                        break;
                    default:
                        if (palette is null || line[x] * 3 + 2 >= palette.Length)
                            throw new InvalidDataException("The cursor image has an invalid palette.");
                        pixels[to] = palette[line[x] * 3];
                        pixels[to + 1] = palette[line[x] * 3 + 1];
                        pixels[to + 2] = palette[line[x] * 3 + 2];
                        pixels[to + 3] = 255;
                        break;
                }
            }
            Array.Copy(line, previous, stride);
        }

        return new CursorImage(width, height, pixels);
    }

    private static void Unfilter(byte filter, byte[] line, byte[] previous, int channels)
    {
        switch (filter)
        {
            case 0:
                break;
            case 1:
                for (int i = channels; i < line.Length; i++) line[i] += line[i - channels];
                break;
            case 2:
                for (int i = 0; i < line.Length; i++) line[i] += previous[i];
                break;
            case 3:
                for (int i = 0; i < line.Length; i++)
                    line[i] += (byte)(((i >= channels ? line[i - channels] : 0) + previous[i]) / 2);
                break;
            case 4:
                for (int i = 0; i < line.Length; i++)
                {
                    int left = i >= channels ? line[i - channels] : 0;
                    int up = previous[i];
                    int upLeft = i >= channels ? previous[i - channels] : 0;
                    int estimate = left + up - upLeft;
                    int pa = Math.Abs(estimate - left), pb = Math.Abs(estimate - up), pc = Math.Abs(estimate - upLeft);
                    line[i] += (byte)(pa <= pb && pa <= pc ? left : pb <= pc ? up : upLeft);
                }
                break;
            default:
                throw new InvalidDataException("The cursor image uses an unsupported PNG filter.");
        }
    }

    private static byte[] Inflate(byte[] compressed, int expected)
    {
        using var input = new MemoryStream(compressed);
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        var output = new byte[expected];
        int read = 0, count;
        while (read < expected && (count = zlib.Read(output, read, expected - read)) > 0)
            read += count;
        if (read != expected)
            throw new InvalidDataException("The cursor image data is incomplete.");
        return output;
    }

    /// <summary>Reads the DIB payload used by older .cur files.</summary>
    private static CursorImage ReadBitmap(byte[] data)
    {
        if (data.Length < 40)
            throw new InvalidDataException("The cursor image is incomplete.");

        int headerSize = BitConverter.ToInt32(data, 0);
        int width = BitConverter.ToInt32(data, 4);
        int doubledHeight = BitConverter.ToInt32(data, 8);
        int bitCount = BitConverter.ToUInt16(data, 14);
        int compression = BitConverter.ToInt32(data, 16);
        if (headerSize < 40 || width <= 0 || width > MaximumImageSize || doubledHeight == 0 || compression != 0)
            throw new InvalidDataException("The cursor image has an unsupported bitmap format.");

        int height = Math.Abs(doubledHeight) / 2;
        if (height <= 0 || height > MaximumImageSize)
            throw new InvalidDataException("The cursor image has an unsupported size.");
        if (bitCount != 32 && bitCount != 24 && bitCount != 8 && bitCount != 4 && bitCount != 1)
            throw new InvalidDataException("The cursor image uses an unsupported colour depth.");

        byte[]? palette = null;
        if (bitCount <= 8)
        {
            int entries = 1 << bitCount;
            int paletteOffset = 40;
            if (data.Length < paletteOffset + entries * 4)
                throw new InvalidDataException("The cursor image palette is incomplete.");
            palette = new byte[entries * 4];
            Array.Copy(data, paletteOffset, palette, 0, palette.Length);
        }

        int xorOffset = headerSize + (palette is null ? 0 : palette.Length);
        int rowSize = ((width * bitCount + 31) / 32) * 4;
        int maskRowSize = ((width + 31) / 32) * 4;
        int xorSize = rowSize * height;
        int maskOffset = xorOffset + xorSize;
        if (xorSize <= 0 || data.Length < xorOffset + xorSize)
            throw new InvalidDataException("The cursor image pixels are incomplete.");

        var pixels = new byte[width * height * 4];
        // 32-bit cursors either use their alpha channel or are fully opaque and
        // rely on the AND mask. Decide once, before the mask is applied.
        bool hasAlpha = false;
        if (bitCount == 32)
            for (int i = xorOffset + 3; i < xorOffset + xorSize; i += 4)
                if (data[i] != 0) { hasAlpha = true; break; }
        var mask = new byte[maskRowSize * height];
        bool hasMask = data.Length >= maskOffset + mask.Length;
        if (hasMask)
            Array.Copy(data, maskOffset, mask, 0, mask.Length);

        bool topDown = doubledHeight < 0;
        for (int y = 0; y < height; y++)
        {
            int sourceRow = topDown ? y : height - 1 - y;
            int rowStart = xorOffset + sourceRow * rowSize;
            for (int x = 0; x < width; x++)
            {
                byte r, g, b, a = 255;
                if (bitCount == 32)
                {
                    int i = rowStart + x * 4;
                    b = data[i]; g = data[i + 1]; r = data[i + 2];
                    a = hasAlpha ? data[i + 3] : (byte)255;
                }
                else if (bitCount == 24)
                {
                    int i = rowStart + x * 3;
                    b = data[i]; g = data[i + 1]; r = data[i + 2];
                }
                else
                {
                    int index = ReadPaletteIndex(data, rowStart, x, bitCount);
                    if (palette is null || index * 4 + 2 >= palette.Length)
                        throw new InvalidDataException("The cursor image has an invalid palette.");
                    b = palette[index * 4]; g = palette[index * 4 + 1]; r = palette[index * 4 + 2];
                }

                // Windows ignores the AND mask when a 32-bit cursor carries a real
                // alpha channel; otherwise the mask is the only transparency.
                if (hasMask && !hasAlpha)
                {
                    int maskRow = topDown ? y : height - 1 - y;
                    int bit = mask[maskRow * maskRowSize + x / 8] & (0x80 >> (x % 8));
                    if (bit != 0)
                        a = 0;
                }

                int to = (y * width + x) * 4;
                pixels[to] = r; pixels[to + 1] = g; pixels[to + 2] = b; pixels[to + 3] = a;
            }
        }

        return new CursorImage(width, height, pixels);
    }

    private static int ReadPaletteIndex(byte[] data, int rowStart, int x, int bitCount)
    {
        switch (bitCount)
        {
            case 8:
                return data[rowStart + x];
            case 4:
                return (data[rowStart + x / 2] >> (x % 2 == 0 ? 4 : 0)) & 0x0F;
            default:
                return (data[rowStart + x / 8] >> (7 - x % 8)) & 1;
        }
    }

    private static int ReadBigEndian(byte[] data, int offset) =>
        (data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3];

    /// <summary>Writes a plain 8-bit RGBA PNG. No external image library needed.</summary>
    public static byte[] ToPng(CursorImage image)
    {
        int stride = image.Width * 4;
        var raw = new byte[(stride + 1) * image.Height];
        for (int y = 0; y < image.Height; y++)
        {
            raw[y * (stride + 1)] = 0;
            Array.Copy(image.Rgba, y * stride, raw, y * (stride + 1) + 1, stride);
        }

        byte[] compressed;
        using (var output = new MemoryStream())
        {
            using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
                zlib.Write(raw, 0, raw.Length);
            compressed = output.ToArray();
        }

        using var png = new MemoryStream();
        png.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, 0, 8);

        var header = new byte[13];
        WriteBigEndian(header, 0, image.Width);
        WriteBigEndian(header, 4, image.Height);
        header[8] = 8;   // bit depth
        header[9] = 6;   // RGBA
        header[10] = 0; header[11] = 0; header[12] = 0;
        WriteChunk(png, "IHDR", header);
        WriteChunk(png, "IDAT", compressed);
        WriteChunk(png, "IEND", Array.Empty<byte>());
        return png.ToArray();
    }

    private static void WriteChunk(Stream stream, string id, byte[] data)
    {
        var length = new byte[4];
        WriteBigEndian(length, 0, data.Length);
        stream.Write(length, 0, 4);
        var idBytes = Encoding.ASCII.GetBytes(id);
        stream.Write(idBytes, 0, 4);
        stream.Write(data, 0, data.Length);
        uint crc = Crc32.Calculate(idBytes, data);
        var crcBytes = new byte[4];
        WriteBigEndian(crcBytes, 0, unchecked((int)crc));
        stream.Write(crcBytes, 0, 4);
    }

    private static void WriteBigEndian(byte[] target, int offset, int value)
    {
        target[offset] = (byte)(value >> 24);
        target[offset + 1] = (byte)(value >> 16);
        target[offset + 2] = (byte)(value >> 8);
        target[offset + 3] = (byte)value;
    }

    private static class Crc32
    {
        private static readonly uint[] Table = BuildTable();

        private static uint[] BuildTable()
        {
            var table = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++)
                    c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                table[n] = c;
            }
            return table;
        }

        public static uint Calculate(byte[] first, byte[] second)
        {
            uint crc = 0xFFFFFFFFu;
            foreach (byte b in first) crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
            foreach (byte b in second) crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
            return crc ^ 0xFFFFFFFFu;
        }
    }
}

/// <summary>Extracts the normal-pointer cursor from a downloaded cursor package.</summary>
public static class CursorPackage
{
    /// <summary>Windows cursor schemes (.cur/.ani zips) — needs conversion.</summary>
    public const string LibraryUrl = "https://www.rw-designer.com/cursor-library";

    /// <summary>
    /// Packs that ship ready 64x64 Roblox PNGs, so no conversion and no hotspot
    /// maths is involved: what the site gives you is exactly what Roblox draws.
    /// </summary>
    public const string RobloxLibraryUrl = "https://sweezy-cursors.com/specials/custom-cursors-for-roblox/";

    /// <summary>
    /// Returns the cursor bytes for the package: a .cur/.ani file is used as-is,
    /// and a .zip (what cursor sites call an "install zip") is searched for the
    /// scheme's [Arrow] entry, then for any cursor file.
    /// </summary>
    public static byte[] ExtractArrow(byte[] package)
    {
        if (package is null || package.Length < 4)
            throw new InvalidDataException("The cursor package is empty.");

        if (package[0] == 'P' && package[1] == 'K')
            return ExtractFromZip(package);

        if (CursorImageReader.IsPngFile(package))
            return package;

        CursorImageReader.Read(package);
        return package;
    }

    private static byte[] ExtractFromZip(byte[] package)
    {
        using var stream = new MemoryStream(package);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        var entries = zip.Entries.Where(x => x.Length > 0).ToList();
        if (entries.Count == 0)
            throw new InvalidDataException("The cursor package is empty.");

        string? arrow = null;
        foreach (var inf in entries.Where(x => x.Name.EndsWith(".inf", StringComparison.OrdinalIgnoreCase) ||
                                               x.Name.EndsWith(".ini", StringComparison.OrdinalIgnoreCase)))
            arrow = ReadArrowPath(ReadText(inf, 256 * 1024)) ?? arrow;

        if (arrow is not null)
        {
            var match = entries.FirstOrDefault(x => x.FullName.Replace('\\', '/')
                .EndsWith(arrow.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                var bytes = ReadAll(match, 4 * 1024 * 1024);
                if (IsUsable(bytes)) return bytes;
            }
        }

        // No scheme file. Roblox-ready packs ship ArrowCursor.png, so a PNG named
        // like the stock texture wins; otherwise prefer an obvious default pointer.
        var candidates = entries
            .Where(x => x.Name.EndsWith(".cur", StringComparison.OrdinalIgnoreCase) ||
                        x.Name.EndsWith(".ani", StringComparison.OrdinalIgnoreCase) ||
                        x.Name.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(x => x.Name.Equals("ArrowCursor.png", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(x => x.Name.Equals("ArrowFarCursor.png", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(x => x.Name.Contains("arrow", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(x => x.Name.Contains("normal", StringComparison.OrdinalIgnoreCase) ||
                                    x.Name.Contains("pointer", StringComparison.OrdinalIgnoreCase) ||
                                    x.Name.Contains("select", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(x => x.Length)
            .ToList();

        foreach (var candidate in candidates)
        {
            var bytes = ReadAll(candidate, 4 * 1024 * 1024);
            if (IsUsable(bytes)) return bytes;
        }

        throw new InvalidDataException("The package does not contain a usable cursor.");
    }

    /// <summary>Reads "Path=..." from the [Arrow] section of an install.inf.</summary>
    public static string? ReadArrowPath(string text)
    {
        string? section = null;
        string? firstCursor = null;
        foreach (var rawLine in text.Split('\n'))
        {
            string line = rawLine.Trim().TrimEnd('\r');
            if (line.StartsWith("[", StringComparison.Ordinal) && line.EndsWith("]", StringComparison.Ordinal))
            {
                section = line[1..^1].Trim();
                continue;
            }

            int equals = line.IndexOf('=');
            if (equals <= 0) continue;
            string key = line[..equals].Trim();
            string value = line[(equals + 1)..].Trim().Trim('"');
            if (value.Length == 0) continue;
            if (!value.EndsWith(".cur", StringComparison.OrdinalIgnoreCase) &&
                !value.EndsWith(".ani", StringComparison.OrdinalIgnoreCase)) continue;

            if (String.Equals(section, "Arrow", StringComparison.OrdinalIgnoreCase) &&
                key.Equals("Path", StringComparison.OrdinalIgnoreCase))
                return value;

            firstCursor ??= value;
        }

        return firstCursor;
    }

    private static string ReadText(ZipArchiveEntry entry, long limit) =>
        Encoding.UTF8.GetString(ReadAll(entry, limit));

    private static byte[] ReadAll(ZipArchiveEntry entry, long limit)
    {
        if (entry.Length > limit) throw new InvalidDataException("The cursor package entry is too large.");
        using var entryStream = entry.Open();
        using var memory = new MemoryStream();
        entryStream.CopyTo(memory);
        return memory.ToArray();
    }
}
