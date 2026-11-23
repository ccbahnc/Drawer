using System;

namespace Drawer.Core;

internal sealed record IconPixels(int Width, int Height, byte[] Bgra)
{
    internal IconPixels RemoveDetachedHairline()
    {
        if (Width < 16 || Height < 16) return this;
        int Pixel(int x, int y) => (y * Width + x) * 4;
        int Alpha(int x, int y) => Bgra[Pixel(x, y) + 3];
        bool FaintGray(int x, int y)
        {
            int p = Pixel(x, y);
            return Bgra[p + 3] is > 0 and <= 176 &&
                Math.Abs(Bgra[p] - Bgra[p + 1]) <= 5 && Math.Abs(Bgra[p + 1] - Bgra[p + 2]) <= 5;
        }
        // Shell canvases can have several antialiased rings. Require a
        // transparent rectangular gap and an opaque graphic inside it.
        int limit = Math.Clamp((int)Math.Ceiling(Math.Min(Width, Height) / 32d), 3, 10);
        int inset = -1;
        for (int depth = 0; depth <= limit; depth++)
        {
            int right = Width - 1 - depth, bottom = Height - 1 - depth;
            bool occupied = false;
            bool Accept(int x, int y)
            {
                if (Alpha(x, y) == 0) return true;
                occupied = true;
                return FaintGray(x, y);
            }
            for (int x = depth; x <= right; x++)
                if (!Accept(x, depth) || !Accept(x, bottom)) return this;
            for (int y = depth + 1; y < bottom; y++)
                if (!Accept(depth, y) || !Accept(right, y)) return this;
            if (occupied)
            {
                if (inset < 0)
                {
                    if (depth > 2) return this;
                    inset = depth;
                }
                continue;
            }
            if (inset < 0) continue;
            int top = 0, lower = 0, left = 0, outerRight = 0;
            for (int x = inset; x < Width - inset; x++)
            {
                bool upperPixel = false, lowerPixel = false;
                for (int band = inset; band < depth; band++)
                {
                    upperPixel |= Alpha(x, band) != 0;
                    lowerPixel |= Alpha(x, Height - 1 - band) != 0;
                }
                if (upperPixel) top++;
                if (lowerPixel) lower++;
            }
            for (int y = inset; y < Height - inset; y++)
            {
                bool leftPixel = false, rightPixel = false;
                for (int band = inset; band < depth; band++)
                {
                    leftPixel |= Alpha(band, y) != 0;
                    rightPixel |= Alpha(Width - 1 - band, y) != 0;
                }
                if (leftPixel) left++;
                if (rightPixel) outerRight++;
            }
            if (Math.Min(top, lower) < (Width - 2 * inset) * .9 ||
                Math.Min(left, outerRight) < (Height - 2 * inset) * .9) return this;
            bool opaque = false;
            for (int y = depth + 1; y < bottom && !opaque; y++)
                for (int x = depth + 1; x < right && !opaque; x++)
                    opaque = Alpha(x, y) > 176;
            if (!opaque) return this;
            var clean = (byte[])Bgra.Clone();
            for (int band = inset; band < depth; band++)
            {
                int endX = Width - 1 - band, endY = Height - 1 - band;
                Array.Clear(clean, Pixel(band, band), (endX - band + 1) * 4);
                Array.Clear(clean, Pixel(band, endY), (endX - band + 1) * 4);
                for (int y = band + 1; y < endY; y++)
                {
                    Array.Clear(clean, Pixel(band, y), 4);
                    Array.Clear(clean, Pixel(endX, y), 4);
                }
            }
            return this with { Bgra = clean };
        }
        return this;
    }

    public IconPixels FitVisibleContent()
    {
        int left = Width, top = Height, right = -1, bottom = -1;
        int strongLeft = Width, strongTop = Height, strongRight = -1, strongBottom = -1;
        for (int y = 0; y < Height; y++)
        for (int x = 0; x < Width; x++)
        {
            int alpha = Bgra[(y * Width + x) * 4 + 3];
            if (alpha == 0) continue;
            left = Math.Min(left, x); right = Math.Max(right, x);
            top = Math.Min(top, y); bottom = Math.Max(bottom, y);
            if (alpha < 32) continue;
            strongLeft = Math.Min(strongLeft, x); strongRight = Math.Max(strongRight, x);
            strongTop = Math.Min(strongTop, y); strongBottom = Math.Max(strongBottom, y);
        }
        if (strongRight < 0 || Math.Max((strongRight - strongLeft + 1d) / Width,
            (strongBottom - strongTop + 1d) / Height) >= .74) return this;

        // Keep the entire alpha footprint (including faint shadows), with at
        // least 6% padding per side and a bounded 1.35x increase in scale.
        double ratio = Math.Max(1 / 1.35, Math.Max((right - left + 1d) / Width, (bottom - top + 1d) / Height) / .88);
        if (ratio >= .96) return this;
        int w = (int)Math.Ceiling(Width * ratio), h = (int)Math.Ceiling(Height * ratio);
        int x0 = Math.Clamp((left + right + 1 - w) / 2, 0, Width - w);
        int y0 = Math.Clamp((top + bottom + 1 - h) / 2, 0, Height - h);
        var fitted = new byte[w * h * 4];
        for (int row = 0; row < h; row++)
            Buffer.BlockCopy(Bgra, ((y0 + row) * Width + x0) * 4, fitted, row * w * 4, w * 4);
        return new(w, h, fitted);
    }
}
