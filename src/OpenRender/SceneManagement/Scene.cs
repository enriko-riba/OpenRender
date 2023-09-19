using OpenRender.Core;
using OpenRender.Core.Culling;
using OpenRender.Core.Rendering;
using OpenRender.Core.Textures;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using System.Runtime.CompilerServices;

namespace OpenRender.SceneManagement;

public class Scene
{
    public const int MaxLights = 4;

    private readonly List<LightUniform> lights = new();
    private readonly List<Material> materialList = new();

    private readonly UniformBuffer<CameraUniform> vboCamera;
    private readonly UniformBuffer<MaterialUniform> vboMaterial;
    private readonly UniformBuffer<LightUniform> vboLight;
    private readonly TextureBatcher textureBatcher;

    private uint lastMaterial;
    private int lastProgramHandle;
    private bool hasNodeListChanged;
    private bool hasCameraChanged;

    private readonly List<Action> actionQueue = new();
    protected readonly List<SceneNode> nodes = new();
    protected readonly Dictionary<RenderGroup, List<SceneNode>> renderLayers = new();
    protected readonly Shader defaultShader;
    protected ICamera? camera;

    internal bool isLoaded;
    internal SceneManager scm = default!;

    internal IReadOnlyList<SceneNode> Nodes => nodes;

    public Scene() : this(null) { }

    public Scene(string? name)
    {
        defaultShader = new Shader("Shaders/standard.vert", "Shaders/standard.frag");
        vboCamera = new UniformBuffer<CameraUniform>("camera", 0);
        vboMaterial = new UniformBuffer<MaterialUniform>("material", 2);
        vboLight = new UniformBuffer<LightUniform>("light", 1);
        Name = name ?? GetType().Name;

        // 16 is minimum per OpenGL standard
        GL.GetInteger(GetPName.MaxTextureImageUnits, out var textureUnitsCount);
        textureBatcher = new TextureBatcher(textureUnitsCount);

        // Create the default render layers
        foreach (var renderGroup in Enum.GetValues<RenderGroup>())
        {
            renderLayers.Add(renderGroup, new List<SceneNode>());
        }
    }

    public SceneManager SceneManager => scm;

    public int VisibleNodes => nodes.Count(n => n.FrameBits.Value == 0);

    public ICamera? Camera => camera;

    public IReadOnlyCollection<LightUniform> Lights => lights;

    public Shader DefaultShader => defaultShader;

    public UniformBuffer<CameraUniform> VboCamera => vboCamera;

    public Color4 BackgroundColor { get; set; } = Color4.CornflowerBlue;

    public string Name { get; protected set; }

    public bool ShowBoundingSphere { get; set; }

    /// <summary>
    /// Enqueues an action to be executed as last step of frame update.
    /// Note: this is needed to correctly handle mutating scene state like node removals or additions, from code that gets executed inside <see cref="UpdateFrame"/>.
    /// </summary>
    /// <param name="action"></param>
    public void AddAction(Action action) => actionQueue.Add(action);

    public void AddLight(LightUniform light)
    {
        if (lights.Count >= MaxLights) throw new ArgumentOutOfRangeException(nameof(light), $"Max lights supported is {MaxLights}");
        lights.Add(light);
    }

    /// <summary>
    /// Adds the given node to the scene.
    /// </summary>
    /// <param name="node"></param>
    /// <exception cref="ArgumentException"></exception>
    public void AddNode(SceneNode node)
    {
        if (node.Scene != null) throw new ArgumentException("Node is already added to a scene!", nameof(node));
        hasNodeListChanged = true;
        nodes.Add(node);
        node.Scene = this; // Set the Scene reference for the added node
        node.OnResize(this, new(scm.ClientSize.X, scm.ClientSize.Y));   //  trigger resize event
        renderLayers[node.RenderGroup].Add(node);
    }

    /// <summary>
    /// Removes the given node from the scene.
    /// </summary>
    /// <param name="node"></param>
    public void RemoveNode(SceneNode node)
    {
        var isRemoved = nodes.Remove(node);
        hasNodeListChanged = hasNodeListChanged || isRemoved;
        node.Scene = null; // Remove the Scene reference from the removed node
        renderLayers[node.RenderGroup].Remove(node);
    }

    /// <summary>
    /// Removes all nodes from the scene.
    /// </summary>
    public void RemoveAllNodes()
    {
        nodes.ForEach(n =>
        {
            n.Scene = null;
            renderLayers[n.RenderGroup].Remove(n);
        });
        nodes.Clear();
        hasNodeListChanged = true;
    }

    /// <summary>
    /// Resets the tracked last material used for rendering, this will force the material to be updated on the next frame.
    /// Note: this is needed if material related properties like textures or lights are updated directly via OpenGL functions instead of the material class.
    /// </summary>
    public void ResetMaterial() => lastMaterial = 0;

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

        var fnDebugProc = Utility.DebugMessageDelegate;
        GL.DebugMessageCallback(fnDebugProc, IntPtr.Zero);
        GL.Enable(EnableCap.DebugOutput);
        GL.Enable(EnableCap.DebugOutputSynchronous);

        vboCamera.BindToShaderProgram(defaultShader);
        vboLight.BindToShaderProgram(defaultShader);
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

        GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

        //  set shared uniform blocks for all programs before render loop
        var cam = new CameraUniform()
        {
            view = camera.ViewMatrix,
            projection = camera.ProjectionMatrix,
            position = camera.Position,
            direction = camera.Front
        };
        vboCamera.UpdateSettings(ref cam);

        if (lights.Any())
        {
            //  TODO: pass lights array
            var dirLight = lights[0];
            vboLight.UpdateSettings(ref dirLight);
        }

