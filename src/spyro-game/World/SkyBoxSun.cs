using OpenRender.Core;
using System;
using OpenRender.Core.Buffers;
using OpenRender.Core.Geometry;
using OpenRender.Core.Rendering;
using OpenRender.Core.Textures;
using OpenRender.SceneManagement;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;

namespace SpyroGame.World;

internal class SkyBoxSun(Mesh mesh, Material material) : SceneNode(mesh, material, Vector3.Zero)
{
    private Matrix4 projectionMatrix = Matrix4.Identity;
    private Matrix4 invProjectionMatrix = Matrix4.Identity;

    public static SkyBoxSun Create()
    {
        var shader = new Shader("Shaders/skybox-sun.vert", "Shaders/skybox-sun.frag");
        var mat = new Material { Shader = shader };
        var (vertices, indices) = GeometryHelper.CreateCube();
        var skyBoxMesh = new Mesh(VertexDeclarations.VertexPositionNormalTexture, vertices, indices);
        var skybox = new SkyBoxSun(skyBoxMesh, mat)
        {
            RenderGroup = RenderGroup.SkyBox,
            DisableCulling = true
        };
        return skybox;
    }

    public override void OnResize(Scene scene, ResizeEventArgs e)
    {
        projectionMatrix = Matrix4.CreatePerspectiveFieldOfView(MathHelper.PiOver4, scene.Camera?.AspectRatio ?? 1f, 0.0001f, 5000);
        Matrix4.Invert(projectionMatrix, out invProjectionMatrix);
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

        int[] viewport = new int[4];
        GL.GetInteger(GetPName.Viewport, viewport);
        var viewportSize = new Vector2(viewport[2], viewport[3]);
        Material.Shader.SetVector2("uViewportSize", ref viewportSize);
        Material.Shader.SetMatrix4("uInvProjection", ref invProjectionMatrix);

        var view = Scene!.Camera!.ViewMatrix;
        view.Row3.Xyz = Vector3.Zero;
        Material.Shader.SetMatrix4("view", ref view);
        Material.Shader.SetMatrix4("projection", ref projectionMatrix);

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
