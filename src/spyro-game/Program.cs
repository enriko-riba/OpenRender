using OpenRender.SceneManagement;
using OpenRender.Text;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using SpyroGame;
using SpyroGame.World;

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
var fontAtlas1 = FontAtlasGenerator.Create("Resources/mcr.ttf", 22, new(0,0,0,0));
var tr1 = new TextRenderer(TextRenderer.CreateTextRenderingProjection(scm.ClientSize.X, scm.ClientSize.Y), fontAtlas1);

var fontAtlas2 = FontAtlasGenerator.Create("Resources/consola.ttf", 20, new(0.2f, 0f, 0.2f, 0.8f));
var tr2 = new TextRenderer(TextRenderer.CreateTextRenderingProjection(scm.ClientSize.X, scm.ClientSize.Y), fontAtlas2);

// Create VoxelWorld once
var world = new VoxelWorld(1338);

// Create GameScene (will receive terrain from loading scene)
var gameScene = new GameScene(tr2)
{
    World = world
};
scm.AddScene(gameScene);

// Start with TerrainLoadingScene which initializes the terrain and transitions to GameScene
var loadingScene = new TerrainLoadingScene(tr1, world);
scm.AddScene(loadingScene);
scm.ActivateScene(loadingScene);
scm.Run();