using OpenTK.Graphics.OpenGL4;
using StbImageSharp;
using System.Reflection.Metadata;
using PixelFormat = OpenTK.Graphics.OpenGL4.PixelFormat;

namespace OpenRender.Core.Textures;

/// <summary>
/// Helper class to load and store textures.
/// </summary>
public class TextureBase: IDisposable
{
    private static readonly Dictionary<string, TextureBase> textureCache = new();

    private TextureBase(uint glHandle)
    {
        Handle = glHandle;
    }

    public uint Handle { get; init; }

    /// <summary>
    /// Source path of image - for debugging.
    /// </summary>
    public string DebugName { get; set; } = default!;

    /// <summary>
    /// The texture width in pixels.
    /// </summary>
    public int Width { get; internal set; }

    /// <summary>
    /// The texture height in pixels.
    /// </summary>
    public int Height { get; internal set; }

    public TextureTarget TextureTarget { get; internal set; }

    /// <summary>
    /// Activates and binds the texture to the given unit.
    /// </summary>
    /// <param name="unit"></param>
    public void Use(TextureUnit unit) => GL.BindTextureUnit((uint)(unit - TextureUnit.Texture0), Handle);

    public ulong GetBindlessHandle(Sampler sampler, bool? makeResident = true) => GetBindlessHandle(sampler.Handle, makeResident);

    public ulong GetBindlessHandle(uint samplerHandle, bool? makeResident = true)
    {
        var rh = (ulong) GL.Arb.GetTextureSamplerHandle(Handle, samplerHandle);        
        if(makeResident ?? true) MakeResident(rh);        
        return rh;
    }

    public override string ToString() => $"{Handle} '{DebugName}', {TextureTarget}";

    public static void MakeResident(ulong bindlessHandle)
    {
        if (bindlessHandle > 0)
        {
            var isResident = GL.Arb.IsTextureHandleResident(bindlessHandle);
            if (!isResident) GL.Arb.MakeTextureHandleResident(bindlessHandle);
        }
    }

    public static TextureBase FromByteArray(byte[] buffer,
        int width,
        int height,
        string name,
        bool generateMipMap = false,
        TextureTarget textureTarget = TextureTarget.Texture2D,
        SizedInternalFormat internalFormat = SizedInternalFormat.Srgb8Alpha8)
    {
        var key = $"{name}|{textureTarget}";
        if (textureCache.TryGetValue(key, out var cachedTexture))
        {
            return cachedTexture;
        }
        var mipmapLevels = 1;
        if(generateMipMap)
        {
            mipmapLevels = 1 + (int)Math.Floor(Math.Log2(Math.Max(width, height)));
        }
        GL.CreateTextures(textureTarget, 1, out uint handle);
        GL.TextureStorage2D(handle, mipmapLevels, internalFormat, width, height);
        GL.TextureSubImage2D(handle, 0, 0, 0, width, height, PixelFormat.Rgba, PixelType.UnsignedByte, buffer);
        if (generateMipMap)
        {
            GL.GenerateTextureMipmap(handle);
        }
        var texture = new TextureBase(handle)
        {
            DebugName = name,
            Width = width,
            Height = height,
            TextureTarget = textureTarget,
        };
        textureCache[key] = texture;
        GL.ObjectLabel(ObjectLabelIdentifier.Texture, handle, -1, texture.ToString());
        Log.CheckGlError();

        Log.Info($"created texture: {texture}");
        return texture;
    }

    public static TextureBase FromDescriptor(TextureDescriptor descriptor) => FromFile(
            descriptor.Paths,
            descriptor.GenerateMipMap,
            descriptor.TextureTarget
    );

    public static TextureBase FromFile(string[] paths, bool generateMipMap = true, TextureTarget textureTarget = TextureTarget.Texture2D)
    {
        var name = string.Join(':', paths);
        var key = $"{textureTarget}|{name}";
        if (textureCache.TryGetValue(key, out var cachedTexture))
        {
            return cachedTexture;
        }
       
        GL.CreateTextures(textureTarget, 1, out uint handle);
        GL.PixelStore(PixelStoreParameter.PackAlignment, 1);

        ImageResult? image = null;
        if (textureTarget == TextureTarget.Texture2D)
        {
            // OpenGL has it's texture origin in the lower left corner instead of the top left corner,
            // so we tell StbImageSharp to flip the image when loading.
            StbImage.stbi_set_flip_vertically_on_load(1);

            using var stream = File.OpenRead(paths[0]);
            image = ImageResult.FromStream(stream, ColorComponents.RedGreenBlueAlpha);
            
            var mipmapLevels = 1;
            if (generateMipMap)
            {
                //  calculate the max number of mip map levels
                mipmapLevels = 1 + (int)Math.Floor(Math.Log2(Math.Max(image.Width, image.Height)));
            }

            GL.TextureStorage2D(handle, mipmapLevels, SizedInternalFormat.Srgb8Alpha8, image.Width, image.Height);
            GL.TextureSubImage2D(handle, 0, 0, 0, image.Width, image.Height, PixelFormat.Rgba, PixelType.UnsignedByte, image.Data);

            if (generateMipMap)
            {
                GL.GenerateTextureMipmap(handle);
            }
        }
        else if (textureTarget == TextureTarget.TextureCubeMap && paths.Length == 6)
        {
            GL.TextureParameterI(handle, TextureParameterName.TextureWrapS, new[] { (int)TextureWrapMode.ClampToEdge });
            GL.TextureParameterI(handle, TextureParameterName.TextureWrapT, new[] { (int)TextureWrapMode.ClampToEdge });
            GL.TextureParameterI(handle, TextureParameterName.TextureMinFilter, new[] { (int)TextureMinFilter.Linear });
            GL.TextureParameterI(handle, TextureParameterName.TextureMagFilter, new[] { (int)TextureMagFilter.Linear });

            StbImage.stbi_set_flip_vertically_on_load(0);
            for (var face = 0; face < paths.Length; face++)
            {
                using var stream = File.OpenRead(paths[face]);
                image = ImageResult.FromStream(stream, ColorComponents.RedGreenBlueAlpha);
                if (face == 0) GL.TextureStorage2D(handle, 1, SizedInternalFormat.Srgb8, image.Width, image.Height);
                GL.TextureSubImage3D(handle, 0, 0, 0, face, image.Width, image.Height, 1, PixelFormat.Rgba, PixelType.UnsignedByte, image.Data);
            }
        }
        else
        {
            throw new NotImplementedException();
        }

        var texture = new TextureBase(handle)
        {
            DebugName = paths[0],
            TextureTarget = textureTarget,
            Width = image!.Width,
            Height = image.Height,
        };
        textureCache[key] = texture;
        GL.ObjectLabel(ObjectLabelIdentifier.Texture, handle, -1, texture.ToString());
        Log.CheckGlError();
        Log.Info($"created texture: {texture}");
        return texture;
    }

    public void Dispose()
    {
        GL.DeleteTexture(Handle);
        GC.SuppressFinalize(this);
    }
}