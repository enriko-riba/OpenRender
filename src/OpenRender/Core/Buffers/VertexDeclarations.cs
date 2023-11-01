using OpenTK.Graphics.OpenGL4;

namespace OpenRender.Core.Buffers;

/// <summary>
/// Contains common vertex declarations.
/// </summary>
public static class VertexDeclarations
{
    public static readonly VertexDeclaration VertexPosition = new(
        new VertexAttribLayout(VertexAttribLocation.Position, 3, VertexAttribType.Float)
    );

    public static readonly VertexDeclaration VertexPositionTexture = new(new VertexAttribLayout[] {
    new VertexAttribLayout(VertexAttribLocation.Position, 3, VertexAttribType.Float),
    new VertexAttribLayout(VertexAttribLocation.TextureCoord, 2, VertexAttribType.Float),
});

    public static readonly VertexDeclaration VertexPosition2DTexture = new(new VertexAttribLayout[] {
    new VertexAttribLayout(VertexAttribLocation.Position, 2, VertexAttribType.Float),
    new VertexAttribLayout(VertexAttribLocation.TextureCoord, 2, VertexAttribType.Float),
});

    public static readonly VertexDeclaration VertexPositionNormal = new(new VertexAttribLayout[] {
new VertexAttribLayout(VertexAttribLocation.Position, 3, VertexAttribType.Float),
new VertexAttribLayout(VertexAttribLocation.Normal, 3, VertexAttribType.Float),
});

    public static readonly VertexDeclaration VertexPositionNormalTexture = new(new VertexAttribLayout[] {
new VertexAttribLayout(VertexAttribLocation.Position, 3, VertexAttribType.Float),
new VertexAttribLayout(VertexAttribLocation.Normal, 3, VertexAttribType.Float),
new VertexAttribLayout(VertexAttribLocation.TextureCoord, 2, VertexAttribType.Float),
});

    public static readonly VertexDeclaration VertexPositionColorTexture = new(new VertexAttribLayout[] {
new VertexAttribLayout(VertexAttribLocation.Position, 3, VertexAttribType.Float),
new VertexAttribLayout(VertexAttribLocation.Color, 3, VertexAttribType.Float),
new VertexAttribLayout(VertexAttribLocation.TextureCoord, 2, VertexAttribType.Float),
});

    public static readonly VertexDeclaration VertexPositionColor = new(new VertexAttribLayout[] {
new VertexAttribLayout(VertexAttribLocation.Position, 3, VertexAttribType.Float),
new VertexAttribLayout(VertexAttribLocation.Color, 3, VertexAttribType.Float),
});

    public static readonly VertexDeclaration VertexPositionNormalColor = new(new VertexAttribLayout[] {
 new VertexAttribLayout(VertexAttribLocation.Position, 3, VertexAttribType.Float),
 new VertexAttribLayout(VertexAttribLocation.Normal, 3, VertexAttribType.Float),
 new VertexAttribLayout(VertexAttribLocation.Color, 3, VertexAttribType.Float),
 });

    public static readonly VertexDeclaration VertexPositionNormalColorTexture = new(new VertexAttribLayout[] {
 new VertexAttribLayout(VertexAttribLocation.Position, 3, VertexAttribType.Float),
 new VertexAttribLayout(VertexAttribLocation.Normal, 3, VertexAttribType.Float),
 new VertexAttribLayout(VertexAttribLocation.Color, 3, VertexAttribType.Float),
 new VertexAttribLayout(VertexAttribLocation.TextureCoord, 2, VertexAttribType.Float),
 });
}