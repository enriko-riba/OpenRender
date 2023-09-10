using OpenTK.Graphics.OpenGL4;

namespace OpenRender.Core;

public delegate int VertexDeclaration(int name);

public enum AttributeLocation
{
    Position,
    Normal,
    Color,
    TextureCoord,
}

public static class VertexDeclarations
{
    public static readonly VertexDeclaration VertexPosition = (vao) =>
    {
        var vertexLocation = (int)AttributeLocation.Position;
        GL.EnableVertexArrayAttrib(vao, vertexLocation);
        GL.VertexArrayAttribFormat(vao, vertexLocation, 3, VertexAttribType.Float, false, 0);
        GL.VertexArrayAttribBinding(vao, vertexLocation, 0);

        return 3 * sizeof(float);
    };

    public static readonly VertexDeclaration VertexPositionTexture = (vao) =>
    {
        // vertex positions
        var vertexLocation = (int)AttributeLocation.Position;
        GL.EnableVertexArrayAttrib(vao, vertexLocation);
        GL.VertexArrayAttribFormat(vao, vertexLocation, 3, VertexAttribType.Float, false, 0);
        GL.VertexArrayAttribBinding(vao, vertexLocation, 0);

        // vertex texture coords
        var texCoordLocation = (int)AttributeLocation.TextureCoord;
        GL.EnableVertexArrayAttrib(vao, texCoordLocation);
        GL.VertexArrayAttribFormat(vao, texCoordLocation, 2, VertexAttribType.Float, false, 3 * sizeof(float));
        GL.VertexArrayAttribBinding(vao, texCoordLocation, 0);

        return 5 * sizeof(float);
    };

    public static readonly VertexDeclaration VertexPosition2DTexture = (vao) =>
    {
        // vertex positions
        var vertexLocation = (int)AttributeLocation.Position;
        GL.EnableVertexArrayAttrib(vao, vertexLocation);
        GL.VertexArrayAttribFormat(vao, vertexLocation, 2, VertexAttribType.Float, false, 0);
        GL.VertexArrayAttribBinding(vao, vertexLocation, 0);

        // vertex texture coords
        var texCoordLocation = (int)AttributeLocation.TextureCoord;
        GL.EnableVertexArrayAttrib(vao, texCoordLocation);
        GL.VertexArrayAttribFormat(vao, texCoordLocation, 2, VertexAttribType.Float, false, 2 * sizeof(float));
        GL.VertexArrayAttribBinding(vao, texCoordLocation, 0);

        return 4 * sizeof(float);
    };

    public static readonly VertexDeclaration VertexPositionNormal = (vao) =>
    {
        // vertex positions
        var vertexLocation = (int)AttributeLocation.Position;
        GL.EnableVertexArrayAttrib(vao, vertexLocation);
        GL.VertexArrayAttribFormat(vao, vertexLocation, 3, VertexAttribType.Float, false, 0);
        GL.VertexArrayAttribBinding(vao, vertexLocation, 0);

        // vertex normals
        var normalLocation = (int)AttributeLocation.Normal;
        GL.EnableVertexArrayAttrib(vao, normalLocation);
        GL.VertexArrayAttribFormat(vao, normalLocation, 3, VertexAttribType.Float, false, 3 * sizeof(float));
        GL.VertexArrayAttribBinding(vao, normalLocation, 0);
        return 6 * sizeof(float);
    };

    public static readonly VertexDeclaration VertexPositionNormalTexture = (vao) =>
    {
        // vertex positions
        var vertexLocation = (int)AttributeLocation.Position;
        GL.EnableVertexArrayAttrib(vao, vertexLocation);
        GL.VertexArrayAttribFormat(vao, vertexLocation, 3, VertexAttribType.Float, false, 0);
        GL.VertexArrayAttribBinding(vao, vertexLocation, 0);

        // vertex normals
        var normalLocation = (int)AttributeLocation.Normal;
        GL.EnableVertexArrayAttrib(vao, normalLocation);
        GL.VertexArrayAttribFormat(vao, normalLocation, 3, VertexAttribType.Float, false, 3 * sizeof(float));
        GL.VertexArrayAttribBinding(vao, normalLocation, 0);

        // vertex texture coords
        var texCoordLocation = (int)AttributeLocation.TextureCoord;
        GL.EnableVertexArrayAttrib(vao, texCoordLocation);
        GL.VertexArrayAttribFormat(vao, texCoordLocation, 2, VertexAttribType.Float, false, 6 * sizeof(float));
        GL.VertexArrayAttribBinding(vao, texCoordLocation, 0);

        return 8 * sizeof(float);
    };

