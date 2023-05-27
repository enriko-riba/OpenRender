using OpenTK.Graphics.OpenGL4;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using System.Diagnostics;
namespace OpenRender.SceneManagement;

public class SceneManager : GameWindow
{
    private readonly Stopwatch sw = new();
    private readonly List<Scene> sceneList = new();
    private Scene? activeScene;

    private long lastFpsTime = 0;
    private int frames;

    public SceneManager(GameWindowSettings gameWindowSettings, NativeWindowSettings nativeWindowSettings) :
        base(gameWindowSettings, nativeWindowSettings)
    {
        UpdateFrame += (e) => activeScene?.UpdateFrame(e.Time);
        Resize += (e) => activeScene?.OnResize(e);
        MouseMove += (e) => activeScene?.OnMouseMove(e);
        MouseWheel += (e) => activeScene?.OnMouseWheel(e);
        Load += () =>
        {
            GL.Enable(EnableCap.Multisample);
            GL.Hint(HintTarget.LineSmoothHint, HintMode.Nicest);
            //GL.Hint(HintTarget.PolygonSmoothHint, HintMode.Nicest);
        };
    }


    public void AddScene(Scene scene)
    {
        var existing = sceneList.FirstOrDefault(s => s.Name == scene.Name);
        if (existing != null) throw new ArgumentException($"Scene {existing.Name} is already added to the scene manager!", nameof(scene));
        sceneList.Add(scene);
    }

    public void ActivateScene(Scene scene)
    {
        ActivateScene(scene.Name);
    }

    public void ActivateScene(string sceneName)
    {
        var scene = sceneList.First(s => s.Name == sceneName);
        activeScene = scene;
        if (!scene.isLoaded)
        {
            scene.scm = this;
            scene.Load();
            scene.isLoaded = true;
        }
        sw.Restart();
    }

    protected override void OnRenderFrame(FrameEventArgs e)
    {
        activeScene?.RenderFrame(e.Time);
        //base.OnRenderFrame(e);
        SwapBuffers();

        frames++;
        var elapsed = sw.ElapsedMilliseconds - lastFpsTime;
        if (elapsed >= 1000)
        {
            var d = (double)elapsed / frames;
            var fps = 1000 / d;
            Title = $"OpenRender, avg frame duration: {d:G3} ms, fps: {fps:G4}";
            frames = 0;
            lastFpsTime = sw.ElapsedMilliseconds;
        }
    }
}
