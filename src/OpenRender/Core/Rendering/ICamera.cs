﻿using OpenTK.Mathematics;

namespace OpenRender.Core.Rendering;
public interface ICamera
{
    Matrix4 ProjectionMatrix { get; }
    Matrix4 ViewMatrix { get; }
    Matrix4 ViewProjectionMatrix { get; }
    Vector3 Front { get; }
    Vector3 Up { get; }
    Vector3 Right { get; }
    Vector3 Position { get; set; }
    float Fov { get; set; }
    float AspectRatio { get; set; }
    void AddRotation(float yawDegrees, float pitchDegrees, float rollDegrees);
    /// <summary>
    /// If dirty, updates camera matrices.
    /// </summary>
    /// <returns>true if camera has been updated else false</returns>
    bool Update();
}