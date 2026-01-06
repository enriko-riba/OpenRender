using OpenRender.SceneManagement;
using OpenRender.Text;
using OpenTK.Mathematics;
using DarkVox.Shared.World;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using SpyroGame;
using DarkVox.Client;
using DarkVox.Server;
using DarkVox.Shared.Net;
using DarkVox.Shared.State;
using DarkVox.World;
using DarkVox.Shared.Diagnostics;

Directory.SetCurrentDirectory(AppContext.BaseDirectory);

static string ContentPath(string relativePath)
    => Path.Combine(AppContext.BaseDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar));

var nativeWindowSettings = new NativeWindowSettings()
{
    ClientSize = new Vector2i(1920, 1080),
    Title = "Spyro Game - GPU Terrain",

#if DEBUG
    Flags = ContextFlags.ForwardCompatible | ContextFlags.Debug,
#else
    Flags = ContextFlags.ForwardCompatible,
#endif

    Vsync = VSyncMode.Off,
    API = ContextAPI.OpenGL,
    APIVersion = new Version(4, 6),
    NumberOfSamples = 8,
    WindowState = WindowState.Maximized,
};

OpenRender.Log.MinimumLevel = OpenRender.Log.LevelInfo;

//  the one and only SceneManager
using var scm = new SceneManager(GameWindowSettings.Default, nativeWindowSettings);

//  create font atlases for different scenes
var fontAtlas1 = FontAtlasGenerator.Create(ContentPath("Resources/mcr.ttf"), 22, new(0,0,0,0));
var tr1 = new TextRenderer(TextRenderer.CreateTextRenderingProjection(scm.ClientSize.X, scm.ClientSize.Y), fontAtlas1);

var fontAtlas2 = FontAtlasGenerator.Create(ContentPath("Resources/consola.ttf"), 20, new(0.2f, 0f, 0.2f, 0.8f));
var tr2 = new TextRenderer(TextRenderer.CreateTextRenderingProjection(scm.ClientSize.X, scm.ClientSize.Y), fontAtlas2);

// Create VoxelWorld once
var world = new VoxelWorld(1338);

// Use debug player UUID for development (would come from auth in production)
var localPlayerId = DebugPlayerIdentity.GetDebugPlayerId();
OpenRender.Log.Info($"[Program] Using player UUID: {localPlayerId.Value}");

var (clientConn, serverConn) = InMemoryDuplexConnection.CreatePair<IClientToServerMessage, IServerToClientMessage>();
var serverStreamer = new DarkVox.Server.Streaming.ChunkStreamingManager(world);
var serverLog = new ConsoleLogger();
var server = new LocalGameServer(world, serverStreamer, spawnPosition: new Vector3(6450, 80, 7850), log: serverLog);

// Server is GL-free; it can tick safely on its own host thread.
var serverHost = new LocalGameServerHost(server, serverConn, localPlayerId);
var localClient = new LocalGameClient(clientConn, localPlayerId);
var session = new GameSession(world, localClient, serverHost, localPlayerId, server);

// Create GameScene (will receive terrain from loading scene)
var gameScene = new GameScene(tr2)
{
    World = world
};
scm.AddScene(gameScene);

// Start with TerrainLoadingScene which initializes the terrain and transitions to GameScene
var loadingScene = new TerrainLoadingScene(tr1, session);
scm.AddScene(loadingScene);
scm.ActivateScene(loadingScene);

try
{
    scm.Run();
}
finally
{
    // Save player data before shutdown
    try
    {
        server.SaveAllPlayers();
        OpenRender.Log.Info("[Program] Player data saved successfully");
    }
    catch (Exception ex)
    {
        OpenRender.Log.Error($"[Program] Failed to save player data: {ex.Message}");
    }

    // Ensure server shutdown flushes dirty chunk states/edits to disk.
    try { session.Stop(); } catch { }
    try { serverStreamer.Dispose(); } catch { }
}