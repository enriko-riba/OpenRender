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
        TextureDataArray = new ResidentTextureData[maxBatchSize];
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
    public ResidentTextureData[] TextureDataArray;
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

        var materialData = new MaterialData()
        {
            Diffuse = node.Material.DiffuseColor,
            Emissive = node.Material.EmissiveColor,
            Specular = node.Material.SpecularColor,
            Shininess = node.Material.Shininess,
            DetailTextureScaleFactor = node.Material.DetailTextureScaleFactor,
            DetailTextureBlendFactor = node.Material.DetailTextureBlendFactor,
        };

        ResidentTextureData textureData = new()
        {
            Diffuse = node.Material.BindlessTextureHandles[0],
            Detail = node.Material.BindlessTextureHandles[1],
            Normal = node.Material.BindlessTextureHandles[2],
            Specular = node.Material.BindlessTextureHandles[3],
            Bump = node.Material.BindlessTextureHandles[4],
            T6 = node.Material.BindlessTextureHandles[5],
            T7 = node.Material.BindlessTextureHandles[6],
            T8 = node.Material.BindlessTextureHandles[7],
        };
        Texture.MakeResident(textureData.Diffuse);
        Texture.MakeResident(textureData.Detail);
        Texture.MakeResident(textureData.Normal);
        Texture.MakeResident(textureData.Specular);
        Texture.MakeResident(textureData.Bump);
        Texture.MakeResident(textureData.T6);
        Texture.MakeResident(textureData.T7);
        Texture.MakeResident(textureData.T8);


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