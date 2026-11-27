namespace GameEvent.Web.Tests.Files;

/// <summary>
/// A large animated GIF made fast and to a known size: every pixel is its own 9-bit LZW code (a clear code keeps the
/// code width at 9 bits), so the file grows by about 1.13 bytes a pixel whatever the picture. A real encoder spends
/// seconds quantising megapixels; the decoder reads this one like any other GIF.
/// </summary>
internal static class PlainGif
{
    // After a clear code each code adds an entry from 258 up; 9 bits hold codes up to 511
    private const int CodesPerClear = 250;

    public static byte[] Make(int width, int height, int frames)
    {
        using var gif = new MemoryStream();
        gif.Write("GIF89a"u8);
        WriteShort(gif, width);
        WriteShort(gif, height);
        gif.Write([0xF7, 0, 0]); // a global colour table of 256 entries
        for (var i = 0; i < 256; i++)
        {
            gif.Write([(byte)i, (byte)(255 - i), (byte)(i * 7)]);
        }

        // Loop forever
        gif.Write([0x21, 0xFF, 11]);
        gif.Write("NETSCAPE2.0"u8);
        gif.Write([3, 1, 0, 0, 0]);
        for (var frame = 0; frame < frames; frame++)
        {
            gif.Write([0x21, 0xF9, 4, 0, 10, 0, 0, 0]); // 100 ms a frame
            gif.WriteByte(0x2C);
            WriteShort(gif, 0);
            WriteShort(gif, 0);
            WriteShort(gif, width);
            WriteShort(gif, height);
            gif.WriteByte(0);
            gif.WriteByte(8); // the LZW minimum code size
            WriteSubBlocks(gif, Lzw(width, height, frame));
        }

        gif.WriteByte(0x3B);
        return gif.ToArray();
    }

    private static byte[] Lzw(int width, int height, int frame)
    {
        var bits = new BitWriter();
        var pixels = (long)width * height;
        for (long i = 0; i < pixels; i++)
        {
            if (i % CodesPerClear == 0)
            {
                bits.Write(256);
            }

            var x = (int)(i % width);
            var y = (int)(i / width);
            bits.Write((x + y + (frame * 13)) & 0xFF);
        }

        bits.Write(257);
        return bits.ToArray();
    }

    private static void WriteSubBlocks(MemoryStream gif, byte[] data)
    {
        for (var at = 0; at < data.Length; at += 255)
        {
            var size = Math.Min(255, data.Length - at);
            gif.WriteByte((byte)size);
            gif.Write(data, at, size);
        }

        gif.WriteByte(0);
    }

    private static void WriteShort(MemoryStream gif, int value)
    {
        gif.WriteByte((byte)(value & 0xFF));
        gif.WriteByte((byte)(value >> 8));
    }

    private sealed class BitWriter
    {
        private readonly List<byte> _bytes = [];
        private int _buffer;
        private int _count;

        public void Write(int code)
        {
            _buffer |= code << _count;
            _count += 9;
            while (_count >= 8)
            {
                _bytes.Add((byte)(_buffer & 0xFF));
                _buffer >>= 8;
                _count -= 8;
            }
        }

        public byte[] ToArray()
        {
            if (_count > 0)
            {
                _bytes.Add((byte)(_buffer & 0xFF));
            }

            return [.. _bytes];
        }
    }
}
