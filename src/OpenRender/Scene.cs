using OpenRender.Core;
using OpenRender.Core.Rendering;
using OpenRender.Core.Textures;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace OpenRender.SceneManagement;

public class Scene 
{
    public const int MaxLights = 4;

    private readonly List<SceneNode> nodes = new();
    private readonly List<LightUniform> lights = new();
    private readonly UniformBuffer<CameraUniform> vboCamera;
    private readonly UniformBuffer<MaterialUniform> vboMaterial;
    private readonly UniformBuffer<LightUniform> vboLight;
    private readonly TextureBatcher textureBatcher;

    private Material? lastMaterial;
    private int lastProgramHandle;

    protected readonly Shader defaultShader;
    protected ICamera? camera;
    protected KeyboardState KeyboardState = default!;
    protected MouseState MouseState = default!;
    internal bool isLoaded;
    internal SceneManager scm = default!;    

    public Scene(string name) 
    {
        defaultShader = new Shader("Shaders/standard.vert", "Shaders/standard.frag");
        vboCamera = new UniformBuffer<CameraUniform>("camera", 0);
        vboMaterial = new UniformBuffer<MaterialUniform>("material", 2);
        vboLight = new UniformBuffer<LightUniform>("light", 1);
        Name = name;
        textureBatcher = new TextureBatcher(16); //  TODO: get real number of texture units
    }

    protected SceneManager SceneManager => scm;

    public ICamera? Camera => camera;

    public IReadOnlyCollection<LightUniform> Lights => lights;

    public string Name { get; protected set; }

    public void AddLight(LightUniform light)
    {
        if (lights.Count >= MaxLights) throw new ArgumentOutOfRangeException(nameof(light), $"Max lights supported is {MaxLights}");
        lights.Add(light);
    }

    public void AddNodeAt(SceneNode node, int index) => nodes.Insert(index, node);

    public void AddNode(SceneNode node) => nodes.Add(node);

    public void RemoveNode(SceneNode node) => nodes.Remove(node);

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
            var dirLight = lights[0];
            vboLight.UpdateSettings(ref dirLight);
        }

        //  TODO: maintain dirty flag when adding/removing nodes so that materials can be fed to texture batcher and sorted

        foreach (var node in nodes)
        {
            RenderNode(node, elapsedSeconds);
        }       
    }

    public virtual void UpdateFrame(double elapsedSeconds)
    {
        KeyboardState = SceneManager.KeyboardState;
        MouseState = SceneManager.MouseState;

        foreach (var node in nodes)
        {
            node.OnUpdate(this, elapsedSeconds);
        }
        camera?.Update();
    }

    public virtual void OnMouseWheel(MouseWheelEventArgs e) { }

    public virtual void OnMouseMove(MouseMoveEventArgs e) { }

    public virtual void OnResize(ResizeEventArgs e)
    {
        GL.Viewport(0, 0, SceneManager.Size.X, SceneManager.Size.Y);
        camera!.AspectRatio = SceneManager.Size.X / (float)SceneManager.Size.Y;
    }
        
    private void RenderNode(SceneNode node, double elapsed)
    {
        var material = node.Mesh?.Material;
        var shader = material?.Shader ?? defaultShader;

        if (lastProgramHandle != shader.Handle)
        {
            lastProgramHandle = shader.Handle;
            vboCamera.BindToShaderProgram(shader);
            vboLight.BindToShaderProgram(shader);
            vboMaterial.BindToShaderProgram(shader);
        }

        if (shader.UniformExists("model")) shader.SetMatrix4("model", node.World);

        if (lastMaterial != material && material != null)
        {
            lastMaterial = material;
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

            for (var i = 0; i < material.Textures?.Length; i++)
            {
                var texture = material.Textures[i];
                if (texture != null)
                {
                    texture.Use();  //  TODO: use texture unit assignment from batcher instead 
                    var id = (int)texture.TextureUnit - (int)TextureUnit.Texture0;
                    shader.SetInt(texture.UniformName, id);
                }
            }
        }

        node.OnDraw(this, elapsed);
        foreach (var child in node.Children) RenderNode(child, elapsed);
    }
}
