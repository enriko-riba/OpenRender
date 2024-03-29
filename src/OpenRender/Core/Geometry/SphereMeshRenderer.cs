﻿using OpenRender.Core.Buffers;
using OpenRender.Core.Rendering;
using OpenTK.Graphics.OpenGL;
using OpenTK.Mathematics;

namespace OpenRender.Core.Geometry;

public class SphereMeshRenderer
{
    public static readonly SphereMeshRenderer DefaultSphereMeshRenderer = new();
    private readonly Shader shader;
    private readonly VertexArrayObject vao;

    public SphereMeshRenderer()
    {
        ScaleMatrix = Matrix4.CreateScale(1.15f);
        var (vertices, indices) = GeometryHelper.CreateSphere(12, 18);
        var floats = Mesh.GetVertices(VertexDeclarations.VertexPositionNormalTexture, vertices);
        vao = new VertexArrayObject();
        vao.AddBuffer(VertexDeclarations.VertexPositionNormalTexture, new Buffer<float>(floats));
        vao.AddIndexBuffer(new IndexBuffer(indices));
        shader = new Shader("Shaders/spheremesh.vert", "Shaders/spheremesh.frag");
    }

    /// <summary>
    /// Scale increase factor. The sphere mesh needs to be a bit larger then the scene node.
    /// The default factor value is 1.15f.
    /// </summary>
    public Matrix4 ScaleMatrix { get; set; }
    public Shader Shader => shader;

    public void Render()
    {
        shader.Use();
        GL.BindVertexArray(vao);
        GL.PolygonMode(MaterialFace.FrontAndBack, PolygonMode.Line);
        GL.DrawElements(PrimitiveType.Triangles, vao.DataLength, DrawElementsType.UnsignedInt, 0);
        GL.PolygonMode(MaterialFace.FrontAndBack, PolygonMode.Fill);
    }
}