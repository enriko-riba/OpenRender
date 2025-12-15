using OpenTK.Mathematics;
using SpyroGame.World;

namespace SpyroGame.Shared.Abstractions;

/// <summary>
/// Minimal seam for server-owned world edits (break/place).
/// Client can submit edits; server applies them to authoritative chunk data.
/// </summary>
public interface IBlockEditService
{
    void ApplyBlockEdit(Vector3i worldPosition, BlockId blockId, bool isBreaking);
}
