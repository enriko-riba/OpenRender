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
        var message = Marshal.PtrToStringUTF8(pMessage, length);
        var formattedMessage = $"[GL source={source} type={type} id={id}] {message}";

        // Suppress shader recompilation warnings (id=131218) - these are driver optimizations, not errors
        // The driver recompiles shaders based on GL state for better performance, this is expected behavior
        if (id == 131218 && type == DebugType.DebugTypePerformance)
        {
            return; // Silently ignore shader recompilation notifications
        }

        // Map OpenGL debug severity to Log levels
        switch (severity)
        {
            case DebugSeverity.DebugSeverityNotification:
                Log.Debug(formattedMessage);
                break;
            
            case DebugSeverity.DebugSeverityLow:
                Log.Info(formattedMessage);
                break;
            
            case DebugSeverity.DebugSeverityMedium:
                Log.Warn(formattedMessage);
                break;
            
            case DebugSeverity.DebugSeverityHigh:
                Log.Error(formattedMessage);
                break;
        }

        if (type == DebugType.DebugTypeError)
        {
            throw new Exception(message);
        }
    }

    public static bool IsExtensionSupported(string name)
    {
        var n = GL.GetInteger(GetPName.NumExtensions);
        for (var i = 0; i < n; i++)
        {
            var extension = GL.GetString(StringNameIndexed.Extensions, (uint)i)!;
            if (extension == name) return true;
        }
        return false;
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
    /// Creates a Vector3 from a Color4.
    /// </summary>
    /// <param name="color"></param>
    /// <returns></returns>
    public static Vector3 ToVector3(this in Color4 color) => new(color.R, color.G, color.B);
}
