using OpenRender.Core;
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
    private Shader shader;
    public SkyBox(string[] texturePaths) : base(default, default)
    {
        ArgumentNullException.ThrowIfNull(texturePaths);
        if (texturePaths.Length != 6) throw new ArgumentException("SkyBox needs 6 images", nameof(texturePaths));


        var desc = new TextureDescriptor(texturePaths,
            TextureType: TextureType.CubeMap,
            TextureTarget: TextureTarget.TextureCubeMap,
            TextureWrapS: TextureWrapMode.ClampToEdge,
            TextureWrapT: TextureWrapMode.ClampToEdge);
        shader = new Shader("Shaders/skybox.vert", "Shaders/skybox.frag");
        Material = Material.Create(shader, desc);

        var (vertices, indices) = GeometryHelper.CreateCube();
        var skyBoxMesh = new Mesh(VertexDeclarations.VertexPositionNormalTexture, vertices, indices);
        SetMesh(skyBoxMesh);
        RenderGroup = RenderGroup.SkyBox;
        DisableCulling = true;
    }

    public override void OnResize(Scene scene, ResizeEventArgs e)
    {
        projectionMatrix = Matrix4.CreatePerspectiveFieldOfView(MathHelper.PiOver4, scene.Camera?.AspectRatio ?? 1f, 0.0001f, 5000);
    }

    public override void OnDraw(double elapsed)
    {
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
        shader.SetMatrix4("view", ref view);
        shader.SetMatrix4("projection", ref projectionMatrix);

        base.OnDraw(elapsed);

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
