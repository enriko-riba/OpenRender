using DarkVox.Shared.World.Registry;
using OpenTK.Mathematics;

namespace DarkVox.Shared.Abstractions;

/// <summary>
/// Minimal seam for server-owned world edits (break/place).
/// Client can submit edits; server applies them to authoritative chunk data.
/// </summary>
public interface IBlockEditService
{
    void ApplyBlockEdit(Vector3i worldPosition, BlockId blockId, bool isBreaking);
}
