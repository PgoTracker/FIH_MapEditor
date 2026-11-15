using System;
using System.IO;
using System.IO.Compression;

namespace FIHMapEditor
{
    // Decodes the common non-interlaced 8-bit RGB/RGBA PNGs used by map packages.
    // Managed decoding avoids Unity IL2CPP's broken ImageConversion.LoadImage path.
    public sealed class PngTexture
    {
        private static readonly byte[] Signature = { 137, 80, 78, 71, 13, 10, 26, 10 };

        public int Width { get; private set; }
        public int Height { get; private set; }
        public byte[] Rgba { get; private set; }

        public static void ValidateHeader(byte[] png)
        {
            if (png == null || png.Length < 33 || png.Length > MapPackage.MaxTextureBytes)
                throw new InvalidDataException("Invalid PNG texture size.");
            for (int i = 0; i < Signature.Length; i++)
                if (png[i] != Signature[i]) throw new InvalidDataException("Texture must be a PNG.");
            if (Read32(png, 8) != 13 || !IsChunk(png, 12, "IHDR"))
                throw new InvalidDataException("PNG texture has no IHDR chunk.");
            uint width = Read32(png, 16), height = Read32(png, 20);
            if (width == 0 || height == 0 || width > MapPackage.MaxTextureDimension
                || height > MapPackage.MaxTextureDimension)
                throw new InvalidDataException("PNG texture dimensions must be 1–2048 pixels.");
            if (png[24] != 8 || (png[25] != 2 && png[25] != 6)
                || png[26] != 0 || png[27] != 0 || png[28] != 0)
                throw new InvalidDataException("PNG texture must be non-interlaced 8-bit RGB or RGBA.");
        }

        public static PngTexture Decode(byte[] png)
        {
            ValidateHeader(png);
            int width = (int)Read32(png, 16), height = (int)Read32(png, 20);
            int channels = png[25] == 2 ? 3 : 4;
            int rowBytes = checked(width * channels);
            int stride = checked(rowBytes + 1);
            int expected = checked(stride * height);
            using var idat = new MemoryStream();
            int offset = 8;
            bool ended = false;
            while (offset <= png.Length - 12)
            {
                uint length = Read32(png, offset);
                if (length > png.Length - offset - 12)
                    throw new InvalidDataException("Truncated PNG chunk.");
                int data = offset + 8;
                if (IsChunk(png, offset + 4, "IDAT")) idat.Write(png, data, (int)length);
                if (IsChunk(png, offset + 4, "IEND")) { ended = true; break; }
                offset = checked(data + (int)length + 4);
            }
            if (!ended || idat.Length == 0) throw new InvalidDataException("PNG has no complete image data.");

            byte[] filtered = new byte[expected];
            idat.Position = 0;
            try
            {
                using var inflater = new ZLibStream(idat, CompressionMode.Decompress, true);
                int read = 0;
                while (read < expected)
                {
                    int n = inflater.Read(filtered, read, expected - read);
                    if (n == 0) throw new InvalidDataException("PNG image data is incomplete.");
                    read += n;
                }
                if (inflater.ReadByte() != -1) throw new InvalidDataException("PNG image data exceeds its dimensions.");
            }
            catch (InvalidDataException) { throw; }
            catch (Exception ex) { throw new InvalidDataException("PNG image data could not be decompressed.", ex); }

            var pixels = new byte[checked(width * height * 4)];
            var previous = new byte[rowBytes];
            var current = new byte[rowBytes];
            for (int y = 0; y < height; y++)
            {
                int start = y * stride;
                byte filter = filtered[start];
                if (filter > 4) throw new InvalidDataException("Unsupported PNG row filter.");
                for (int x = 0; x < rowBytes; x++)
                {
                    int left = x >= channels ? current[x - channels] : 0;
                    int above = previous[x];
                    int upperLeft = x >= channels ? previous[x - channels] : 0;
                    int predictor = filter switch
                    {
                        0 => 0,
                        1 => left,
                        2 => above,
                        3 => (left + above) / 2,
                        _ => Paeth(left, above, upperLeft)
                    };
                    current[x] = unchecked((byte)(filtered[start + 1 + x] + predictor));
                }
                for (int x = 0; x < width; x++)
                {
                    // PNG scanlines start at the top; Unity raw texture rows start
                    // at the bottom.
                    int source = x * channels, target = ((height - 1 - y) * width + x) * 4;
                    pixels[target] = current[source];
                    pixels[target + 1] = current[source + 1];
                    pixels[target + 2] = current[source + 2];
                    pixels[target + 3] = channels == 4 ? current[source + 3] : (byte)255;
                }
                (previous, current) = (current, previous);
            }
            return new PngTexture { Width = width, Height = height, Rgba = pixels };
        }

        private static int Paeth(int left, int above, int upperLeft)
        {
            int estimate = left + above - upperLeft;
            int a = Math.Abs(estimate - left), b = Math.Abs(estimate - above), c = Math.Abs(estimate - upperLeft);
            return a <= b && a <= c ? left : b <= c ? above : upperLeft;
        }

        private static bool IsChunk(byte[] bytes, int offset, string type)
            => bytes[offset] == type[0] && bytes[offset + 1] == type[1]
               && bytes[offset + 2] == type[2] && bytes[offset + 3] == type[3];

        private static uint Read32(byte[] bytes, int offset)
            => ((uint)bytes[offset] << 24) | ((uint)bytes[offset + 1] << 16)
               | ((uint)bytes[offset + 2] << 8) | bytes[offset + 3];
    }
}
