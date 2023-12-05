using OpenRender.SceneManagement;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using Samples.Batching;

var nativeWindowSettings = new NativeWindowSettings()
{
    ClientSize = new Vector2i(1280, 1024),
    Title = "OpenRender Batching",
    Flags = ContextFlags.ForwardCompatible | ContextFlags.Debug,    // ForwardCompatible is needed to run on Mac OS
    Vsync = VSyncMode.Off,
    API = ContextAPI.OpenGL,
    APIVersion = new Version(4, 6),
    NumberOfSamples = 64,
};

using var scm = new SceneManager(GameWindowSettings.Default, nativeWindowSettings);
var scene = new BatchingScene();
scm.AddScene(scene);
scm.ActivateScene(scene.Name);
scm.Run();
