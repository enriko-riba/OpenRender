using System.Diagnostics.CodeAnalysis;

namespace SpyroGame.Shared.Net;

public interface IClientConnection<TClientToServer, TServerToClient>
{
    void Send(TClientToServer message);
    bool TryReceive([NotNullWhen(true)] out TServerToClient? message);
    bool WaitReceive(TimeSpan timeout, [NotNullWhen(true)] out TServerToClient? message);
}

public interface IServerConnection<TClientToServer, TServerToClient>
{
    bool TryReceive([NotNullWhen(true)] out TClientToServer? message);
    bool WaitReceive(TimeSpan timeout, [NotNullWhen(true)] out TClientToServer? message);
    void Send(TServerToClient message);
}
