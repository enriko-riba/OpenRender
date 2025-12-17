using OpenRender.Core.Rendering;
using OpenTK.Mathematics;

namespace SpyroGame.Client.Mobs;

internal static class MobBlockGeometry
{
    private const float Half = 0.5f;
    private const int AtlasCols = 3;
    private const int AtlasRows = 2;

    public static (Vertex[] Vertices, uint[] Indices) CreateCoreyBlock()
    {
        // Atlas layout (300x200, frames 100x100).
        // IMPORTANT: OpenRender flips textures vertically on load (StbImageSharp flip=1)
        // so we should use standard OpenGL UVs (v=0 is bottom).
        // Row 0 (top):    top, bottom, front
        // Row 1 (bottom): left, right, back
        var top = AtlasRect(col: 0, row: 0);
        var bottom = AtlasRect(col: 1, row: 0);
        var front = AtlasRect(col: 2, row: 0);
        var left = AtlasRect(col: 0, row: 1);
        var right = AtlasRect(col: 1, row: 1);
        var back = AtlasRect(col: 2, row: 1);

        // 6 faces * 4 vertices.
        var v = new Vertex[24];
        var i = 0;

        // Winding: vertices are provided in CCW order as seen from outside the cube.

        // Front (+Z)
        AddQuad(v, ref i,
            normal: new Vector3(0, 0, 1),
            bl: new Vector3(-Half, -Half, +Half),
            br: new Vector3(+Half, -Half, +Half),
            tr: new Vector3(+Half, +Half, +Half),
            tl: new Vector3(-Half, +Half, +Half),
            uv: front);

        // Back (-Z) (NOTE: winding must be flipped vs front so cross product points -Z)
        AddQuad(v, ref i,
            normal: new Vector3(0, 0, -1),
            bl: new Vector3(+Half, -Half, -Half),
            br: new Vector3(-Half, -Half, -Half),
            tr: new Vector3(-Half, +Half, -Half),
            tl: new Vector3(+Half, +Half, -Half),
            uv: back);

        // Left (-X)
        AddQuad(v, ref i,
            normal: new Vector3(-1, 0, 0),
            bl: new Vector3(-Half, -Half, -Half),
            br: new Vector3(-Half, -Half, +Half),
            tr: new Vector3(-Half, +Half, +Half),
            tl: new Vector3(-Half, +Half, -Half),
            uv: left);

        // Right (+X)
        AddQuad(v, ref i,
            normal: new Vector3(1, 0, 0),
            bl: new Vector3(+Half, -Half, +Half),
            br: new Vector3(+Half, -Half, -Half),
            tr: new Vector3(+Half, +Half, -Half),
            tl: new Vector3(+Half, +Half, +Half),
            uv: right);

        // Top (+Y)
        AddQuad(v, ref i,
            normal: new Vector3(0, 1, 0),
            bl: new Vector3(-Half, +Half, +Half),
            br: new Vector3(+Half, +Half, +Half),
            tr: new Vector3(+Half, +Half, -Half),
            tl: new Vector3(-Half, +Half, -Half),
            uv: top);

        // Bottom (-Y)
        AddQuad(v, ref i,
            normal: new Vector3(0, -1, 0),
            bl: new Vector3(-Half, -Half, -Half),
            br: new Vector3(+Half, -Half, -Half),
            tr: new Vector3(+Half, -Half, +Half),
            tl: new Vector3(-Half, -Half, +Half),
            uv: bottom);

        // Indices: 6 quads.
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

        return (v, indices);
    }

    private static (float U0, float V0, float U1, float V1) AtlasRect(int col, int row)
    {
        var u0 = col / (float)AtlasCols;
        var u1 = (col + 1) / (float)AtlasCols;

        // Row 0 is the TOP row in the source image. With standard OpenGL UVs (v=0 bottom),
        // top row maps to v in [0.5, 1.0] when AtlasRows=2.
        var v1 = 1.0f - (row / (float)AtlasRows);           // top edge
        var v0 = 1.0f - ((row + 1) / (float)AtlasRows);     // bottom edge

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
        // Standard UV mapping (v=0 bottom, v=1 top)
        vertices[writeIndex++] = new Vertex(bl, normal, new Vector2(uv.U0, uv.V0));
        vertices[writeIndex++] = new Vertex(br, normal, new Vector2(uv.U1, uv.V0));
        vertices[writeIndex++] = new Vertex(tr, normal, new Vector2(uv.U1, uv.V1));
        vertices[writeIndex++] = new Vertex(tl, normal, new Vector2(uv.U0, uv.V1));
    }
}
