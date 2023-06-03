using OpenRender.Core;
using OpenRender.Core.Geometry;
using OpenRender.Core.Rendering;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace OpenRender.SceneManagement;

public class SceneNode
{
    private Vector3 position;
    private Vector3 scale;
    private Quaternion rotation = Quaternion.Identity;
    private Matrix4 scaleMatrix = Matrix4.Identity;
    private Matrix4 rotationMatrix = Matrix4.Identity;
    private Matrix4 worldMatrix = Matrix4.Identity;
    private BoundingSphere boundingSphere;
    private Mesh mesh;
    private bool showBoundingSphere;
    private readonly SphereMeshRenderer sphereMeshRenderer = SphereMeshRenderer.DefaultSphereMeshRenderer;
    private readonly List<SceneNode> children = new();

    public SceneNode(Mesh mesh, Material? material = default, Vector3 position = default)
    {
        Material = material ?? new();
        SetScale(Vector3.One);
        SetPosition(position);
        SetRotation(Vector3.Zero);
        SetMesh(ref mesh);
    }

    public BoundingSphere BoundingSphere => boundingSphere;

    public IEnumerable<SceneNode> Children => children;

    public SceneNode? Parent { get; private set; }

    public Vector3 AngleRotation { get; private set; }

    public Material Material { get; set; } = default!;

    public void GetWorldMatrix(out Matrix4 worldMatrix)
    {
        worldMatrix = this.worldMatrix;
    }

    public void GetMesh(out Mesh mesh)
    {
        mesh = this.mesh;
    }

    public void SetMesh(ref Mesh mesh)
    {
        this.mesh = mesh;
        var bs = CullingHelper.CalculateBoundingSphere(mesh.VertexBuffer);
        boundingSphere = bs with
        {
            Radius = bs.LocalRadius * Math.Max(Math.Max(scale.X, scale.Y), scale.Z),
            Center = bs.LocalCenter + position,
        };
    }

    public bool ShowBoundingSphere
    {
        get => showBoundingSphere || (Scene?.ShowBoundingSphere ?? false);
        set => showBoundingSphere = value;
    }

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
        if (ShowBoundingSphere)
        {
            sphereMeshRenderer.Shader.SetMatrix4("model", worldMatrix);
            sphereMeshRenderer.Render();
        }
        else
        {
            GL.BindVertexArray(mesh.VertexBuffer.Vao);
            if (mesh.DrawMode == DrawMode.Indexed)
                GL.DrawElements(PrimitiveType.Triangles, mesh.VertexBuffer.Indices!.Length, DrawElementsType.UnsignedInt, 0);
            else
                GL.DrawArrays(PrimitiveType.Triangles, 0, mesh.VertexBuffer.Vertices.Length);
            GL.BindVertexArray(0);
        }
    }

    public void AddChild(SceneNode child)
    {
        children.Add(child);
        child.Parent = this;
        Invalidate();
        Scene?.RemoveNode(child);
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
    /// Gets the node position.
    /// </summary>
    /// <param name="position"></param>
    public void GetPosition(out Vector3 position)
    {
        position = this.position;
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
    /// Gets the nodes rotation quaternion.
    /// </summary>
    /// <param name="rotation"></param>
    public void GetRotation(out Quaternion rotation)
    {
        rotation = this.rotation;
    }

    /// <summary>
    /// Sets the new rotation from Euler angles.
    /// </summary>
    /// <param name="rot"></param>
    public void SetRotation(in Vector3 eulerRot)
    {
        Quaternion.FromEulerAngles(eulerRot, out rotation);
        Matrix4.CreateFromQuaternion(rotation, out rotationMatrix);
        AngleRotation = eulerRot;
        Invalidate();
    }

    /// <summary>
    /// Gets the node scale.
    /// </summary>
    /// <param name="scale"></param>
    public void GetScale(out Vector3 scale)
    {
        scale = this.scale;
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
        Matrix4.CreateScale(scale, out scaleMatrix);
        Matrix4.CreateFromQuaternion(rotation, out rotationMatrix);
        Matrix4.Mult(scaleMatrix, rotationMatrix, out worldMatrix);
        worldMatrix.Row3.Xyz = position;    //  sets the translation

        if (Parent != null)
            Matrix4.Mult(worldMatrix, Parent.worldMatrix, out worldMatrix);
    }

    internal void Invalidate()
    {
        UpdateMatrix();
        boundingSphere.Update(in scale, in worldMatrix);
        foreach (var child in children)
        {
            child.Invalidate();
        }
    }

    internal Scene? Scene { get; set; } // Reference to the parent Scene
}
