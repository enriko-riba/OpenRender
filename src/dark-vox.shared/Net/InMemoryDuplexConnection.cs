using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

namespace DarkVox.Shared.Net;

public static class InMemoryDuplexConnection
{
    public static (IClientConnection<TClientToServer, TServerToClient> Client, IServerConnection<TClientToServer, TServerToClient> Server)
        CreatePair<TClientToServer, TServerToClient>()
        where TClientToServer : notnull
        where TServerToClient : notnull
    {
        var c2s = new BlockingCollection<TClientToServer>(new ConcurrentQueue<TClientToServer>());
        var s2c = new BlockingCollection<TServerToClient>(new ConcurrentQueue<TServerToClient>());

        return (new ClientEndpoint<TClientToServer, TServerToClient>(c2s, s2c), new ServerEndpoint<TClientToServer, TServerToClient>(c2s, s2c));
    }

    private sealed class ClientEndpoint<TClientToServer, TServerToClient>(BlockingCollection<TClientToServer> c2s, BlockingCollection<TServerToClient> s2c)
        : IClientConnection<TClientToServer, TServerToClient>
        where TClientToServer : notnull
        where TServerToClient : notnull
    {
        public void Send(TClientToServer message) => c2s.Add(message);

        public bool TryReceive([NotNullWhen(true)] out TServerToClient? message) => s2c.TryTake(out message);

        public bool WaitReceive(TimeSpan timeout, [NotNullWhen(true)] out TServerToClient? message)
            => s2c.TryTake(out message, timeout);
    }

    private sealed class ServerEndpoint<TClientToServer, TServerToClient>(BlockingCollection<TClientToServer> c2s, BlockingCollection<TServerToClient> s2c)
        : IServerConnection<TClientToServer, TServerToClient>
        where TClientToServer : notnull
        where TServerToClient : notnull
    {
        public bool TryReceive([NotNullWhen(true)] out TClientToServer? message) => c2s.TryTake(out message);

        public bool WaitReceive(TimeSpan timeout, [NotNullWhen(true)] out TClientToServer? message)
            => c2s.TryTake(out message, timeout);

        public void Send(TServerToClient message) => s2c.Add(message);
    }
}
