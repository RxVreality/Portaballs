namespace Portaballs.Core
{
    /// <summary>
    /// A rectangle of texels, inclusive at both ends, used to say which part of the paint changed.
    /// An empty rectangle has X1 &lt; X0. Texel (0, 0) is the lower-left corner of the wall.
    /// </summary>
    public struct TexelRect
    {
        public int X0;
        public int Y0;
        public int X1;
        public int Y1;

        public static TexelRect Empty
        {
            get { return new TexelRect { X0 = int.MaxValue, Y0 = int.MaxValue, X1 = int.MinValue, Y1 = int.MinValue }; }
        }

        public TexelRect(int x0, int y0, int x1, int y1)
        {
            X0 = x0; Y0 = y0; X1 = x1; Y1 = y1;
        }

        public bool IsEmpty
        {
            get { return X1 < X0 || Y1 < Y0; }
        }

        /// <summary>Width in texels (0 when empty).</summary>
        public int Width
        {
            get { return IsEmpty ? 0 : X1 - X0 + 1; }
        }

        /// <summary>Height in texels (0 when empty).</summary>
        public int Height
        {
            get { return IsEmpty ? 0 : Y1 - Y0 + 1; }
        }

        /// <summary>Grow to cover one texel.</summary>
        public void Include(int x, int y)
        {
            if (x < X0) X0 = x;
            if (x > X1) X1 = x;
            if (y < Y0) Y0 = y;
            if (y > Y1) Y1 = y;
        }

        /// <summary>Grow to cover another rectangle.</summary>
        public void Include(TexelRect r)
        {
            if (r.IsEmpty) return;
            if (r.X0 < X0) X0 = r.X0;
            if (r.X1 > X1) X1 = r.X1;
            if (r.Y0 < Y0) Y0 = r.Y0;
            if (r.Y1 > Y1) Y1 = r.Y1;
        }

        public bool Contains(int x, int y)
        {
            return x >= X0 && x <= X1 && y >= Y0 && y <= Y1;
        }

        public bool Intersects(TexelRect r)
        {
            if (IsEmpty || r.IsEmpty) return false;
            return r.X0 <= X1 && r.X1 >= X0 && r.Y0 <= Y1 && r.Y1 >= Y0;
        }

        /// <summary>The same rectangle pushed out by n texels on every side.</summary>
        public TexelRect Expanded(int n)
        {
            if (IsEmpty) return this;
            return new TexelRect(X0 - n, Y0 - n, X1 + n, Y1 + n);
        }

        public override string ToString()
        {
            return IsEmpty ? "(empty)" : "(" + X0 + "," + Y0 + ")-(" + X1 + "," + Y1 + ")";
        }
    }
}
