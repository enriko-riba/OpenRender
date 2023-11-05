using OpenTK.Graphics.OpenGL4;

namespace OpenRender.Core.Textures;

public class BindlessTexture : IDisposable
{
    private readonly ulong handle;

    public BindlessTexture(uint textureHandle, uint samplerHandle)
    {
        handle = (ulong)GL.Arb.GetTextureSamplerHandle(textureHandle, samplerHandle);
    }
    public BindlessTexture(TextureBase texture, Sampler sampler) : this(texture.Handle, sampler.Handle) { }

    public ulong Handle => handle;

    public void MakeResident() => GL.Arb.MakeTextureHandleResident(handle);

    public void MakeNonResident() => GL.Arb.MakeTextureHandleNonResident(handle);

    public void Dispose()
    {
        GL.Arb.MakeTextureHandleNonResident(handle);
        GC.SuppressFinalize(this);
    }
}
