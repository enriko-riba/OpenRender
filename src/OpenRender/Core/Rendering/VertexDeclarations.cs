using OpenTK.Graphics.OpenGL4;

namespace OpenRender.Core.Rendering;

/// <summary>
/// Contains common vertex declarations.
/// </summary>
public static class VertexDeclarations
{
    public static readonly VertexDeclaration VertexPosition = new(
        new AttributeLayout(AttributeLocation.Position, 3, VertexAttribType.Float)
    );

    public static readonly VertexDeclaration VertexPositionTexture = new(new AttributeLayout[] {
    new AttributeLayout(AttributeLocation.Position, 3, VertexAttribType.Float),
    new AttributeLayout(AttributeLocation.TextureCoord, 2, VertexAttribType.Float),
});

    public static readonly VertexDeclaration VertexPosition2DTexture = new(new AttributeLayout[] {
    new AttributeLayout(AttributeLocation.Position, 2, VertexAttribType.Float),
    new AttributeLayout(AttributeLocation.TextureCoord, 2, VertexAttribType.Float),
});

    public static readonly VertexDeclaration VertexPositionNormal = new(new AttributeLayout[] {
new AttributeLayout(AttributeLocation.Position, 3, VertexAttribType.Float),
new AttributeLayout(AttributeLocation.Normal, 3, VertexAttribType.Float),
});

    public static readonly VertexDeclaration VertexPositionNormalTexture = new(new AttributeLayout[] {
new AttributeLayout(AttributeLocation.Position, 3, VertexAttribType.Float),
new AttributeLayout(AttributeLocation.Normal, 3, VertexAttribType.Float),
new AttributeLayout(AttributeLocation.TextureCoord, 2, VertexAttribType.Float),
});

    public static readonly VertexDeclaration VertexPositionColorTexture = new(new AttributeLayout[] {
new AttributeLayout(AttributeLocation.Position, 3, VertexAttribType.Float),
new AttributeLayout(AttributeLocation.Color, 3, VertexAttribType.Float),
new AttributeLayout(AttributeLocation.TextureCoord, 2, VertexAttribType.Float),
});

    public static readonly VertexDeclaration VertexPositionColor = new(new AttributeLayout[] {
new AttributeLayout(AttributeLocation.Position, 3, VertexAttribType.Float),
new AttributeLayout(AttributeLocation.Color, 3, VertexAttribType.Float),
});

    public static readonly VertexDeclaration VertexPositionNormalColor = new(new AttributeLayout[] {
 new AttributeLayout(AttributeLocation.Position, 3, VertexAttribType.Float),
 new AttributeLayout(AttributeLocation.Normal, 3, VertexAttribType.Float),
 new AttributeLayout(AttributeLocation.Color, 3, VertexAttribType.Float),
 });

    public static readonly VertexDeclaration VertexPositionNormalColorTexture = new(new AttributeLayout[] {
 new AttributeLayout(AttributeLocation.Position, 3, VertexAttribType.Float),
 new AttributeLayout(AttributeLocation.Normal, 3, VertexAttribType.Float),
 new AttributeLayout(AttributeLocation.Color, 3, VertexAttribType.Float),
 new AttributeLayout(AttributeLocation.TextureCoord, 2, VertexAttribType.Float),
 });
}