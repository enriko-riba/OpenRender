using OpenRender.Core.Rendering;
using OpenRender.SceneManagement;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace Samples.Triangle;

internal class MainScene : Scene
{
    private Buffer<float> vb = default!;
    private VertexArrayObject vao = default!;

    public override void Load()
    {
        //  Note: rendering a triangle manually as this method shows is complicated.
        //  Using built in components like SceneNode, Mesh, Material is the preferred and more intuitive way,
        //  it would replace most of this code, but it is a good example of how to use the low level API. 


        //  base class sets up important state
        base.Load();

        //  a camera is mandatory for rendering, the camera is facing the negative z axis
        camera = new Camera3D(Vector3.Zero, Width / (float)Height);

        //  create three vertices (x,y,z positions) forming a triangle
        var vertices = new float[]
        {
           -0.5f, -0.5f, -5.0f,     // left bottom
            0.5f, -0.5f, -5.0f,     // right bottom
            0.0f,  0.5f, -5.0f ,    // top middle
        };
        var colors = new float[]
        {
            1.0f, 0.0f, 0.0f,   // left bottom - red
            0.0f, 1.0f, 0.0f,   // right bottom - green
            0.0f, 0.0f, 1.0f,   // top middle - blue
        };
        var normals = new float[]
        {
            0f, 0f, 1.0f,   // left bottom
            0f, 0f, 1.0f,   // right bottom 
            0f, 0f, 1.0f,   // top middle
        };

        //  create a vertex buffer from the vertices an tell OpenGL to use it       
        vao = new VertexArrayObject();
        vao.AddBuffer(VertexDeclarations.VertexPosition, vertices, name: "VBO Triangle");
        vao.AddBuffer(new VertexDeclaration(new VertexAttribLayout(VertexAttribLocation.Color, 3, VertexAttribType.Float)), colors, name: "VBO Color");
        vao.AddBuffer(new VertexDeclaration(new VertexAttribLayout(VertexAttribLocation.Normal, 3, VertexAttribType.Float)), normals, name: "VBO Normal");
        GL.BindVertexArray(vao);

        var modelMatrix = Matrix4.Identity;
        var shader = defaultShader;
        shader.Use();
        shader.SetMatrix4("model", ref modelMatrix);

        //  since the default shader expects material and light uniform,
        //  we need to create those and bind them to the shader.
        //  Light is bound to shader by the scene, so we just need to add it
        //  but material needs to be both created and bound to the shader.
        var lightUniform = new LightUniform()
        {
            Direction = new Vector3(0, 0, -1),
            Ambient = new Vector3(0.010f),
            Diffuse = new Vector3(1),
        };
        AddLight(lightUniform);

        var uboMaterial = new UniformBuffer<MaterialUniform>("material", 2);
        uboMaterial.BindToShaderProgram(shader);
        var materialUniform = new MaterialUniform() { Diffuse = Vector3.Zero };
        uboMaterial.UpdateSettings(ref materialUniform);
    }

    public override void RenderFrame(double elapsedSeconds)
    {
        base.RenderFrame(elapsedSeconds);
        GL.DrawArrays(PrimitiveType.Triangles, 0, vao.DataLength);
    }
}
