using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace OpenRender;

public static class Utility
{
    internal static GCHandle debugProcCallbackHandle = GCHandle.Alloc(DebugMessageDelegate);
    internal static DebugProc DebugMessageDelegate = OnDebugMessage;

    [DebuggerStepThrough]
    private static void OnDebugMessage(
        DebugSource source,     // Source of the debugging message.
        DebugType type,         // Type of the debugging message.
        int id,                 // ID associated with the message.
        DebugSeverity severity, // Severity of the message.
        int length,             // Length of the string in pMessage.
        IntPtr pMessage,        // Pointer to message string.
        IntPtr pUserParam)      // The pointer you gave to OpenGL
    {
        // In order to access the string pointed to by pMessage, you can use Marshal
        // class to copy its contents to a C# string without unsafe code. You can
        // also use the new function Marshal.PtrToStringUTF8 since .NET Core 1.1.
        var message = Marshal.PtrToStringUTF8(pMessage, length);

        Console.WriteLine("[{0} source={1} type={2} id={3}] {4}", severity, source, type, id, message);

        // Potentially, you may want to throw from the function for certain severity messages.
        if (type == DebugType.DebugTypeError)
        {
            throw new Exception(message);
        }
    }

    /// <summary>
    /// Creates a rotation quaternion which rotates the eyeDirection to point to the targetDirection
    /// </summary>
    /// <param name="eyeDirection"></param>
    /// <param name="targetDirection"></param>
    /// <param name="rotation"></param>
    public static void CreateLookAtRotation(in Vector3 eyeDirection, in Vector3 targetDirection, out Quaternion rotation)
    {
        if (targetDirection == Vector3.Zero)
            throw new InvalidOperationException("Target can not be Vector3.Zero");

        eyeDirection.Normalize();
        targetDirection.Normalize();
        var vNormal = Vector3.Cross(eyeDirection, targetDirection);
        if (vNormal != Vector3.Zero)
            vNormal.Normalize();

        var angle = Vector3.Dot(eyeDirection, targetDirection);
        angle = MathF.Acos(angle);
        var halfAngle = angle * 0.5f;  //   half angle
        var halfSine = MathF.Sin(halfAngle);

        rotation = new Quaternion(halfSine * vNormal.X,
                                  halfSine * vNormal.Y,
                                  halfSine * vNormal.Z,
                                  MathF.Cos(halfSine));
    }

    /// <summary>
    /// Creates a rotation quaternion which rotates the eyeDirection to point to the targetDirection
    /// </summary>
    /// <param name="eyeDirection"></param>
    /// <param name="targetDirection"></param>
    /// <returns></returns>
    public static Quaternion CreateLookAtRotation(in Vector3 eyeDirection, in Vector3 targetDirection)
    {
        CreateLookAtRotation(in eyeDirection, in targetDirection, out var tmp);
        return tmp;
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="color"></param>
    /// <returns></returns>
    public static Vector3 ToVector3(this in Color4 color) => new(color.R, color.G, color.B);
}
