using OpenRender.Core;
using OpenRender.SceneManagement;
using OpenTK.Mathematics;

namespace playground;

internal class RandomNode : SceneNode
{
    private readonly int rotationAxis;

    public RandomNode(Mesh mesh) : base(mesh)
    {
        const int Area = 500;
        SetPosition(new Vector3(Random.Shared.Next(-Area, Area + 1), Random.Shared.Next(-Area, Area + 1), Random.Shared.Next(-Area, Area + 1)));
        SetScale(Random.Shared.Next(2, 5));
        rotationAxis = Random.Shared.Next(0, 2);
    }

    public override void OnUpdate(Scene scene, double elapsed)
    {
        var factor = 2f;
        factor *= (float)elapsed;
        var rot = AngleRotation;

        if (rotationAxis == 0) rot.X += (float)Random.Shared.NextDouble() * factor;
        if (rotationAxis == 1) rot.Y += (float)Random.Shared.NextDouble() * factor;
        if (rotationAxis == 2) rot.Z += (float)Random.Shared.NextDouble() * factor;

        SetRotation(rot);
        base.OnUpdate(scene, elapsed);
    }
}