    public static readonly VertexDeclaration VertexPositionColorTexture = (vao) =>
    {
        // vertex positions
        var vertexLocation = (int)AttributeLocation.Position;
        GL.EnableVertexArrayAttrib(vao, vertexLocation);
        GL.VertexArrayAttribFormat(vao, vertexLocation, 3, VertexAttribType.Float, false, 0);
        GL.VertexArrayAttribBinding(vao, vertexLocation, 0);

        // vertex colors
        var colorLocation = (int)AttributeLocation.Color;
        GL.EnableVertexArrayAttrib(vao, colorLocation);
        GL.VertexArrayAttribFormat(vao, colorLocation, 3, VertexAttribType.Float, false, 3 * sizeof(float));
        GL.VertexArrayAttribBinding(vao, colorLocation, 0);

        // vertex texture coords
        var texCoordLocation = (int)AttributeLocation.TextureCoord;
        GL.EnableVertexArrayAttrib(vao, texCoordLocation);
        GL.VertexArrayAttribFormat(vao, texCoordLocation, 2, VertexAttribType.Float, false, 6 * sizeof(float));
        GL.VertexArrayAttribBinding(vao, texCoordLocation, 0);

        return 8 * sizeof(float);
    };

    public static readonly VertexDeclaration VertexPositionColor = (vao) =>
    {
        // vertex positions
        var vertexLocation = (int)AttributeLocation.Position;
        GL.EnableVertexArrayAttrib(vao, vertexLocation);
        GL.VertexArrayAttribFormat(vao, vertexLocation, 3, VertexAttribType.Float, false, 0);
        GL.VertexArrayAttribBinding(vao, vertexLocation, 0);

        // vertex colors
        var colorLocation = (int)AttributeLocation.Color;
        GL.EnableVertexArrayAttrib(vao, colorLocation);
        GL.VertexArrayAttribFormat(vao, colorLocation, 3, VertexAttribType.Float, false, 3 * sizeof(float));
        GL.VertexArrayAttribBinding(vao, colorLocation, 0);

        return 6 * sizeof(float);
    };

    public static readonly VertexDeclaration VertexPositionNormalColor = (vao) =>
    {
        // vertex positions
        var vertexLocation = (int)AttributeLocation.Position;
        GL.EnableVertexArrayAttrib(vao, vertexLocation);
        GL.VertexArrayAttribFormat(vao, vertexLocation, 3, VertexAttribType.Float, false, 0);
        GL.VertexArrayAttribBinding(vao, vertexLocation, 0);

        // vertex normals
        var normalLocation = (int)AttributeLocation.Normal;
        GL.EnableVertexArrayAttrib(vao, normalLocation);
        GL.VertexArrayAttribFormat(vao, normalLocation, 3, VertexAttribType.Float, false, 3 * sizeof(float));
        GL.VertexArrayAttribBinding(vao, normalLocation, 0);

        // vertex colors
        var colorLocation = (int)AttributeLocation.Color;
        GL.EnableVertexArrayAttrib(vao, colorLocation);
        GL.VertexArrayAttribFormat(vao, colorLocation, 3, VertexAttribType.Float, false, 6 * sizeof(float));
        GL.VertexArrayAttribBinding(vao, colorLocation, 0);

        return 9 * sizeof(float);
    };

    public static readonly VertexDeclaration VertexPositionNormalColorTexture = (vao) =>
    {
        const int stride = 11 * sizeof(float);

        // vertex positions
        var vertexLocation = (int)AttributeLocation.Position;
        GL.EnableVertexArrayAttrib(vao, vertexLocation);
        GL.VertexArrayAttribFormat(vao, vertexLocation, 3, VertexAttribType.Float, false, 0);
        GL.VertexArrayAttribBinding(vao, vertexLocation, 0);

        // vertex normals
        var normalLocation = (int)AttributeLocation.Normal;
        GL.EnableVertexArrayAttrib(vao, normalLocation);
        GL.VertexArrayAttribFormat(vao, normalLocation, 3, VertexAttribType.Float, false, 3 * sizeof(float));
        GL.VertexArrayAttribBinding(vao, normalLocation, 0);

        // vertex colors
        var colorLocation = (int)AttributeLocation.Color;
        GL.EnableVertexArrayAttrib(vao, colorLocation);
        GL.VertexArrayAttribFormat(vao, colorLocation, 3, VertexAttribType.Float, false, 6 * sizeof(float));
        GL.VertexArrayAttribBinding(vao, colorLocation, 0);

        // vertex texture coords
        var texCoordLocation = (int)AttributeLocation.TextureCoord;
        GL.EnableVertexArrayAttrib(vao, texCoordLocation);
        GL.VertexArrayAttribFormat(vao, texCoordLocation, 2, VertexAttribType.Float, false, 9 * sizeof(float));
        GL.VertexArrayAttribBinding(vao, texCoordLocation, 0);
        return stride;
    };
}