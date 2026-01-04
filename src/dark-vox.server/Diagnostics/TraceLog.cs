using System.Diagnostics;
using DarkVox.Shared.Abstractions;

namespace DarkVox.Server.Diagnostics;

public sealed class TraceLog : ILog
{
    public void Info(string message) => Trace.TraceInformation(message);
    public void Warn(string message) => Trace.TraceWarning(message);
    public void Error(string message) => Trace.TraceError(message);
}
