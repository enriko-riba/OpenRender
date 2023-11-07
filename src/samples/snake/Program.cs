using OpenRender.SceneManagement;
using OpenRender.Text;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using Samples.Snake;

var nativeWindowSettings = new NativeWindowSettings()
{
    Size = new Vector2i(1600, 1280),
    Title = "OpenRender Snake",
    Flags = ContextFlags.ForwardCompatible | ContextFlags.Debug,    // ForwardCompatible is needed to run on Mac OS
    Vsync = VSyncMode.Off,
    API = ContextAPI.OpenGL,
    APIVersion = new Version(4, 6),
    NumberOfSamples = 32,
    WindowState = WindowState.Fullscreen,
};

//  the one and only SceneManager
using var scm = new SceneManager(GameWindowSettings.Default, nativeWindowSettings); 

//  create a font atlas and a text renderer
var fontAtlas = FontAtlasGenerator.Create("Resources/consola.ttf", 20, new (0f, 0f, 0f, 0f));
var tr = new TextRenderer(TextRenderer.CreateTextRenderingProjection(scm.ClientSize.X, scm.ClientSize.Y), fontAtlas);

// start app with menu scene
var scene = new SnakeScene(tr);
scm.AddScene(scene);
scm.ActivateScene(scene.Name);
scm.Run();