        //  render each layer separately
        var renderList = renderLayers[RenderGroup.SkyBox];
        RenderNodeList(renderList, elapsedSeconds);

        renderList = renderLayers[RenderGroup.Default];
        RenderNodeList(renderList, elapsedSeconds);

        renderList = renderLayers[RenderGroup.DistanceSorted];
        RenderNodeList(renderList, elapsedSeconds);

        renderList = renderLayers[RenderGroup.UI];
        RenderNodeList(renderList, elapsedSeconds);
    }

    /// <summary>
    /// Renders visible nodes in the list.
    /// </summary>
    /// <param name="list"></param>
    /// <param name="elapsedSeconds"></param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void RenderNodeList(IEnumerable<SceneNode> list, double elapsedSeconds)
    {
        foreach (var node in list)
        {
            if (node.FrameBits.Value == 0)
            {
                RenderNode(node, elapsedSeconds);
            }
        }
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

        foreach (var action in actionQueue)
        {
            action.Invoke();
        }
        actionQueue.Clear();

        CullFrustum();
        SortRenderList();
        OptimizeTextureUnitUsage();
        hasNodeListChanged = false;
    }

    public virtual void OnMouseWheel(MouseWheelEventArgs e) { }

    public virtual void OnMouseMove(MouseMoveEventArgs e) { }

    public virtual void OnResize(ResizeEventArgs e)
    {
        GL.Viewport(0, 0, SceneManager.ClientSize.X, SceneManager.ClientSize.Y);
        if (camera is not null) camera.AspectRatio = SceneManager.ClientSize.X / (float)SceneManager.ClientSize.Y;
        foreach (var node in nodes)
        {
            node.OnResize(this, e);
        }
    }

    private void RenderNode(SceneNode node, double elapsed)
    {
        var material = node.Material;
        var shader = material.Shader ?? defaultShader;
        shader.Use();
        if (lastProgramHandle != shader.Handle)
        {
            lastProgramHandle = shader.Handle;
            if (vboCamera.IsUniformSupported(shader)) vboCamera.BindToShaderProgram(shader);
            if (vboLight.IsUniformSupported(shader)) vboLight.BindToShaderProgram(shader);
            if (vboMaterial.IsUniformSupported(shader)) vboMaterial.BindToShaderProgram(shader);
        }

        if (shader.UniformExists("model"))
        {
            node.GetWorldMatrix(out var worldMatrix);
            shader.SetMatrix4("model", ref worldMatrix);
        }

        if (lastMaterial != material.Id)
        {
            lastMaterial = material.Id;
            var settings = new MaterialUniform()
            {
                Diffuse = material.DiffuseColor,
                Emissive = material.EmissiveColor,
                Specular = material.SpecularColor,
                Shininess = material.Shininess,
            };
            vboMaterial.UpdateSettings(ref settings);
            if (shader.UniformExists("uHasDiffuseTexture")) shader.SetInt("uHasDiffuseTexture", material.HasDiffuse ? 1 : 0);
            if (shader.UniformExists("uDetailTextureFactor")) shader.SetFloat("uDetailTextureFactor", material.DetailTextureFactor);

            _ = textureBatcher.GetOptimalTextureUnits(material);
            for (var i = 0; i < material.Textures?.Length; i++)
            {
                var texture = material.Textures[i];
                if (texture != null)
                {
                    var unit = textureBatcher.GetTextureUnitWithTexture(texture.Handle);
                    texture.Use(TextureUnit.Texture0 + unit);
                    shader.SetInt(texture.UniformName, unit);
                }
            }
        }

        node.OnDraw(this, elapsed);
        GL.UseProgram(0);
    }

    public int GetBatchedTextureUnit(Texture texture)
    {
        var unit = textureBatcher.GetTextureUnitWithTexture(texture.Handle);
        return unit < 0 ? 0 : unit;
    }

    public Frustum Frustum = new();

    private void CullFrustum()
    {
        hasCameraChanged = (camera?.Update() ?? false);
        if (hasCameraChanged && camera is not null)
        {
            Frustum.Update(camera);
            CullingHelper.CullNodes(Frustum, nodes);
        }
    }

    private void OptimizeTextureUnitUsage()
    {
        if (hasNodeListChanged)
        {
            UpdateSceneMaterials();
            textureBatcher.Reset();
            textureBatcher.SortMaterials(materialList);
        }
    }

    private void UpdateSceneMaterials()
    {
        materialList.Clear();
        materialList.AddRange(nodes
            .Select(n => n.Material)
            .DistinctBy(m => m.Id));
    }

    private void SortRenderList()
    {
        if (hasCameraChanged || hasNodeListChanged) nodes.Sort(GroupComparer);

        if (nodes.Any(n => n.RenderGroup == RenderGroup.DistanceSorted && n.FrameBits.Value == 0))
        {
            //  distance based sorting for RenderGroup.DistanceSorted
            var firstDistanceSorted = nodes.FindIndex(n => n.RenderGroup == RenderGroup.DistanceSorted);
            var lastDistanceSorted = nodes.FindLastIndex(n => n.RenderGroup == RenderGroup.DistanceSorted);
            nodes.Sort(firstDistanceSorted, lastDistanceSorted - firstDistanceSorted + 1, new DistanceComparer(camera!.Position));
        }
    }

    private int GroupComparer(SceneNode a, SceneNode b)
    {
        var renderGroupComparison = a.RenderGroup.CompareTo(b.RenderGroup);
        if (renderGroupComparison == 0 && a.RenderGroup == RenderGroup.UI)
        {
            // If UI RenderGroup values are the same, compare by index.
            return nodes.IndexOf(a).CompareTo(nodes.IndexOf(b));
        }
        return renderGroupComparison;
    }
}
