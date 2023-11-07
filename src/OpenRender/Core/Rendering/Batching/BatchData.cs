using OpenRender.Core.Buffers;
using OpenRender.Core.Textures;
using OpenRender.SceneManagement;
using OpenTK.Mathematics;

namespace OpenRender.Core.Rendering.Batching;

/// <summary>
/// Holds batch related data like commands, SSBO buffers, batch geometry.
/// </summary>
internal class BatchData
{
    public BatchData(Shader shader, VertexDeclaration vertexDeclaration, int maxBatchSize)
    {
        Shader = shader;
        VertexDeclaration = vertexDeclaration;
        CommandsDataArray = new DrawElementsIndirectCommand[maxBatchSize];
        WorldMatricesDataArray = new Matrix4[maxBatchSize];
        MaterialDataArray = new MaterialData[maxBatchSize];
        TextureDataArray = new TextureData[maxBatchSize];
        LastIndex = -1;
    }

    public VertexArrayObject Vao = new();
    public List<float> Vertices = new();
    public List<uint> Indices = new();

    public uint CommandsBufferName;
    public uint WorldMatricesBufferName;
    public uint MaterialsBufferName;
    public uint TexturesBufferName;

    public VertexDeclaration VertexDeclaration;
    public Shader Shader;

    /// <summary>
    /// Contains SceneNode ID mappings to array indices.
    /// </summary>
    public Dictionary<uint, int> MapperDict = new();

    public DrawElementsIndirectCommand[] CommandsDataArray;
    public Matrix4[] WorldMatricesDataArray;
    public MaterialData[] MaterialDataArray;
    public TextureData[] TextureDataArray;
    public int LastIndex;


    public void WriteDrawCommand(SceneNode node)
    {
        var nodeIndices = node.Mesh.Indices;
        var cmd = new DrawElementsIndirectCommand
        {
            Count = nodeIndices.Length,
            InstanceCount = 0,
            FirstIndex = Indices.Count,
            BaseVertex = Vertices.Count / node.Mesh.VertexDeclaration.StrideInFloats,
            BaseInstance = 0
        };

        //  write material data, this is a but more complex as we need bindless textures
        var materialData = new MaterialData()
        {
            Diffuse = node.Material.DiffuseColor,
            Emissive = node.Material.EmissiveColor,
            Specular = node.Material.SpecularColor,
            Shininess = node.Material.Shininess,
            DetailTextureFactor = node.Material.DetailTextureFactor,
            HasDiffuse = node.Material.HasDiffuse ? 1 : 0,
            HasNormal = 0,
        };

        TextureData textureData = new()
        {
            Diffuse = node.Material.BindlessTextures[0]
        };
        TextureBase.MakeResident(textureData.Diffuse);
        
        //  TODO: handle detail and normal textures residency

        //if(node.Material.HasDetail)
        //{
        //    var detailTextureIndex = node.Material.TextureDescriptors!.First(ti => ti?.TextureType == TextureType.Detail);
        //    var detailTexture = node.Material.Textures![detailTextureIndex];
        //    textureData.Detail = GL.Arb.GetTextureHandle(node.Material.Textures[0].Handle);
        //    if (!TextureDataArray.Any(x => x.Diffuse == textureData.Diffuse))
        //        GL.Arb.MakeTextureHandleResident(textureData.Diffuse);
        //}

        node.GetTransform(out var transform);

        MapperDict[node.Id] = ++LastIndex;
        TextureDataArray[LastIndex] = textureData;
        MaterialDataArray[LastIndex] = materialData;
        WorldMatricesDataArray[LastIndex] = transform.worldMatrix;
        CommandsDataArray[LastIndex] = cmd;

        Vertices.AddRange(node.Mesh.Vertices);
        Indices.AddRange(nodeIndices);
    }

    public void UpdateCommand(SceneNode node, bool shouldRender)
    {
        var idx = MapperDict[node.Id];
        CommandsDataArray[idx].InstanceCount = shouldRender ? 1 : 0;
        node.GetTransform(out var transform);
        WorldMatricesDataArray[idx] = transform.worldMatrix;
    }
}