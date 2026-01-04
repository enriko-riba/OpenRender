using OpenRender.Core.Rendering;
using OpenTK.Mathematics;
using System;

namespace DarkVox.Components;

public interface IDayNightTimeProvider
{
    float DayFactor { get; }
    LightUniform DirLight { get; }
    TimeSpan TimeOfDay { get; }
    Vector3 SunDirection { get; }
}
