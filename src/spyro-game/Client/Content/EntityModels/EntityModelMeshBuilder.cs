using OpenRender.Core;
using OpenRender.Core.Buffers;
using OpenRender.Core.Rendering;
using OpenTK.Mathematics;

namespace SpyroGame.Client.Content.EntityModels;

internal static class EntityModelMeshBuilder
{
    // Builds a single cube mesh (24 verts, 36 indices) in local space [0..size].
    // Callers position it via SceneNode transforms.
    public static Mesh BuildCubeMesh(Vector3 size, EntityModelFaceTiles tiles, EntityModelAtlas atlas)
    {
        var x0 = 0.0f;
        var y0 = 0.0f;
        var z0 = 0.0f;
        var x1 = size.X;
        var y1 = size.Y;
        var z1 = size.Z;

        var top = AtlasRect(tiles.Top.Col, tiles.Top.Row, atlas.Columns, atlas.Rows);
        var bottom = AtlasRect(tiles.Bottom.Col, tiles.Bottom.Row, atlas.Columns, atlas.Rows);
        var front = AtlasRect(tiles.Front.Col, tiles.Front.Row, atlas.Columns, atlas.Rows);
        var left = AtlasRect(tiles.Left.Col, tiles.Left.Row, atlas.Columns, atlas.Rows);
        var right = AtlasRect(tiles.Right.Col, tiles.Right.Row, atlas.Columns, atlas.Rows);
        var back = AtlasRect(tiles.Back.Col, tiles.Back.Row, atlas.Columns, atlas.Rows);

        var v = new Vertex[24];
        var i = 0;

        // Front (+Z)
        AddQuad(v, ref i,
            normal: new Vector3(0, 0, 1),
            bl: new Vector3(x0, y0, z1),
            br: new Vector3(x1, y0, z1),
            tr: new Vector3(x1, y1, z1),
            tl: new Vector3(x0, y1, z1),
            uv: front);

        // Back (-Z)
        AddQuad(v, ref i,
            normal: new Vector3(0, 0, -1),
            bl: new Vector3(x1, y0, z0),
            br: new Vector3(x0, y0, z0),
            tr: new Vector3(x0, y1, z0),
            tl: new Vector3(x1, y1, z0),
            uv: back);

        // Left (-X)
        AddQuad(v, ref i,
            normal: new Vector3(-1, 0, 0),
            bl: new Vector3(x0, y0, z0),
            br: new Vector3(x0, y0, z1),
            tr: new Vector3(x0, y1, z1),
            tl: new Vector3(x0, y1, z0),
            uv: left);

        // Right (+X)
        AddQuad(v, ref i,
            normal: new Vector3(1, 0, 0),
            bl: new Vector3(x1, y0, z1),
            br: new Vector3(x1, y0, z0),
            tr: new Vector3(x1, y1, z0),
            tl: new Vector3(x1, y1, z1),
            uv: right);

        // Top (+Y)
        AddQuad(v, ref i,
            normal: new Vector3(0, 1, 0),
            bl: new Vector3(x0, y1, z1),
            br: new Vector3(x1, y1, z1),
            tr: new Vector3(x1, y1, z0),
            tl: new Vector3(x0, y1, z0),
            uv: top);

        // Bottom (-Y)
        AddQuad(v, ref i,
            normal: new Vector3(0, -1, 0),
            bl: new Vector3(x0, y0, z0),
            br: new Vector3(x1, y0, z0),
            tr: new Vector3(x1, y0, z1),
            tl: new Vector3(x0, y0, z1),
            uv: bottom);

        var indices = new uint[6 * 6];
        var idx = 0;
        for (uint face = 0; face < 6; face++)
        {
            var baseV = face * 4;
            indices[idx++] = baseV + 0;
            indices[idx++] = baseV + 1;
            indices[idx++] = baseV + 2;
            indices[idx++] = baseV + 0;
            indices[idx++] = baseV + 2;
            indices[idx++] = baseV + 3;
        }

        return new Mesh(VertexDeclarations.VertexPositionNormalTexture, v, indices);
    }

