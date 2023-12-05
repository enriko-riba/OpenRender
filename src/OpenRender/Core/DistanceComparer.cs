using OpenRender.SceneManagement;
using OpenTK.Mathematics;

namespace OpenRender.Core;
public class DistanceComparer(Vector3 cameraPosition) : IComparer<SceneNode>
{
    public int Compare(SceneNode? a, SceneNode? b)
    {
        if (a == null || b == null)
        {
            return 0;
        }

        a.GetPosition(out var aPosition);
        b.GetPosition(out var bPosition);
        var distanceA = Vector3.Distance(aPosition, cameraPosition);
        var distanceB = Vector3.Distance(bPosition, cameraPosition);
        return distanceB.CompareTo(distanceA);
    }
}
