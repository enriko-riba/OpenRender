using OpenRender.Core;
using OpenRender.Core.Culling;
using OpenRender.Core.Geometry;
using OpenRender.Core.Rendering;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;

namespace OpenRender.SceneManagement;

public class SceneNode
{
    private bool showBoundingSphere;
    private Mesh mesh;
    private readonly SphereMeshRenderer sphereMeshRenderer = SphereMeshRenderer.DefaultSphereMeshRenderer;
    private readonly List<SceneNode> children = new();

    protected Transform transform = new();

    public SceneNode()
    {
        SetScale(Vector3.One);
        SetPosition(Vector3.Zero);
        SetRotation(Vector3.Zero);
        RenderGroup = RenderGroup.Default;
    }

    public SceneNode(Mesh mesh, Material? material = default, Vector3? position = default) : this()
    {
        Material = material ?? new();
        SetMesh(mesh);
        if (position != null) SetPosition(position.Value);
    }

    public BoundingSphere BoundingSphere => mesh.BoundingSphere;

    public IEnumerable<SceneNode> Children => children;

    public SceneNode? Parent { get; private set; }

    public Vector3 AngleRotation { get; private set; }

    public Material Material { get; set; } = default!;

    public bool IsVisible
    {
        get => !FrameBits.HasFlag(FrameBitsFlags.NotVisible);
        set
        {
            if (value)
                FrameBits.ClearFlag(FrameBitsFlags.NotVisible);
            else
                FrameBits.SetFlag(FrameBitsFlags.NotVisible);
        }
    }
    public bool IsBatchingAllowed
    {
        get => FrameBits.HasFlag(FrameBitsFlags.BatchAllowed);
        set
        {
            if (value)
                FrameBits.ClearFlag(FrameBitsFlags.BatchAllowed);
            else
                FrameBits.SetFlag(FrameBitsFlags.BatchAllowed);
        }
    }

    public void GetWorldMatrix(out Matrix4 worldMatrix) => worldMatrix = transform.worldMatrix;

    public void GetRotationMatrix(out Matrix4 rotationMatrix) => rotationMatrix = transform.rotationMatrix;

    public ref Mesh Mesh => ref mesh;

    public void SetMesh(in Mesh mesh)
    {
        this.mesh = mesh;
        var vb = mesh.Vao?.VertexBuffer;
        if (vb != null)
        {
            var bs = CullingHelper.CalculateBoundingSphere(vb.Data);
            this.mesh.BoundingSphere = bs with
            {
                Radius = bs.LocalRadius * MathF.MaxMagnitude(MathF.MaxMagnitude(transform.Scale.X, transform.Scale.Y), transform.Scale.Z),
                Center = bs.LocalCenter + transform.Position,
            };
        }
    }

    public bool ShowBoundingSphere
    {
        get => showBoundingSphere || (Scene?.ShowBoundingSphere ?? false);
        set => showBoundingSphere = value;
    }

    /// <summary>
    /// If true, the <see cref="SceneNode"/> will not be culled. This is useful for 2D sprites and UI elements.
    /// </summary>
    public bool DisableCulling { get; set; }

    /// <summary>
    /// The rendering layer of the <see cref="SceneNode"/>, affects depth sorting order.
    /// </summary>
    public RenderGroup RenderGroup { get; set; }

    /// <summary>
    /// Action method invoked from <see cref="OnUpdate(Scene, double)"/>.
    /// When inheriting <see cref="SceneNode"/> overriding the <see cref="OnUpdate(Scene, double)"/> method is the preferred way of adding custom update logic.
    /// </summary>
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
        GL.BindVertexArray(mesh.Vao);
        if (mesh.Vao.DrawMode == DrawMode.Indexed)
            GL.DrawElements(PrimitiveType.Triangles, mesh.Vao.DataLength, DrawElementsType.UnsignedInt, 0);
        else
            GL.DrawArrays(PrimitiveType.Triangles, 0, mesh.Vao.DataLength);

        if (ShowBoundingSphere)
        {
            sphereMeshRenderer.Shader.SetMatrix4("model", ref transform.worldMatrix);
            sphereMeshRenderer.Render();
        }
        //GL.BindVertexArray(0);
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
    public void GetPosition(out Vector3 position) => position = transform.Position;

    /// <summary>
    /// Sets the new position.
    /// </summary>
    /// <param name="position"></param>
    public void SetPosition(in Vector3 position)
    {
        transform.Position = position;
        Invalidate();
    }

    /// <summary>
    /// Gets the nodes rotation quaternion.
    /// </summary>
    /// <param name="rotation"></param>
    public void GetRotation(out Quaternion rotation) => rotation = transform.Rotation;

    /// <summary>
    /// Sets the new rotation quaternion from Euler angles in radians.
    /// </summary>
    /// <param name="rot"></param>
    public void SetRotation(in Vector3 eulerRot)
    {
        Quaternion.FromEulerAngles(eulerRot, out transform.Rotation);
        Matrix4.CreateFromQuaternion(transform.Rotation, out transform.rotationMatrix);
        AngleRotation = eulerRot;
        Invalidate();
    }

    /// <summary>
    /// Gets the node scale.
    /// </summary>
    /// <param name="scale"></param>
    public void GetScale(out Vector3 scale) => scale = transform.Scale;

    /// <summary>
    /// Sets the node Scale.
    /// </summary>
    /// <param name="scale"></param>
    public virtual void SetScale(in Vector3 scale)
    {
        transform.Scale = scale;
        Invalidate();
    }

    /// <summary>
    /// Sets the node Scale.
    /// </summary>
    /// <param name="scale"></param>
    public void SetScale(float scale)
    {
        transform.Scale.X = scale;
        transform.Scale.Y = scale;
        transform.Scale.Z = scale;
        Invalidate();
    }

    /// <summary>
    /// Handles the viewport resize event. The base class has no implementation so invoking base.OnResize() does nothing.
    /// </summary>
    /// <param name="scene"></param>
    /// <param name="e"></param>
    public virtual void OnResize(Scene scene, ResizeEventArgs e) { }


    /// <summary>
    /// Calculates the world matrix (SROT).
    /// </summary>
    protected virtual void UpdateMatrix()
    {
        var parent = Parent?.transform;
        transform.UpdateMatrix(parent);
        //Matrix4.CreateScale(scale, out scaleMatrix);
        //Matrix4.CreateFromQuaternion(rotation, out rotationMatrix);
        //Matrix4.Mult(scaleMatrix, rotationMatrix, out worldMatrix);
        //worldMatrix.Row3.Xyz = position;    //  sets the translation

        //if (Parent != null)
        //{
        //    Matrix4.Mult(worldMatrix, Parent.worldMatrix, out worldMatrix);
        //}
    }

    internal void Invalidate()
    {
        UpdateMatrix();
        mesh.BoundingSphere.Update(in transform.Scale, in transform.worldMatrix);
        foreach (var child in children)
        {
            child.Invalidate();
        }
    }

    /// <summary>
    /// Reference to the parent Scene
    /// </summary>
    internal Scene? Scene { get; set; }

    internal FrameBits FrameBits;
}
