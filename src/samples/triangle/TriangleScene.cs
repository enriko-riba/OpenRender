using OpenRender.Core;
using OpenRender.Core.Buffers;
using OpenRender.Core.Rendering;
using OpenRender.Core.Rendering.Batching;
using OpenRender.SceneManagement;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace Samples.Triangle;

internal class TriangleScene : Scene
{
    private VertexArrayObject vao = default!;

    //  note: the binding points for the block uniforms are not arbitrary numbers, they need to match the shader implementation.
    //  The default shader implementation uses binding points 0, 1, 2, 3 for camera, light, material and textures respectively.
    private readonly UniformBlockBuffer<CameraUniform> uboCamera = new("camera", 0);
    private readonly UniformBlockBuffer<LightUniform> uboLight = new("light", 1);
    private readonly UniformBlockBuffer<MaterialUniform> uboMaterial = new("material", 2);
    private readonly UniformBlockBuffer<ResidentTextureData> uboTextures = new("textures", 3);

    public override void Load()
    {
        //  Note: rendering a triangle manually as this method shows is complicated.
        //  Using built in components like SceneNode, Mesh, Material is the preferred and more intuitive way,
        //  as it would replace most of this code. Still this is a good example of how to use the low level API. 


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


        //  the default shader expects material light and camera uniforms,
        //  so we need to create those and bind them to the shader.
        //  The scene creates the uniform buffer objects for camera,
        //  light and material and handles binding them to the shader.
        //  As we are not using this mechanism we must do it manually.

        //  create buffers for vertices, colors and normals and tell OpenGL to use it       
        vao = new VertexArrayObject();
        vao.AddBuffer(VertexDeclarations.VertexPosition, vertices, name: "VBO Position");
        vao.AddBuffer(new VertexDeclaration(new VertexAttribLayout(VertexAttribLocation.Color, 3, VertexAttribType.Float)), colors, name: "VBO Color");
        vao.AddBuffer(new VertexDeclaration(new VertexAttribLayout(VertexAttribLocation.Normal, 3, VertexAttribType.Float)), normals, name: "VBO Normal");
        GL.BindVertexArray(vao);

        var modelMatrix = Matrix4.Identity;
        defaultShader.Use();
        defaultShader.SetMatrix4("model", ref modelMatrix);

        //  bind three uniform blocks to the shader
        if (uboCamera.IsUniformBlockSupported(defaultShader)) uboCamera.BindToShaderProgram(defaultShader);
        if (uboLight.IsUniformBlockSupported(defaultShader)) uboLight.BindToShaderProgram(defaultShader);
        if (uboMaterial.IsUniformBlockSupported(defaultShader)) uboMaterial.BindToShaderProgram(defaultShader);
        if (uboTextures.IsUniformBlockSupported(defaultShader)) uboTextures.BindToShaderProgram(defaultShader);

        // upload light, material, camera and texture data to uniform blocks
        var lightUniform = new LightUniform()
        {
            Direction = new Vector3(0, 0, -1),
            Ambient = new Vector3(0.010f),
            Diffuse = new Vector3(1),
        };

        //  usually the material uniform is populated from the material assigned to a scene node,
        //  here we just create a default material that provides reasonable defaults and textures
        var material = Material.Default;
        var materialUniform = new MaterialUniform()
        {
            Diffuse = material.DiffuseColor,
            Emissive = material.EmissiveColor,
            Specular = material.SpecularColor,
        };
        ResidentTextureData textureData = new()
        {
            Diffuse = material.BindlessTextures[0],
            Detail = material.BindlessTextures[1],
            Normal = material.BindlessTextures[2],
            Specular = material.BindlessTextures[3],
            Bump = material.BindlessTextures[4],
            T6 = material.BindlessTextures[5],
            T7 = material.BindlessTextures[6],
            T8 = material.BindlessTextures[7]
        };
        var cam = new CameraUniform()
        {
            view = camera.ViewMatrix,
            projection = camera.ProjectionMatrix,
            position = camera.Position,
            direction = camera.Front
        };
        uboLight.UpdateSettings(ref lightUniform);
        uboMaterial.UpdateSettings(ref materialUniform);
        uboCamera.UpdateSettings(ref cam);
        uboTextures.UpdateSettings(ref textureData);
    }

    public override void RenderFrame(double elapsedSeconds)
    {
        GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
        GL.DrawArrays(PrimitiveType.Triangles, 0, vao.DataLength);
    }
}
