using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
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

    public Scene? ActiveScene => activeScene;

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

    private void Render(FrameEventArgs e)
    {
        activeScene?.RenderFrame(e.Time);

        if(!string.IsNullOrEmpty(fpsText))
            activeScene?.tr.RenderText(fpsText, 5, ClientSize.Y - 20, textColor, ClientSize.X, ClientSize.Y);

        nodesText = $"nodes: {activeScene?.RenderList?.Count ?? 0}/{activeScene?.Nodes.Count}";
        activeScene?.tr.RenderText(nodesText, 5, ClientSize.Y - 40, textColor, ClientSize.X, ClientSize.Y);

        SwapBuffers();

        frames++;
        var elapsed = sw.ElapsedMilliseconds - lastFpsTime;
        if (elapsed >= 1000)
        {
            var d = (double)elapsed / frames;
            var fps = 1000 / d;
            fpsText = $"avg frame duration: {d:G3} ms, fps: {fps:N0}";
            Title = fpsText;
            frames = 0;
            lastFpsTime = sw.ElapsedMilliseconds;
        }
    }

    private string fpsText = string.Empty;
    private string nodesText = string.Empty;
    private Vector3 textColor = new Vector3(0.21f, 0.21f, 0.95f);
}
