using OpenRender.Core;
using OpenRender.SceneManagement;
using OpenTK.Mathematics;

namespace Samples.Batching;

internal class RandomNode : SceneNode
{
    private readonly int rotationAxis;

    public RandomNode(Mesh mesh, Material material, int area) : base(mesh, material)
    {
        SetScale(Random.Shared.Next(1, 3) / 2f);
        SetPosition(new Vector3(Random.Shared.Next(-area, area + 1), Random.Shared.Next(-area, area + 1), Random.Shared.Next(-area, area + 1)));
        SetRotation(new Vector3(Random.Shared.Next(0, 360), Random.Shared.Next(0, 360), Random.Shared.Next(0, 360)));
        rotationAxis = Random.Shared.Next(0, 2);
        IsBatchingAllowed = true;
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
