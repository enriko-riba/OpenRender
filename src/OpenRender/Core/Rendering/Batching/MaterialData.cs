﻿using OpenTK.Mathematics;
using System.Runtime.InteropServices;

namespace OpenRender.Core.Rendering.Batching;
[StructLayout(LayoutKind.Explicit, Size = 64)]
public struct MaterialData
{
    [FieldOffset(0)]
    public Vector3 Diffuse;
    [FieldOffset(16)]
    public Vector3 Emissive;
    [FieldOffset(32)]
    public Vector3 Specular;
    [FieldOffset(44)]
    public float Shininess;
    [FieldOffset(48)]
    public float DetailTextureScaleFactor;
    [FieldOffset(52)]
    public float DetailTextureBlendFactor;
    //the Size is effectively adding the padding to 64 bytes        
}
