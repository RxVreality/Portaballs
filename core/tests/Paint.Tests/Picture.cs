using System;
using System.IO;
using System.IO.Compression;

namespace Portaballs.Core.Tests
{
    /// <summary>A plain RGB picture in memory, row 0 at the top, with just enough drawing to make contact sheets.</summary>
    public sealed class Picture
    {
        public readonly int Width;
        public readonly int Height;
        public readonly byte[] Rgb;

        public Picture(int width, int height)
        {
            Width = width;
            Height = height;
            Rgb = new byte[width * height * 3];
        }

        public void Fill(byte r, byte g, byte b)
        {
            for (int i = 0; i < Width * Height; i++) { Rgb[i * 3] = r; Rgb[i * 3 + 1] = g; Rgb[i * 3 + 2] = b; }
        }

        public void Set(int x, int y, byte r, byte g, byte b)
        {
            if (x < 0 || y < 0 || x >= Width || y >= Height) return;
            int o = (x + y * Width) * 3;
            Rgb[o] = r; Rgb[o + 1] = g; Rgb[o + 2] = b;
        }

        public void SetF(int x, int y, float r, float g, float b)
        {
            Set(x, y, B(r), B(g), B(b));
        }

        private static byte B(float v)
        {
            v = v * 255f + 0.5f;
            return v <= 0f ? (byte)0 : v >= 255f ? (byte)255 : (byte)v;
        }

        public void Blit(Picture src, int x0, int y0)
        {
            for (int y = 0; y < src.Height; y++)
            for (int x = 0; x < src.Width; x++)
            {
                int o = (x + y * src.Width) * 3;
                Set(x0 + x, y0 + y, src.Rgb[o], src.Rgb[o + 1], src.Rgb[o + 2]);
            }
        }

        public void Rect(int x0, int y0, int x1, int y1, byte r, byte g, byte b)
        {
            for (int x = x0; x <= x1; x++) { Set(x, y0, r, g, b); Set(x, y1, r, g, b); }
            for (int y = y0; y <= y1; y++) { Set(x0, y, r, g, b); Set(x1, y, r, g, b); }
        }

        public void FillRect(int x0, int y0, int x1, int y1, byte r, byte g, byte b)
        {
            for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++) Set(x, y, r, g, b);
        }

        public void Circle(float cx, float cy, float radius, byte r, byte g, byte b)
        {
            int n = Math.Max(32, (int)(radius * 8));
            for (int i = 0; i < n; i++)
            {
                float a = i * 2f * MathF.PI / n;
                int x = (int)MathF.Round(cx + MathF.Cos(a) * radius);
                int y = (int)MathF.Round(cy + MathF.Sin(a) * radius);
                Set(x, y, r, g, b);
                Set(x + 1, y, r, g, b);
            }
        }

        /// <summary>Text in a tiny blocky font (capitals, digits and a few signs), on a dark band so it reads on anything.</summary>
        public void Text(int x, int y, string s, int scale = 2)
        {
            int w = s.Length * 4 * scale + scale;
            FillRect(x - scale, y - scale, x + w, y + 6 * scale, 20, 20, 24);
            int cx = x;
            foreach (char ch in s.ToUpperInvariant())
            {
                string[] g = TinyFont.Glyph(ch);
                for (int row = 0; row < 5; row++)
                for (int col = 0; col < 3; col++)
                {
                    if (g[row][col] == '#')
                        FillRect(cx + col * scale, y + row * scale, cx + col * scale + scale - 1, y + row * scale + scale - 1, 240, 240, 240);
                }
                cx += 4 * scale;
            }
        }

        /// <summary>Save as a PNG file (8-bit RGB, no filtering, zlib compressed).</summary>
        public void SavePng(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
            fs.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
            byte[] ihdr = new byte[13];
            WriteBE(ihdr, 0, (uint)Width);
            WriteBE(ihdr, 4, (uint)Height);
            ihdr[8] = 8;  // bit depth
            ihdr[9] = 2;  // colour type RGB
            WriteChunk(fs, "IHDR", ihdr);
            using (var raw = new MemoryStream())
            {
                using (var z = new ZLibStream(raw, CompressionLevel.Fastest, leaveOpen: true))
                {
                    for (int y = 0; y < Height; y++)
                    {
                        z.WriteByte(0);
                        z.Write(Rgb, y * Width * 3, Width * 3);
                    }
                }
                WriteChunk(fs, "IDAT", raw.ToArray());
            }
            WriteChunk(fs, "IEND", Array.Empty<byte>());
        }

        private static void WriteBE(byte[] b, int o, uint v)
        {
            b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v;
        }

