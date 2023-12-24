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

//  create a font atlas and a text renderer
var fontAtlas = FontAtlasGenerator.Create("Resources/consola.ttf", 20, new(0f, 0f, 0f, 0f));
var tr = new TextRenderer(TextRenderer.CreateTextRenderingProjection(scm.ClientSize.X, scm.ClientSize.Y), fontAtlas);

// start app with menu scene
var scene = new MainScene(tr);
scm.AddScene(scene);
scm.ActivateScene(scene.Name);
scm.Run();