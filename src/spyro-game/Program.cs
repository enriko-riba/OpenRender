using OpenRender.SceneManagement;
using OpenRender.Text;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using SpyroGame;
using SpyroGame.Client;
using SpyroGame.Server;
using SpyroGame.Shared.Net;
using SpyroGame.Shared.State;
using SpyroGame.World;

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

// Create local in-process session wiring (server lifecycle is owned by Program).
var localPlayerId = PlayerId.New();
var (clientConn, serverConn) = InMemoryDuplexConnection.CreatePair<IClientToServerMessage, IServerToClientMessage>();
var serverStreamer = new SpyroGame.Server.Streaming.ChunkStreamingManager(world);
var server = new LocalGameServer(world, serverStreamer, spawnPosition: new Vector3(6450, 80, 7850));

// Server is GL-free; it can tick safely on its own host thread.
var serverHost = new LocalGameServerHost(server, serverConn, localPlayerId);
var localClient = new LocalGameClient(clientConn, localPlayerId);
var session = new GameSession(world, localClient, serverHost, localPlayerId);

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
scm.Run();