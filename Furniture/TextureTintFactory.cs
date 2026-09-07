using System;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Interface.Textures;

namespace IzzysFurniture;

internal static class TextureTintFactory
{
    public static async Task<nint> Create(string path, Vector4 color)
    {
        var source = await Service.TextureProvider.GetFromGame(path).RentAsync(CancellationToken.None).ConfigureAwait(false);
        var args = new TextureModificationArgs
        {
            DxgiFormat = RawImageSpecification.Bgra32(1, 1).DxgiFormat,
        };
        var image = await Service.TextureReadbackProvider
            .GetRawImageAsync(source, args, false, CancellationToken.None)
            .ConfigureAwait(false);

        TintBgraPixels(image.Item2, color);
        var wrap = Service.TextureProvider.CreateFromRaw(image.Item1, image.Item2, $"IzzysFurniture:{path}");
        return Service.TextureProvider.ConvertToKernelTexture(wrap, false);
    }

    private static void TintBgraPixels(Span<byte> pixels, Vector4 color)
    {
        var red = Math.Clamp(color.X, 0.0f, 1.0f);
        var green = Math.Clamp(color.Y, 0.0f, 1.0f);
        var blue = Math.Clamp(color.Z, 0.0f, 1.0f);
        for (var offset = 0; offset < pixels.Length; offset += 4)
        {
            var luminance = (pixels[offset + 2] * 54 + pixels[offset + 1] * 183 + pixels[offset] * 19) >> 8;
            pixels[offset] = (byte)Math.Clamp(luminance * blue, 0.0f, 255.0f);
            pixels[offset + 1] = (byte)Math.Clamp(luminance * green, 0.0f, 255.0f);
            pixels[offset + 2] = (byte)Math.Clamp(luminance * red, 0.0f, 255.0f);
        }
    }
}
