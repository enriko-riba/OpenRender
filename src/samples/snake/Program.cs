using OpenRender.SceneManagement;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using Samples.Snake;

var nativeWindowSettings = new NativeWindowSettings()
{
    Size = new Vector2i(1280, 1024),
    Title = "OpenRender Playground",
    Flags = ContextFlags.ForwardCompatible | ContextFlags.Debug,    // ForwardCompatible is needed to run on Mac OS
    Vsync = VSyncMode.Off,
    API = ContextAPI.OpenGL,
    APIVersion = new Version(4, 6),
    NumberOfSamples = 32,
};

using var scm = new SceneManager(GameWindowSettings.Default, nativeWindowSettings);
var menuScene = new MenuScene();
scm.AddScene(menuScene);
scm.ActivateScene(menuScene.Name);
scm.Run();
