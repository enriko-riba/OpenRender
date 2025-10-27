using OpenRender.Core;
using OpenRender.Core.Buffers;
using OpenRender.Core.Rendering;
using OpenRender.SceneManagement;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using SpyroGame.World;

namespace SpyroGame.Components;

public class WaterNode : SceneNode
{
    private readonly IDayNightTimeProvider timeProvider;
    private double uTime;

    public static WaterNode Create(IDayNightTimeProvider timeProvider)
    {
        var waterShader = new Shader("Shaders/water.vert", "Shaders/water.frag");
        var material = new Material { Shader = waterShader };
        float [] vertices = [
            -1f, 0,  1,  0, 1,   // left near
             1f, 0, -1,  1, 0,   // right far
            -1f, 0, -1,  0, 0,   // left far
             1f, 0,  1,  1, 1    // right near
        ];
        uint[] indices = [0, 1, 2, 0, 3, 1];
        var mesh = new Mesh(VertexDeclarations.VertexPositionTexture, vertices, indices);

        var waterNode = new WaterNode(mesh, material, timeProvider)
        {
            RenderGroup = RenderGroup.Default, // Render after solid objects
            DisableCulling = true
        };
        return waterNode;
    }

    private WaterNode(Mesh mesh, Material material, IDayNightTimeProvider timeProvider) : base(mesh, material)
    {
        this.timeProvider = timeProvider;
    }

    public override void OnDraw(double elapsed)
    {
        uTime += elapsed;
        GL.BindVertexArray(Vao!);
        Material.Shader.Use();

        // Note: Blending should be enabled before drawing this node
        // GL.Enable(EnableCap.Blend);
        // GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);

        Matrix4.CreateScale(VoxelHelper.ChunkSideSize * VoxelHelper.WorldChunksXZ, 1, VoxelHelper.ChunkSideSize * VoxelHelper.WorldChunksXZ, out var worldMatrix);
        worldMatrix.Row3.Xyz = new Vector3(0, VoxelHelper.WaterLevel + 0.85f, 0);
        Material.Shader.SetMatrix4("model", ref worldMatrix);
        Material.Shader.SetFloat("uTime", (float)uTime);
        Material.Shader.SetFloat("uDayFactor", timeProvider.DayFactor);
        var isCullFaceEnabled = GL.IsEnabled(EnableCap.CullFace);
        if (isCullFaceEnabled)
        {
            GL.Disable(EnableCap.CullFace);
        }
        GL.DrawElements(PrimitiveType.Triangles, Vao!.DataLength, DrawElementsType.UnsignedInt, 0);
        if (isCullFaceEnabled)
        {
            GL.Enable(EnableCap.CullFace);
        }
        // Note: Blending should be disabled after drawing
        // GL.Disable(EnableCap.Blend);
    }
}
