using OpenTK.Graphics.OpenGL4;

namespace OpenRender.Core;

public delegate int VertexDeclaration();

public enum AttributeLocation
{
    Position,
    Normal,
    Color,
    TextureCoord,
}

public static class VertexDeclarations
{
    public static readonly VertexDeclaration VertexPosition = () =>
    {
        const int stride = 3 * sizeof(float);

        var vertexLocation = (int)AttributeLocation.Position;
        GL.EnableVertexAttribArray(vertexLocation);
        GL.VertexAttribPointer(vertexLocation, 3, VertexAttribPointerType.Float, false, stride, 0);
        return stride;
    };

    public static readonly VertexDeclaration VertexPositionTexture = () =>
    {
        const int stride = 5 * sizeof(float);

        // vertex positions
        var vertexLocation = (int)AttributeLocation.Position;
        GL.EnableVertexAttribArray(vertexLocation);
        GL.VertexAttribPointer(vertexLocation, 3, VertexAttribPointerType.Float, false, stride, 0);

        // vertex texture coords
        var texCoordLocation = (int)AttributeLocation.TextureCoord;
        GL.EnableVertexAttribArray(texCoordLocation);
        GL.VertexAttribPointer(texCoordLocation, 2, VertexAttribPointerType.Float, false, stride, 3 * sizeof(float));
        return stride;
    };

    public static readonly VertexDeclaration VertexPosition2DTexture = () =>
    {
        const int stride = 4 * sizeof(float);

        // vertex positions
        var vertexLocation = (int)AttributeLocation.Position;
        GL.EnableVertexAttribArray(vertexLocation);
        GL.VertexAttribPointer(vertexLocation, 2, VertexAttribPointerType.Float, false, stride, 0);

        // vertex texture coords
        var texCoordLocation = (int)AttributeLocation.TextureCoord;
        GL.EnableVertexAttribArray(texCoordLocation);
        GL.VertexAttribPointer(texCoordLocation, 2, VertexAttribPointerType.Float, false, stride, 2 * sizeof(float));
        return stride;
    };

    public static readonly VertexDeclaration VertexPositionNormal = () =>
    {
        const int stride = 6 * sizeof(float);

        // vertex positions
        var vertexLocation = (int)AttributeLocation.Position;
        GL.EnableVertexAttribArray(vertexLocation);
        GL.VertexAttribPointer(vertexLocation, 3, VertexAttribPointerType.Float, false, stride, 0);

        // vertex normals
        var normalLocation = (int)AttributeLocation.Normal;
        GL.EnableVertexAttribArray(normalLocation);
        GL.VertexAttribPointer(normalLocation, 3, VertexAttribPointerType.Float, false, stride, 3 * sizeof(float));
        return stride;
    };

    public static readonly VertexDeclaration VertexPositionNormalTexture = () =>
    {
        const int stride = 8 * sizeof(float);

        // vertex positions
        var vertexLocation = (int)AttributeLocation.Position;
        GL.EnableVertexAttribArray(vertexLocation);
        GL.VertexAttribPointer(vertexLocation, 3, VertexAttribPointerType.Float, false, stride, 0);

        // vertex normals
        var normalLocation = (int)AttributeLocation.Normal;
        GL.EnableVertexAttribArray(normalLocation);
        GL.VertexAttribPointer(normalLocation, 3, VertexAttribPointerType.Float, false, stride, 3 * sizeof(float));

        // vertex texture coords
        var texCoordLocation = (int)AttributeLocation.TextureCoord;
        GL.EnableVertexAttribArray(texCoordLocation);
        GL.VertexAttribPointer(texCoordLocation, 2, VertexAttribPointerType.Float, false, stride, 6 * sizeof(float));
        return stride;
    };

    public static readonly VertexDeclaration VertexPositionColorTexture = () =>
    {
        const int stride = 8 * sizeof(float);

        // vertex positions
        var vertexLocation = (int)AttributeLocation.Position;
        GL.EnableVertexAttribArray(vertexLocation);
        GL.VertexAttribPointer(vertexLocation, 3, VertexAttribPointerType.Float, false, stride, 0);

        // vertex colors
        var colorLocation = (int)AttributeLocation.Color;
        GL.EnableVertexAttribArray(colorLocation);
        GL.VertexAttribPointer(colorLocation, 3, VertexAttribPointerType.Float, false, stride, 3 * sizeof(float));

        // vertex texture coords
        var texCoordLocation = (int)AttributeLocation.TextureCoord;
        GL.EnableVertexAttribArray(texCoordLocation);
        GL.VertexAttribPointer(texCoordLocation, 2, VertexAttribPointerType.Float, false, stride, 6 * sizeof(float));
        return stride;
    };

    public static readonly VertexDeclaration VertexPositionColor = () =>
    {
        const int stride = 6 * sizeof(float);

        // vertex positions
        var vertexLocation = (int)AttributeLocation.Position;
        GL.EnableVertexAttribArray(vertexLocation);
        GL.VertexAttribPointer(vertexLocation, 3, VertexAttribPointerType.Float, false, stride, 0);

        // vertex colors
        var colorLocation = (int)AttributeLocation.Color;
        GL.EnableVertexAttribArray(colorLocation);
        GL.VertexAttribPointer(colorLocation, 3, VertexAttribPointerType.Float, false, stride, 3 * sizeof(float));
        return stride;
    };

    public static readonly VertexDeclaration VertexPositionNormalColor = () =>
    {
        const int stride = 9 * sizeof(float);

        // vertex positions
        var vertexLocation = (int)AttributeLocation.Position;
        GL.EnableVertexAttribArray(vertexLocation);
        GL.VertexAttribPointer(vertexLocation, 3, VertexAttribPointerType.Float, false, stride, 0);

        // vertex normals
        var normalLocation = (int)AttributeLocation.Normal;
        GL.EnableVertexAttribArray(normalLocation);
        GL.VertexAttribPointer(normalLocation, 3, VertexAttribPointerType.Float, false, stride, 3 * sizeof(float));

        // vertex colors
        var colorLocation = (int)AttributeLocation.Color;
        GL.VertexAttribPointer(colorLocation, 3, VertexAttribPointerType.Float, false, stride, 6 * sizeof(float));
        return stride;
    };

    public static readonly VertexDeclaration VertexPositionNormalColorTexture = () =>
    {
        const int stride = 11 * sizeof(float);

        // vertex positions
        var vertexLocation = (int)AttributeLocation.Position;
        GL.EnableVertexAttribArray(vertexLocation);
        GL.VertexAttribPointer(vertexLocation, 3, VertexAttribPointerType.Float, false, stride, 0);

        // vertex normals
        var normalLocation = (int)AttributeLocation.Normal;
        GL.EnableVertexAttribArray(normalLocation);
        GL.VertexAttribPointer(normalLocation, 3, VertexAttribPointerType.Float, false, stride, 3 * sizeof(float));

        // vertex colors
        var colorLocation = (int)AttributeLocation.Color;
        GL.EnableVertexAttribArray(colorLocation);
        GL.VertexAttribPointer(colorLocation, 3, VertexAttribPointerType.Float, false, stride, 6 * sizeof(float));

        // vertex texture coords
        var texCoordLocation = (int)AttributeLocation.TextureCoord;
        GL.EnableVertexAttribArray(texCoordLocation);
        GL.VertexAttribPointer(texCoordLocation, 2, VertexAttribPointerType.Float, false, stride, 9 * sizeof(float));
        return stride;
    };
}