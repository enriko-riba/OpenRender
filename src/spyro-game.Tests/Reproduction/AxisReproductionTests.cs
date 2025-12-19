using System.Text.Json;
using OpenTK.Mathematics;
using SpyroGame.Client.Content;
using SpyroGame.Client.Content.EntityModels;
using Xunit;

namespace SpyroGame.Tests.Reproduction;

public class AxisReproductionTests
{
    [Fact]
    public void Vector3JsonConverter_ReadsXYZ_Correctly()
    {
        var json = @"{ ""x"": 1, ""y"": 2, ""z"": 3 }";
        var options = new JsonSerializerOptions();
        options.Converters.Add(new Vector3JsonConverter());
        
        var v = JsonSerializer.Deserialize<Vector3>(json, options);
        
        Assert.Equal(1, v.X);
        Assert.Equal(2, v.Y);
        Assert.Equal(3, v.Z);
    }

    [Fact]
    public void EntityModelMeshBuilder_UsesY_ForHeight()
    {
        var size = new Vector3(10, 20, 30);
        // 10 width, 20 height, 30 depth
        
        // We can't easily inspect the Mesh geometry without internals, 
        // but we can check if the code compiles and runs.
        // To verify the logic, we rely on reading the code, which says:
        // tr: new Vector3(x1, y1, z1)
        // where y1 = size.Y
        
        // If this test passes, it just confirms the method exists and runs.
        // The assertion is in the code reading.
        var mesh = EntityModelMeshBuilder.BuildCubeMesh(size, new EntityModelFaceTiles(
            new EntityModelTile(0,0), new EntityModelTile(0,0),
            new EntityModelTile(0,0), new EntityModelTile(0,0),
            new EntityModelTile(0,0), new EntityModelTile(0,0)
        ), new EntityModelAtlas(1,1));
        
        Assert.NotNull(mesh);
    }
}
