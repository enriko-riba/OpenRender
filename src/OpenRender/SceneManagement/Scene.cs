using OpenRender.Core.Culling;
using OpenRender.Core.Rendering;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;

namespace OpenRender.SceneManagement;

public class Scene
{
    public const int MaxLights = 4;

    private bool isLoaded;
    private readonly List<LightUniform> lights = [];
    private readonly List<Action> actionQueue = [];
    protected readonly List<SceneNode> nodes = [];
    protected readonly Shader defaultShader;

    protected readonly Renderer renderer = new();
    protected ICamera? camera;

    internal SceneManager scm = default!;

    internal IReadOnlyList<SceneNode> Nodes => nodes;

    public Scene() : this(null) { }

    public Scene(string? name)
    {
        defaultShader = new Shader("Shaders/standard.vert", "Shaders/standard.frag");
        Name = name ?? GetType().Name;
    }

    public bool IsLoaded => isLoaded;

    public SceneManager SceneManager => scm;

    public int VisibleNodes => nodes.Count(n => (n.FrameBits.Value & (uint)FrameBitsFlags.RenderMask) == 0);

    public ICamera? Camera => camera;

    public IReadOnlyCollection<LightUniform> Lights => lights;

    public Shader DefaultShader => defaultShader;

    public Color4 BackgroundColor { get; set; } = Color4.CornflowerBlue;

    public string Name { get; protected set; }

    public bool ShowBoundingSphere { get; set; }

    /// <summary>
    /// Returns the width of the scene viewport.
    /// </summary>
    public int Width => SceneManager.ClientSize.X;

    /// <summary>
    /// Returns the height of the scene viewport.
    /// </summary>
    public int Height => SceneManager.ClientSize.Y;

    /// <summary>
    /// Enqueues an action to be executed as last step of frame update.
    /// Note: this is needed to correctly handle mutating scene state like node removals or additions, from code that gets executed inside <see cref="UpdateFrame"/>.
    /// </summary>
    /// <param name="action"></param>
    public void AddAction(Action action) 
    {
        lock (actionQueue) 
        {
            actionQueue.Add(action);
        }
    }

    public void AddLight(in LightUniform light)
    {
        if (lights.Count >= MaxLights) throw new ArgumentOutOfRangeException(nameof(light), $"Max lights supported is {MaxLights}");
        lights.Add(light);
    }

    public void UpdateLight(int index, in LightUniform light)
    {
        if (index >= MaxLights) throw new ArgumentOutOfRangeException(nameof(index), $"Max lights supported is {MaxLights}");
        lights[index] = light;
    }   

    /// <summary>
    /// Adds the given node to the scene.
    /// </summary>
    /// <param name="node"></param>
    /// <exception cref="ArgumentException"></exception>
    public void AddNode(SceneNode node)
    {
        if (node.Scene != null) throw new ArgumentException("Node is already added to a scene!", nameof(node));
        nodes.Add(node);
        node.Scene = this; // Set the Scene reference for the added node
        node.OnResize(this, new(Width, Height));   //  trigger resize event
        renderer.AddNode(node);
    }

    /// <summary>
    /// Removes the given node from the scene.
    /// </summary>
    /// <param name="node"></param>
    public void RemoveNode(SceneNode node)
    {
        nodes.Remove(node);
        node.Scene = null; // Remove the Scene reference from the removed node
        renderer.RemoveNode(node);
    }

    /// <summary>
    /// Removes all nodes from the scene.
    /// </summary>
    public void RemoveAllNodes()
    {
        nodes.ForEach(n => n.Scene = null);
        nodes.Clear();
        renderer.RemoveAllNodes();
    }
        
    /// <summary>
    /// Returns the renderer used by the scene.
    /// </summary>
    public Renderer Renderer => renderer;

    /// <summary>
    /// Sets up OpenGL state and loads the default shader.    
    /// </summary>
    public virtual void Load()
    {
        GL.FrontFace(FrontFaceDirection.Ccw);
        GL.Enable(EnableCap.DepthTest);
        GL.Enable(EnableCap.CullFace);
        GL.Enable(EnableCap.LineSmooth);
        GL.Enable(EnableCap.PolygonSmooth);
        GL.Enable(EnableCap.Blend);
        GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        GL.ClearColor(BackgroundColor);
        isLoaded = true;

#if DEBUG
        if (Utility.IsExtensionSupported("KHR_debug") || Utility.IsExtensionSupported("GL_KHR_debug"))
        {
            var fnDebugProc = Utility.DebugMessageDelegate;
            GL.DebugMessageCallback(fnDebugProc, IntPtr.Zero);
            GL.Enable(EnableCap.DebugOutput);
            GL.Enable(EnableCap.DebugOutputSynchronous);
        }
#endif        
    }

    /// <summary>
    /// Fired when the scene gets loaded.
    /// Note: if overridden, the base <see cref="Load()"/> method must be called.
    /// </summary>
    public virtual void OnLoaded()
    {
        renderer.PrepareBatching();
    }

    /// <summary>
    /// Fired when the scene gets activated.
    /// </summary>
    public virtual void OnActivate() => GL.ClearColor(BackgroundColor);

    /// <summary>
    /// Clears the scene, assigns camera and light uniform buffer objects and renders each node.
    /// </summary>
    /// <param name="elapsedSeconds"></param>
    public virtual void RenderFrame(double elapsedSeconds)
    {
        ArgumentNullException.ThrowIfNull(camera);
        renderer.BeforeRenderFrame(camera!, lights);
        renderer.RenderFrame(elapsedSeconds);
    }


    /// <summary>
    /// Invokes <see cref="SceneNode.OnUpdate(Scene, double)"/> for each node, applies frustum culling to build 
    /// a node render list and if node graph has changed, invokes texture batcher.
    /// </summary>
    /// <param name="elapsedSeconds"></param>
    public virtual void UpdateFrame(double elapsedSeconds)
    {
        foreach (var node in nodes)
        {
            node.OnUpdate(this, elapsedSeconds);
        }

        lock (actionQueue)
        {
            foreach (var action in actionQueue)
            {
                action.Invoke();
            }
            actionQueue.Clear();
        }
        renderer.Update(camera, nodes);
    }

    public virtual void OnMouseWheel(MouseWheelEventArgs e) { }

    public virtual void OnMouseMove(MouseMoveEventArgs e) { }

    public virtual void OnResize(ResizeEventArgs e)
    {
        GL.Viewport(0, 0, Width, Height);
        if (camera is not null) camera.AspectRatio = Width / (float)Height;
        foreach (var node in nodes)
        {
            node.OnResize(this, e);
        }
    }

    /// <summary>
    /// Override to handle close event.
    /// The base Scene implementation does nothing.
    /// </summary>
    public virtual void Close() { }
}
