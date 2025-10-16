using OpenRender.SceneManagement;
using OpenRender.Text;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using SpyroGame;

var nativeWindowSettings = new NativeWindowSettings()
{
    ClientSize = new Vector2i(1920, 1080),
    Title = "Game",

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

//  the one and only SceneManager
using var scm = new SceneManager(GameWindowSettings.Default, nativeWindowSettings);

//  create a font atlas and a text renderer for loading and main scenes
var fontAtlas1 = FontAtlasGenerator.Create("Resources/mcr.ttf", 22, new(0,0,0,0));
var tr1 = new TextRenderer(TextRenderer.CreateTextRenderingProjection(scm.ClientSize.X, scm.ClientSize.Y), fontAtlas1);

var fontAtlas2 = FontAtlasGenerator.Create("Resources/consola.ttf", 20, new(0.2f, 0f, 0.2f, 0.8f));
var tr2 = new TextRenderer(TextRenderer.CreateTextRenderingProjection(scm.ClientSize.X, scm.ClientSize.Y), fontAtlas2);

// start app with loading scene
var scene = new LoadingScene(tr1);
scm.AddScene(scene);
scm.AddScene(new MainScene(tr2));
scm.ActivateScene(scene);
scm.Run();