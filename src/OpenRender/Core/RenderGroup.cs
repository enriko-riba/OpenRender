namespace OpenRender.Core;

public enum RenderGroup
{
    /// <summary>
    /// Rendered first, usually just one node in group.
    /// </summary>
    SkyBox,

    /// <summary>
    /// Default group. Used for opaque nodes as those do not require distance sorting.
    /// </summary>
    Default,

    /// <summary>
    /// For nodes with transparency. This group is rendered after the default group and requires distance sorting.
    /// </summary>
    DistanceSorted,

    /// <summary>
    /// Top most nodes, usually used for UI or 2D nodes. Not sorted.
    /// </summary>
    UI,
}
