using OpenRender.SceneManagement;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;

using Samples.Triangle;

var nativeWindowSettings = new NativeWindowSettings()
{
    Size = new Vector2i(1280, 1024),
    Title = "OpenRender Triangle",
    Flags = ContextFlags.ForwardCompatible | ContextFlags.Debug,    // ForwardCompatible is needed to run on Mac OS
    Vsync = VSyncMode.Off,
    API = ContextAPI.OpenGL,
    APIVersion = new Version(4, 6),
    NumberOfSamples = 32,
};

using var scm = new SceneManager(GameWindowSettings.Default, nativeWindowSettings);
var mainScene = new MainScene();
scm.AddScene(mainScene);
scm.ActivateScene(mainScene.Name);
scm.Run();