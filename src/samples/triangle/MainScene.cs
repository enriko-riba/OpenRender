using OpenRender.Core.Rendering;
using OpenRender.SceneManagement;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace Samples.Triangle;

internal class MainScene :Scene
{
    //private VertexBuffer vb = default!;
    private VertexArrayObject vao = default!;
    
    public override void Load()
    {
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

        vao = new VertexArrayObject();
        GL.BindVertexArray(vao);
        vao.AddBuffer(new VertexDeclaration(new VertexAttribLayout(0, 3, VertexAttribType.Float)), vertices, "Triangle");

        //  create a vertex buffer from the vertices an tell OpenGL to use it       
        //vb = new VertexBuffer(VertexDeclarations.VertexPosition, vertices);        
        //GL.BindVertexArray(vb.Vao);        

        var modelMatrix = Matrix4.Identity;
        var shader = defaultShader;
        shader.Use();
        shader.SetMatrix4("model", ref modelMatrix);
    }

    public override void RenderFrame(double elapsedSeconds)
    {
        base.RenderFrame(elapsedSeconds);
        //GL.DrawArrays(PrimitiveType.Triangles, 0, vb.Data.Length);
        GL.DrawArrays(PrimitiveType.Triangles, 0, vao.DataLength);
    } 
}
