using SkiaSharp;

namespace client.Services.Screenshots;

/// <summary>Encode packed BGRA frames to WebP via SkiaSharp (already shipped with Avalonia).</summary>
public static class ScreenshotWebpEncoder
{
    public static byte[]? EncodeBgra(byte[] bgra, int width, int height, int quality)
    {
        if (width < 2 || height < 2 || bgra.Length < width * height * 4)
            return null;

        quality = Math.Clamp(quality, 1, 100);
        var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        using var bitmap = new SKBitmap(info);
        var needed = width * height * 4;
        System.Runtime.InteropServices.Marshal.Copy(bgra, 0, bitmap.GetPixels(), needed);

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Webp, quality);
        if (data is null || data.Size <= 0)
            return null;
        return data.ToArray();
    }
}
