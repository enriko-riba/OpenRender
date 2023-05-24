using OpenRender.Core;
using OpenRender.Core.Geometry;
using OpenRender.Core.Rendering;
using OpenRender.Core.Textures;
using OpenTK.Graphics.OpenGL4;

namespace OpenRender.SceneManagement;

public class SkyBox : SceneNode
{
    public SkyBox(string[] texturePaths) : base(null, default)
    {
        ArgumentNullException.ThrowIfNull(texturePaths);
        if (texturePaths.Length != 6) throw new ArgumentException("SkyBox needs 6 images", nameof(texturePaths));

        var vb = GeometryHelper.CreateCube(false);
        var desc = new TextureDescriptor(texturePaths,
            TextureType: TextureType.CubeMap,
            TextureTarget: TextureTarget.TextureCubeMap,
            TextureWrapS: TextureWrapMode.ClampToEdge,
            TextureWrapT: TextureWrapMode.ClampToEdge);
        var shader = new Shader("Shaders/skybox.vert", "Shaders/skybox.frag");
        var mat = Material.Create(shader, desc);
        mat.Initialize();
        Mesh = new Mesh(vb, DrawMode.Indexed, mat);
        SetScale(5);
    }

    public override void OnDraw(Scene scene, double elapsed)
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
        base.OnDraw(scene, elapsed);

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
