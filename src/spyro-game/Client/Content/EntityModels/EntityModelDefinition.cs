using System.Text.Json;
using System.Text.Json.Serialization;
using OpenTK.Mathematics;

namespace SpyroGame.Client.Content.EntityModels;

// Minimal Bedrock/GeckoLib-inspired data model (JSON-driven).
// This is intentionally small: enough to define bones, cubes, and UVs.

internal sealed record EntityModelDefinition(
    string? FormatVersion,
    EntityModelTexture Texture,
    IReadOnlyList<EntityModelBone> Bones);

internal sealed record EntityModelTexture(
    string Path,
    int Width,
    int Height,
    EntityModelAtlas? Atlas = null);

internal sealed record EntityModelAtlas(
    int Columns,
    int Rows,
    EntityModelFaceTiles? FaceTiles = null);

internal sealed record EntityModelFaceTiles(
    EntityModelTile Top,
    EntityModelTile Bottom,
    EntityModelTile Front,
    EntityModelTile Left,
    EntityModelTile Right,
    EntityModelTile Back);

internal sealed record EntityModelTile(int Col, int Row);

internal sealed record EntityModelBone(
    string Name,
    string? Parent,
    Vector3 Pivot,
    Vector3 Rotation,
    IReadOnlyList<EntityModelCube> Cubes);

internal sealed record EntityModelCube(
    // Optional label for debugging/authoring (has no semantic meaning to the renderer).
    string? Name,
    Vector3 Origin,
    Vector3? Size,
    Vector3 Pivot,
    Vector3 Rotation,
    // If present, overrides the model atlas face-tiles for this cube.
    EntityModelFaceTiles? FaceTiles = null,
    // Optional per-face UV mapping in pixel coordinates (origin at top-left of the texture).
    EntityModelCubeUv? Uv = null);

// Per-face UV rectangles in pixel coordinates (origin at top-left of texture).
// Each face specifies X, Y, W, H in pixels.
internal sealed record EntityModelCubeUv(
    EntityModelUvRect Top,
    EntityModelUvRect Bottom,
    EntityModelUvRect Front,
    EntityModelUvRect Left,
    EntityModelUvRect Right,
    EntityModelUvRect Back);

internal sealed record EntityModelUvRect(int X, int Y, int W, int H);

