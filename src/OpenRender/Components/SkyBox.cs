using OpenRender.Core;
using OpenRender.Core.Buffers;
using OpenRender.Core.Geometry;
using OpenRender.Core.Rendering;
using OpenRender.Core.Textures;
using OpenRender.SceneManagement;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;

namespace OpenRender.Components;

public sealed class SkyBox : SceneNode
{    
    private Matrix4 projectionMatrix = Matrix4.Identity;

    public static SkyBox Create(string[] texturePaths)
    {
        ArgumentNullException.ThrowIfNull(texturePaths);
        if (texturePaths.Length != 6) throw new ArgumentException("SkyBox needs 6 images", nameof(texturePaths));
        var desc = new TextureDescriptor(texturePaths,
            TextureType: TextureType.CubeMap,
            TextureTarget: TextureTarget.TextureCubeMap,
            TextureWrapS: TextureWrapMode.ClampToEdge,
            TextureWrapT: TextureWrapMode.ClampToEdge);
        var shader = new Shader("Shaders/skybox.vert", "Shaders/skybox.frag");
        var material = Material.Create(shader, desc);

        var (vertices, indices) = GeometryHelper.CreateCube();
        var skyBoxMesh = new Mesh(VertexDeclarations.VertexPositionNormalTexture, vertices, indices);
        var skybox = new SkyBox(skyBoxMesh, material);
        return skybox;
    }

    public SkyBox(Mesh mesh, Material material) : base(mesh, material)
    {
        RenderGroup = RenderGroup.SkyBox;
        DisableCulling = true;
    }


    public override void OnResize(Scene scene, ResizeEventArgs e)
    {
        projectionMatrix = Matrix4.CreatePerspectiveFieldOfView(MathHelper.PiOver4, scene.Camera?.AspectRatio ?? 1f, 0.0001f, 5000);
    }
     

    public override void OnDraw(double elapsed)
    {
        GL.DepthMask(false);
        GL.GetInteger(GetPName.DepthFunc, out var depthFunc);
        var isCullFaceEnabled = GL.IsEnabled(EnableCap.CullFace);

        if (isCullFaceEnabled)
        {
            GL.Disable(EnableCap.CullFace);
        }
        if (depthFunc != (int)DepthFunction.Lequal)
        {
            GL.DepthFunc(DepthFunction.Lequal);
        }

        var view = Scene!.Camera!.ViewMatrix;
        Material.Shader.SetMatrix4("view", ref view);
        Material.Shader.SetMatrix4("projection", ref projectionMatrix);
        
        GL.BindTextureUnit(0, Material.Textures[0].Handle);
        Material.Shader.SetInt("texture_cubemap", 0);

        base.OnDraw(elapsed);

        GL.DepthMask(true);
        //  restore previous values
        if (depthFunc != (int)DepthFunction.Lequal)
        {
            GL.DepthFunc((DepthFunction)depthFunc);
        }
        if (isCullFaceEnabled)
        {
            GL.Enable(EnableCap.CullFace);
        }
    }
}
