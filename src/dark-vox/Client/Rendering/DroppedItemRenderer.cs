using OpenRender.Core;
using OpenRender.Core.Rendering;
using OpenRender.SceneManagement;
using OpenTK.Mathematics;
using DarkVox.Shared.State;
using DarkVox.World;

namespace DarkVox.Client.Rendering;

public sealed class DroppedItemRenderer(Scene scene) : IDisposable
{
    private readonly Dictionary<int, SceneNode> itemNodes = [];
    private readonly Mesh blockMesh = CreateBlockMesh(0.25f);
    private readonly Mesh flatItemMesh = CreateFlatItemMesh(0.25f);

    public void Update(DroppedItemSnapshot[]? items, double elapsedSeconds)
    {
        if (items == null)
        {
            Clear();
            return;
        }

        var currentIds = new HashSet<int>();
        foreach (var item in items)
        {
            currentIds.Add(item.Id);

            if (!itemNodes.TryGetValue(item.Id, out var node))
            {
                var material = ItemTextureManager.GetMaterial(item.Item);
                var mesh = ItemTextureManager.IsBlockItem(item.Item) ? blockMesh : flatItemMesh;

                node = new SceneNode(mesh, material)
                {
                    IsVisible = true
                };

                scene.AddNode(node);
                itemNodes[item.Id] = node;
            }

            // Animate: Bob and rotate
            var time = (float)scene.SceneManager.Time;
            var bobOffset = (float)Math.Sin(time * 2.0f + item.Id) * 0.1f;
            var rotAngle = time * 90.0f; // Degrees per second

            node.SetPosition(item.Position + new Vector3(0, bobOffset, 0));
            node.SetRotation(new Vector3(0, MathHelper.DegreesToRadians(rotAngle), 0));
        }

        // Remove missing items
        var toRemove = new List<int>();
        foreach (var id in itemNodes.Keys)
        {
            if (!currentIds.Contains(id))
            {
                toRemove.Add(id);
            }
        }

        foreach (var id in toRemove)
        {
            if (itemNodes.TryGetValue(id, out var node))
            {
                scene.RemoveNode(node);
                itemNodes.Remove(id);
            }
        }
    }

    private void Clear()
    {
        foreach (var node in itemNodes.Values)
        {
            scene.RemoveNode(node);
        }
        itemNodes.Clear();
    }

    public void Dispose() => Clear();

    private static Mesh CreateBlockMesh(float size)
    {
        var h = size * 0.5f;
        var t1 = 1.0f / 3.0f;
        var t2 = 2.0f / 3.0f;

        // 6 faces * 4 verts = 24 vertices
        var vertices = new Vertex[]
        {
            // Front (Side)
            new(new Vector3(-h, -h,  h), Vector3.UnitZ, new Vector2(t2, 0)),
            new(new Vector3( h, -h,  h), Vector3.UnitZ, new Vector2(1, 0)),
            new(new Vector3( h,  h,  h), Vector3.UnitZ, new Vector2(1, 1)),
            new(new Vector3(-h,  h,  h), Vector3.UnitZ, new Vector2(t2, 1)),
            // Back (Side)
            new(new Vector3( h, -h, -h), -Vector3.UnitZ, new Vector2(t2, 0)),
            new(new Vector3(-h, -h, -h), -Vector3.UnitZ, new Vector2(1, 0)),
            new(new Vector3(-h,  h, -h), -Vector3.UnitZ, new Vector2(1, 1)),
            new(new Vector3( h,  h, -h), -Vector3.UnitZ, new Vector2(t2, 1)),
            // Top (Top)
            new(new Vector3(-h,  h,  h), Vector3.UnitY, new Vector2(0, 0)),
            new(new Vector3( h,  h,  h), Vector3.UnitY, new Vector2(t1, 0)),
            new(new Vector3( h,  h, -h), Vector3.UnitY, new Vector2(t1, 1)),
            new(new Vector3(-h,  h, -h), Vector3.UnitY, new Vector2(0, 1)),
            // Bottom (Bottom)
            new(new Vector3(-h, -h, -h), -Vector3.UnitY, new Vector2(t1, 0)),
            new(new Vector3( h, -h, -h), -Vector3.UnitY, new Vector2(t2, 0)),
            new(new Vector3( h, -h,  h), -Vector3.UnitY, new Vector2(t2, 1)),
            new(new Vector3(-h, -h,  h), -Vector3.UnitY, new Vector2(t1, 1)),
            // Right (Side)
            new(new Vector3( h, -h,  h), Vector3.UnitX, new Vector2(t2, 0)),
            new(new Vector3( h, -h, -h), Vector3.UnitX, new Vector2(1, 0)),
            new(new Vector3( h,  h, -h), Vector3.UnitX, new Vector2(1, 1)),
            new(new Vector3( h,  h,  h), Vector3.UnitX, new Vector2(t2, 1)),
            // Left (Side)
            new(new Vector3(-h, -h, -h), -Vector3.UnitX, new Vector2(t2, 0)),
            new(new Vector3(-h, -h,  h), -Vector3.UnitX, new Vector2(1, 0)),
            new(new Vector3(-h,  h,  h), -Vector3.UnitX, new Vector2(1, 1)),
            new(new Vector3(-h,  h, -h), -Vector3.UnitX, new Vector2(t2, 1)),
        };

        var indices = new uint[]
        {
            0, 1, 2, 2, 3, 0,       // Front
            4, 5, 6, 6, 7, 4,       // Back
            8, 9, 10, 10, 11, 8,    // Top
            12, 13, 14, 14, 15, 12, // Bottom
            16, 17, 18, 18, 19, 16, // Right
            20, 21, 22, 22, 23, 20  // Left
        };

        return new Mesh(OpenRender.Core.Buffers.VertexDeclarations.VertexPositionNormalTexture, vertices, indices);
    }

    private static Mesh CreateFlatItemMesh(float size)
    {
        var h = size * 0.5f;

        // Double-sided quad
        var vertices = new Vertex[]
        {
            // Front
            new(new Vector3(-h, -h, 0), Vector3.UnitZ, new Vector2(0, 0)),
            new(new Vector3( h, -h, 0), Vector3.UnitZ, new Vector2(1, 0)),
            new(new Vector3( h,  h, 0), Vector3.UnitZ, new Vector2(1, 1)),
            new(new Vector3(-h,  h, 0), Vector3.UnitZ, new Vector2(0, 1)),
            // Back
            new(new Vector3( h, -h, 0), -Vector3.UnitZ, new Vector2(1, 0)),
            new(new Vector3(-h, -h, 0), -Vector3.UnitZ, new Vector2(0, 0)),
            new(new Vector3(-h,  h, 0), -Vector3.UnitZ, new Vector2(0, 1)),
            new(new Vector3( h,  h, 0), -Vector3.UnitZ, new Vector2(1, 1)),
        };

        var indices = new uint[]
        {
            0, 1, 2, 2, 3, 0,
            4, 5, 6, 6, 7, 4
        };

        return new Mesh(OpenRender.Core.Buffers.VertexDeclarations.VertexPositionNormalTexture, vertices, indices);
    }
}
