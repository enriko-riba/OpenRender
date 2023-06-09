﻿using OpenRender.Core;
using OpenRender.Core.Rendering;
using OpenRender.Core.Textures;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;

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
    private readonly CullingHelper cullingHelper = new();

    private uint lastMaterial;
    private int lastProgramHandle;
    private bool hasNodeListChanged;
    private bool hasCameraChanged;

    protected readonly List<SceneNode> nodes = new();
    protected readonly List<SceneNode> renderList = new();
    protected readonly Shader defaultShader;
    protected ICamera? camera;

    internal bool isLoaded;
    internal SceneManager scm = default!;
    internal IReadOnlyList<SceneNode> RenderList => renderList;
    internal IReadOnlyList<SceneNode> Nodes => nodes;

    public Scene(string name)
    {
        defaultShader = new Shader("Shaders/standard.vert", "Shaders/standard.frag");
        vboCamera = new UniformBuffer<CameraUniform>("camera", 0);
        vboMaterial = new UniformBuffer<MaterialUniform>("material", 2);
        vboLight = new UniformBuffer<LightUniform>("light", 1);
        Name = name;

        // 16 is minimum per OpenGL standard
        GL.GetInteger(GetPName.MaxTextureImageUnits, out var textureUnitsCount);
        textureBatcher = new TextureBatcher(textureUnitsCount); 
    }

    protected SceneManager SceneManager => scm;

    public ICamera? Camera => camera;

    public IReadOnlyCollection<LightUniform> Lights => lights;

    public Shader DefaultShader => defaultShader;

    public string Name { get; protected set; }

    public bool ShowBoundingSphere { get; set; }

    public void AddLight(LightUniform light)
    {
        if (lights.Count >= MaxLights) throw new ArgumentOutOfRangeException(nameof(light), $"Max lights supported is {MaxLights}");
        lights.Add(light);
    }

    public void AddNode(SceneNode node)
    {
        hasNodeListChanged = true;
        nodes.Add(node);
        node.Scene = this; // Set the Scene reference for the added node       
    }

    public void RemoveNode(SceneNode node)
    {
        hasNodeListChanged = hasNodeListChanged || nodes.Remove(node);       
        node.Scene = null; // Remove the Scene reference from the removed node
    }

    public virtual void Load()
    {
        GL.FrontFace(FrontFaceDirection.Ccw);
        GL.Enable(EnableCap.DepthTest);
        GL.Enable(EnableCap.CullFace);
        GL.Enable(EnableCap.LineSmooth);
        GL.Enable(EnableCap.PolygonSmooth);
        GL.ClearColor(Color4.CornflowerBlue);

        var fnDebugProc = Utility.DebugMessageDelegate;
        GL.DebugMessageCallback(fnDebugProc, IntPtr.Zero);
        GL.Enable(EnableCap.DebugOutput);
        GL.Enable(EnableCap.DebugOutputSynchronous);

        vboCamera.BindToShaderProgram(defaultShader);
        vboLight.BindToShaderProgram(defaultShader);
    }

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

        foreach (var node in renderList)
        {
            RenderNode(node, elapsedSeconds);
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

        CullFrustum();
        OptimizeTextureUnitUsage();
    }

    public virtual void OnMouseWheel(MouseWheelEventArgs e) { }

    public virtual void OnMouseMove(MouseMoveEventArgs e) { }

    public virtual void OnResize(ResizeEventArgs e)
    {
        GL.Viewport(0, 0, SceneManager.ClientSize.X, SceneManager.ClientSize.Y);
        camera!.AspectRatio = SceneManager.ClientSize.X / (float)SceneManager.ClientSize.Y;
    }
    
    private void RenderNode(SceneNode node, double elapsed)
    {
        var material = node.Material;
        var shader = material.Shader ?? defaultShader;
        shader.Use();
        if (lastProgramHandle != shader.Handle)
        {
            lastProgramHandle = shader.Handle;
            vboCamera.BindToShaderProgram(shader);
            vboLight.BindToShaderProgram(shader);
            vboMaterial.BindToShaderProgram(shader);
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

        shader.Use();
        node.OnDraw(this, elapsed);
    }

    private void CullFrustum()
    {
        hasCameraChanged = (camera?.Update() ?? false);
        if (hasCameraChanged && camera is not null)
        {
            cullingHelper.ExtractFrustumPlanes(camera.ViewProjectionMatrix);
        }
        cullingHelper.CullNodes(nodes, renderList);
    }

    private void OptimizeTextureUnitUsage()
    {
        if (hasNodeListChanged)
        {
            UpdateSceneMaterials();
            textureBatcher.Reset();
            textureBatcher.SortMaterials(materialList);
            hasNodeListChanged = false;
        }
    }
    
    private void UpdateSceneMaterials()
    {
        materialList.Clear();
        materialList.AddRange(nodes
            .Select(n => n.Material)
            .DistinctBy(m => m.Id));
    }
}
