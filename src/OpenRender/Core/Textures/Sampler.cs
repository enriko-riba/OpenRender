using OpenTK.Graphics.OpenGL4;

namespace OpenRender.Core.Textures;

public class Sampler
{
    private readonly uint handle;
    private static readonly Dictionary<string, Sampler> samplerCache = new();

    public static Sampler FromDescriptor(TextureDescriptor descriptor, string? name = null)
    {
        var key = CalculateKey(descriptor.MinFilter, descriptor.MagFilter, descriptor.TextureWrapS, descriptor.TextureWrapT);
        if (samplerCache.TryGetValue(key, out var cachedSampler))
        {
            return cachedSampler;
        }
        var sampler = new Sampler(descriptor.MinFilter, descriptor.MagFilter, descriptor.TextureWrapS, descriptor.TextureWrapT, name);
        samplerCache.Add(key, sampler);
        return sampler;
    }

    public static Sampler Create(TextureMinFilter minFilter, TextureMagFilter magFilter, TextureWrapMode wrapS, TextureWrapMode wrapT, string? name = null)
    {
        var key = CalculateKey(minFilter, magFilter, wrapS, wrapT);
        if (samplerCache.TryGetValue(key, out var cachedSampler))
        {
            return cachedSampler;
        }
        var sampler = new Sampler(minFilter, magFilter, wrapS, wrapT, name);
        samplerCache.Add(key, sampler);
        return sampler;
    }

    private Sampler(TextureMinFilter minFilter, TextureMagFilter magFilter, TextureWrapMode wrapS, TextureWrapMode wrapT, string? name = null)
    {
        MinFilter = minFilter;
        MagFilter = magFilter;
        WrapS = wrapS;
        WrapT = wrapT;

        name ??= $"Sampler {minFilter}, {magFilter}, {wrapT}, {wrapS}";
        GL.CreateSamplers(1, out handle);
        GL.ObjectLabel(ObjectLabelIdentifier.Sampler, handle, -1, name);
        GL.SamplerParameter(handle, SamplerParameterName.TextureMinFilter, (int)minFilter);
        GL.SamplerParameter(handle, SamplerParameterName.TextureMagFilter, (int)magFilter);
        GL.SamplerParameter(handle, SamplerParameterName.TextureWrapS, (int)wrapS);
        GL.SamplerParameter(handle, SamplerParameterName.TextureWrapT, (int)wrapT);
        //GL.SamplerParameter(handle, SamplerParameterName.TextureBorderColor, 0);
        Log.CheckGlError();
    }

    public uint Handle => handle;
    public TextureMinFilter MinFilter { get; set; }
    public TextureMagFilter MagFilter { get; set; }
    public TextureWrapMode WrapS { get; set; }
    public TextureWrapMode WrapT { get; set; }

    private static string CalculateKey(
        TextureMinFilter minFilter,
        TextureMagFilter magFilter,
        TextureWrapMode textureWrapS,
        TextureWrapMode textureWrapT) => $"{minFilter}:{magFilter}:{textureWrapS}:{textureWrapT}";
}
