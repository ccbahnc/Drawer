using System;

namespace Drawer.Core;

internal static class BackdropBlur
{
    // Three separable box passes approximate a Gaussian with clamped edges.
    // Work on the premultiplied BGRA bytes from the visual snapshot.
    public static byte[] Apply(byte[] pixels, int width, int height, int radius)
    {
        var horizontal = new byte[pixels.Length];
        var output = new byte[pixels.Length];
        var input = pixels;
        for (int pass = 0; pass < 3; pass++)
        {
            Filter(input, horizontal, height, width, width * 4, 4, radius);
            Filter(horizontal, output, width, height, 4, width * 4, radius);
            input = output;
        }
        return output;
    }

    private static void Filter(byte[] source, byte[] target, int lines, int length, int lineStep, int step, int radius)
    {
        int diameter = radius * 2 + 1;
        for (int line = 0; line < lines; line++)
        {
            int start = line * lineStep;
            for (int channel = 0; channel < 4; channel++)
            {
                int first = start + channel;
                int sum = 0;
                for (int i = -radius; i <= radius; i++)
                    sum += source[first + Math.Clamp(i, 0, length - 1) * step];
                for (int i = 0; i < length; i++)
                {
                    target[first + i * step] = (byte)((sum + radius) / diameter);
                    sum += source[first + Math.Min(length - 1, i + radius + 1) * step] -
                           source[first + Math.Max(0, i - radius) * step];
                }
            }
        }
    }
}