    public static Mesh BuildCubeMesh(Vector3 size, EntityModelCubeUv uvs, int textureWidth, int textureHeight)
    {
        var x0 = 0.0f;
        var y0 = 0.0f;
        var z0 = 0.0f;
        var x1 = size.X;
        var y1 = size.Y;
        var z1 = size.Z;

        var top = PixelRect(uvs.Top, textureWidth, textureHeight);
        var bottom = PixelRect(uvs.Bottom, textureWidth, textureHeight);
        var front = PixelRect(uvs.Front, textureWidth, textureHeight);
        var left = PixelRect(uvs.Left, textureWidth, textureHeight);
        var right = PixelRect(uvs.Right, textureWidth, textureHeight);
        var back = PixelRect(uvs.Back, textureWidth, textureHeight);

        var v = new Vertex[24];
        var i = 0;

        // Front (+Z) -> Back Face in Minecraft (South)
        AddQuad(v, ref i,
            normal: new Vector3(0, 0, 1),
            bl: new Vector3(x0, y0, z1),
            br: new Vector3(x1, y0, z1),
            tr: new Vector3(x1, y1, z1),
            tl: new Vector3(x0, y1, z1),
            uv: back);

        // Back (-Z) -> Front Face in Minecraft (North)
        AddQuad(v, ref i,
            normal: new Vector3(0, 0, -1),
            bl: new Vector3(x1, y0, z0),
            br: new Vector3(x0, y0, z0),
            tr: new Vector3(x0, y1, z0),
            tl: new Vector3(x1, y1, z0),
            uv: front);

        // Left (-X) -> Right Face in Minecraft (West)
        AddQuad(v, ref i,
            normal: new Vector3(-1, 0, 0),
            bl: new Vector3(x0, y0, z0),
            br: new Vector3(x0, y0, z1),
            tr: new Vector3(x0, y1, z1),
            tl: new Vector3(x0, y1, z0),
            uv: right);

        // Right (+X) -> Left Face in Minecraft (East)
        AddQuad(v, ref i,
            normal: new Vector3(1, 0, 0),
            bl: new Vector3(x1, y0, z1),
            br: new Vector3(x1, y0, z0),
            tr: new Vector3(x1, y1, z0),
            tl: new Vector3(x1, y1, z1),
            uv: left);

        // Top (+Y)
        AddQuad(v, ref i,
            normal: new Vector3(0, 1, 0),
            bl: new Vector3(x0, y1, z1),
            br: new Vector3(x1, y1, z1),
            tr: new Vector3(x1, y1, z0),
            tl: new Vector3(x0, y1, z0),
            uv: top);

        // Bottom (-Y)
        AddQuad(v, ref i,
            normal: new Vector3(0, -1, 0),
            bl: new Vector3(x0, y0, z0),
            br: new Vector3(x1, y0, z0),
            tr: new Vector3(x1, y0, z1),
            tl: new Vector3(x0, y0, z1),
            uv: bottom);

        var indices = new uint[6 * 6];
        var idx = 0;
        for (uint face = 0; face < 6; face++)
        {
            var baseV = face * 4;
            indices[idx++] = baseV + 0;
            indices[idx++] = baseV + 1;
            indices[idx++] = baseV + 2;
            indices[idx++] = baseV + 0;
            indices[idx++] = baseV + 2;
            indices[idx++] = baseV + 3;
        }

        return new Mesh(VertexDeclarations.VertexPositionNormalTexture, v, indices);
    }

    private static (float U0, float V0, float U1, float V1) AtlasRect(int col, int row, int cols, int rows)
    {
        var u0 = col / (float)cols;
        var u1 = (col + 1) / (float)cols;

        // JSON row 0 is top row in the source image.
        // OpenRender flips textures on load for Texture2D, so convert to OpenGL UVs (v=0 bottom).
        var v0 = 1.0f - ((row + 1) / (float)rows);
        var v1 = 1.0f - (row / (float)rows);

        return (u0, v0, u1, v1);
    }

    private static (float U0, float V0, float U1, float V1) PixelRect(EntityModelUvRect rect, int textureWidth, int textureHeight)
    {
        var w = Math.Max(1, textureWidth);
        var h = Math.Max(1, textureHeight);

        // To avoid bleeding/sampling fully transparent padding (common in resource packs),
        // sample *inside* the rectangle by half a texel on each edge.
        // This keeps "nearest" sampling stable and makes linear sampling less likely to hit transparent borders.
        var xStart = (float)rect.X;
        var yStart = (float)rect.Y;
        var xEnd = (float)(rect.X + rect.W);
        var yEnd = (float)(rect.Y + rect.H);

        // Allow negative widths/heights to flip a face.
        // Example: W = -16 means "use 16px width, but flip horizontally".
        var flipU = xEnd < xStart;
        var flipV = yEnd < yStart;

        var x0 = Math.Min(xStart, xEnd);
        var x1 = Math.Max(xStart, xEnd);
        var y0 = Math.Min(yStart, yEnd);
        var y1 = Math.Max(yStart, yEnd);

        const float inset = 0.5f;
        var absW = Math.Abs(rect.W);
        var absH = Math.Abs(rect.H);

        if (absW > 1)
        {
            x0 += inset;
            x1 -= inset;
        }
        if (absH > 1)
        {
            y0 += inset;
            y1 -= inset;
        }

        x0 = Math.Clamp(x0, 0.0f, w);
        x1 = Math.Clamp(x1, 0.0f, w);
        y0 = Math.Clamp(y0, 0.0f, h);
        y1 = Math.Clamp(y1, 0.0f, h);

        var u0 = x0 / w;
        var u1 = x1 / w;

        if (flipU)
        {
            (u0, u1) = (u1, u0);
        }

        // JSON pixel coords are top-left in the source image.
        // OpenRender flips textures on load for Texture2D, so convert to OpenGL UVs (v=0 bottom).
        var v0 = 1.0f - (y1 / h);
        var v1 = 1.0f - (y0 / h);

        if (flipV)
        {
            (v0, v1) = (v1, v0);
        }

        return (u0, v0, u1, v1);
    }

    private static void AddQuad(
        Vertex[] vertices,
        ref int writeIndex,
        in Vector3 normal,
        in Vector3 bl,
        in Vector3 br,
        in Vector3 tr,
        in Vector3 tl,
        in (float U0, float V0, float U1, float V1) uv)
    {
        vertices[writeIndex++] = new Vertex(bl, normal, new Vector2(uv.U0, uv.V0));
        vertices[writeIndex++] = new Vertex(br, normal, new Vector2(uv.U1, uv.V0));
        vertices[writeIndex++] = new Vertex(tr, normal, new Vector2(uv.U1, uv.V1));
        vertices[writeIndex++] = new Vertex(tl, normal, new Vector2(uv.U0, uv.V1));
    }
}
