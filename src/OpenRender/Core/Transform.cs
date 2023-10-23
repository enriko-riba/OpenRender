using OpenTK.Mathematics;

namespace OpenRender.Core;

public struct Transform
{
    public Transform()
    {
        scaleMatrix = Matrix4.Identity;
        rotationMatrix = Matrix4.Identity;
        worldMatrix = Matrix4.Identity;
    }
    public Vector3 Position;
    public Vector3 Scale;
    public Quaternion Rotation;

    public Matrix4 scaleMatrix;
    public Matrix4 rotationMatrix;
    public Matrix4 worldMatrix;

    public void UpdateMatrix(in Transform? parent = null)
    {
        Matrix4.CreateScale(Scale, out scaleMatrix);
        Matrix4.CreateFromQuaternion(Rotation, out rotationMatrix);
        Matrix4.Mult(scaleMatrix, rotationMatrix, out worldMatrix);
        worldMatrix.Row3.Xyz = Position;    //  sets the translation

        if (parent.HasValue)
        {
            Matrix4.Mult(worldMatrix, parent.Value.worldMatrix, out worldMatrix);
        }
    }
}
