using OpenRender.SceneManagement;
using OpenTK.Mathematics;

namespace OpenRender.Core;
public class DistanceComparer : IComparer<SceneNode>
{
    private Vector3 cameraPosition;

    public DistanceComparer(Vector3 cameraPosition)
    {
        this.cameraPosition = cameraPosition;
    }

    public int Compare(SceneNode a, SceneNode b)
    {
        a.GetPosition(out var aPosition);
        b.GetPosition(out var bPosition);
        var distanceA = Vector3.Distance(aPosition, cameraPosition);
        var distanceB = Vector3.Distance(bPosition, cameraPosition);
        return distanceB.CompareTo(distanceA);
    }
}
