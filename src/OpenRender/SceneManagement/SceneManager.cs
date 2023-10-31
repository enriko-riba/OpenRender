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
        RenderFrame += Render;
        //UpdateFrame += (e) => activeScene?.UpdateFrame(e.Time);
        Resize += (e) => activeScene?.OnResize(e);
        MouseMove += (e) => activeScene?.OnMouseMove(e);
        MouseWheel += (e) => activeScene?.OnMouseWheel(e);
        Load += () =>
        {
            GL.Enable(EnableCap.Multisample);
            GL.Hint(HintTarget.LineSmoothHint, HintMode.Nicest);
        };
    }

    public Scene? ActiveScene => activeScene;

    public float Fps { get; private set; }
    public float AvgFrameDuration { get; private set; }

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
        if (!scene.IsLoaded)
        {
            scene.scm = this;
            scene.Load();
            scene.OnLoaded();
        }
        scene.OnResize(new ResizeEventArgs(ClientSize.X, ClientSize.Y));
        scene.OnActivate();
        sw.Restart();
    }

    private void Render(FrameEventArgs e)
    {
        activeScene?.UpdateFrame(e.Time);
        activeScene?.RenderFrame(e.Time);
        SwapBuffers();

        frames++;
        var elapsed = sw.ElapsedMilliseconds - lastFpsTime;
        if (elapsed >= 1000)
        {
            AvgFrameDuration = (float)elapsed / frames;
            Fps = 1000f / AvgFrameDuration;
            frames = 0;
            lastFpsTime = sw.ElapsedMilliseconds;
        }
    }
}