        private static void WriteChunk(Stream s, string type, byte[] data)
        {
            byte[] len = new byte[4];
            WriteBE(len, 0, (uint)data.Length);
            s.Write(len);
            byte[] t = System.Text.Encoding.ASCII.GetBytes(type);
            s.Write(t);
            s.Write(data);
            uint crc = Crc32(t, 0xFFFFFFFFu);
            crc = Crc32(data, crc) ^ 0xFFFFFFFFu;
            byte[] c = new byte[4];
            WriteBE(c, 0, crc);
            s.Write(c);
        }

        private static uint[] _crcTable;

        private static uint Crc32(byte[] data, uint crc)
        {
            if (_crcTable == null)
            {
                var t = new uint[256];
                for (uint n = 0; n < 256; n++)
                {
                    uint c = n;
                    for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                    t[n] = c;
                }
                _crcTable = t;
            }
            foreach (byte b in data) crc = _crcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
            return crc;
        }
    }

    /// <summary>A 3 x 5 pixel font, enough for panel labels.</summary>
    internal static class TinyFont
    {
        public static string[] Glyph(char c)
        {
            switch (c)
            {
                case '0': return new[] { "###", "#.#", "#.#", "#.#", "###" };
                case '1': return new[] { ".#.", "##.", ".#.", ".#.", "###" };
                case '2': return new[] { "###", "..#", "###", "#..", "###" };
                case '3': return new[] { "###", "..#", ".##", "..#", "###" };
                case '4': return new[] { "#.#", "#.#", "###", "..#", "..#" };
                case '5': return new[] { "###", "#..", "###", "..#", "###" };
                case '6': return new[] { "###", "#..", "###", "#.#", "###" };
                case '7': return new[] { "###", "..#", ".#.", ".#.", ".#." };
                case '8': return new[] { "###", "#.#", "###", "#.#", "###" };
                case '9': return new[] { "###", "#.#", "###", "..#", "###" };
                case 'A': return new[] { "###", "#.#", "###", "#.#", "#.#" };
                case 'B': return new[] { "##.", "#.#", "##.", "#.#", "##." };
                case 'C': return new[] { "###", "#..", "#..", "#..", "###" };
                case 'D': return new[] { "##.", "#.#", "#.#", "#.#", "##." };
                case 'E': return new[] { "###", "#..", "##.", "#..", "###" };
                case 'F': return new[] { "###", "#..", "##.", "#..", "#.." };
                case 'G': return new[] { "###", "#..", "#.#", "#.#", "###" };
                case 'H': return new[] { "#.#", "#.#", "###", "#.#", "#.#" };
                case 'I': return new[] { "###", ".#.", ".#.", ".#.", "###" };
                case 'J': return new[] { "..#", "..#", "..#", "#.#", "###" };
                case 'K': return new[] { "#.#", "#.#", "##.", "#.#", "#.#" };
                case 'L': return new[] { "#..", "#..", "#..", "#..", "###" };
                case 'M': return new[] { "#.#", "###", "###", "#.#", "#.#" };
                case 'N': return new[] { "##.", "#.#", "#.#", "#.#", "#.#" };
                case 'O': return new[] { "###", "#.#", "#.#", "#.#", "###" };
                case 'P': return new[] { "###", "#.#", "###", "#..", "#.." };
                case 'Q': return new[] { "###", "#.#", "#.#", "###", "..#" };
                case 'R': return new[] { "##.", "#.#", "##.", "#.#", "#.#" };
                case 'S': return new[] { "###", "#..", "###", "..#", "###" };
                case 'T': return new[] { "###", ".#.", ".#.", ".#.", ".#." };
                case 'U': return new[] { "#.#", "#.#", "#.#", "#.#", "###" };
                case 'V': return new[] { "#.#", "#.#", "#.#", "#.#", ".#." };
                case 'W': return new[] { "#.#", "#.#", "###", "###", "#.#" };
                case 'X': return new[] { "#.#", "#.#", ".#.", "#.#", "#.#" };
                case 'Y': return new[] { "#.#", "#.#", ".#.", ".#.", ".#." };
                case 'Z': return new[] { "###", "..#", ".#.", "#..", "###" };
                case '.': return new[] { "...", "...", "...", "...", ".#." };
                case ',': return new[] { "...", "...", "...", ".#.", "#.." };
                case '=': return new[] { "...", "###", "...", "###", "..." };
                case '-': return new[] { "...", "...", "###", "...", "..." };
                case '+': return new[] { "...", ".#.", "###", ".#.", "..." };
                case '/': return new[] { "..#", "..#", ".#.", "#..", "#.." };
                case ':': return new[] { "...", ".#.", "...", ".#.", "..." };
                case '(': return new[] { ".#.", "#..", "#..", "#..", ".#." };
                case ')': return new[] { ".#.", "..#", "..#", "..#", ".#." };
                default: return new[] { "...", "...", "...", "...", "..." };
            }
        }
    }
}
