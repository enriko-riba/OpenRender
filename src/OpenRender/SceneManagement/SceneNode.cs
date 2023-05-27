using OpenRender.Core;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace OpenRender.SceneManagement;

public class SceneNode
{
    private Vector3 position;
    private Vector3 scale;
    private Quaternion rotation;
    private Matrix4 scaleMatrix;
    private Matrix4 rotationMatrix;
    private Matrix4 worldMatrix;

    private readonly List<SceneNode> children = new();
    
    public SceneNode(Mesh mesh, Vector3 position = default)
    {
        Mesh = mesh;
        SetScale(Vector3.One);
        SetPosition(position);
        SetRotation(Vector3.Zero);
    }

    public IEnumerable<SceneNode> Children => children;

    public SceneNode? Parent { get; private set; }

    public Matrix4 World => worldMatrix;

    public Vector3 AngleRotation { get; private set; }

    public Vector3 Position => position;

    public Vector3 Scale => scale;

    public Quaternion Rotation => rotation;

    public Mesh Mesh;

    public Action<SceneNode, double>? Update { get; set; }

    public virtual void OnUpdate(Scene scene, double elapsed)
    {
        Update?.Invoke(this, elapsed);
        foreach (var child in children)
        {
            child.OnUpdate(scene, elapsed);
        }
    }

    public virtual void OnDraw(Scene scene, double elapsed)
    {
        GL.BindVertexArray(Mesh.VertexBuffer.Vao);
        if (Mesh.DrawMode == DrawMode.Indexed)
            GL.DrawElements(PrimitiveType.Triangles, Mesh.VertexBuffer.Indices!.Length, DrawElementsType.UnsignedInt, 0);
        else
            GL.DrawArrays(PrimitiveType.Triangles, 0, Mesh.VertexBuffer.Vertices.Length);
        GL.BindVertexArray(0);
    }

    public void AddChild(SceneNode child)
    {
        children.Add(child);
        child.Parent = this;
        Invalidate();
        Scene?.AddNode(child); // Add the child node to the Scene's master list
    }

    public void RemoveChild(SceneNode child)
    {
        if (children.Remove(child))
        {
            child.Parent = null;
            Invalidate();
            Scene?.RemoveNode(child); // Remove the child node from the Scene's master list
        }
    }

    /// <summary>
    /// Sets the new position.
    /// </summary>
    /// <param name="position"></param>
    public void SetPosition(in Vector3 position)
    {
        this.position = position;
        Invalidate();
    }

    /// <summary>
    /// Sets the new rotation from Euler angles.
    /// </summary>
    /// <param name="rot"></param>
    public void SetRotation(in Vector3 eulerRot)
    {
        rotation = Quaternion.FromEulerAngles(eulerRot);
        rotationMatrix = Matrix4.CreateFromQuaternion(rotation);
        AngleRotation = eulerRot;
        Invalidate();
    }

    /// <summary>
    /// Sets the node Scale.
    /// </summary>
    /// <param name="scale"></param>
    public void SetScale(in Vector3 scale)
    {
        this.scale = scale;
        Invalidate();
    }

    /// <summary>
    /// Sets the node Scale.
    /// </summary>
    /// <param name="scale"></param>
    public void SetScale(float scale)
    {
        this.scale.X = scale;
        this.scale.Y = scale;
        this.scale.Z = scale;
        Invalidate();
    }

    /// <summary>
    /// Calculates the world matrix (SROT).
    /// </summary>
    private void UpdateMatrix()
    {
        scaleMatrix = Matrix4.CreateScale(Scale);
        rotationMatrix = Matrix4.CreateFromQuaternion(Rotation);
        Matrix4.Mult(scaleMatrix, rotationMatrix, out worldMatrix);
        worldMatrix.Row3.Xyz = Position;    //  sets the translation

        if (Parent != null)
            Matrix4.Mult(worldMatrix, Parent.worldMatrix, out worldMatrix);
    }

    internal void Invalidate()
    {
        UpdateMatrix();
        foreach (var child in children)
        {
            child.Invalidate();
        }
    }

    internal Scene? Scene { get; set; } // Reference to the parent Scene
}
