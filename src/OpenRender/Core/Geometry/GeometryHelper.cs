using OpenRender.Core.Rendering;

namespace OpenRender.Core.Geometry;

/// <summary>
/// Helper class for creating basic geometry.
/// </summary>
public static class GeometryHelper
{
    private const float HALF = 0.5f;

    /// <summary>
    /// Creates quad geometry with an indexed VertexPositionTexture or VertexPositionNormalTexture VB.
    /// </summary>
    /// <returns></returns>
    public static (Vertex2D[] vertices, uint[] indices) Create2dQuad()
    {
        Vertex2D[] vertices;
        vertices = Create2dQuadWithoutNormals();

        uint[] indices =
        {
            0, 1, 3,
            0, 3, 2
        };

        return (vertices, indices);
    }

    /// <summary>
    /// Creates quad geometry with an indexed VertexPositionTexture or VertexPositionNormalTexture VB.
    /// </summary>
    /// <returns></returns>
    public static (Vertex[] vertices, uint[] indices) CreateQuad()
    {
        Vertex[] vertices = CreateQuadWithNormals();

        uint[] indices =
        {
            0, 1, 3,
            0, 3, 2
        };

        return (vertices, indices);
    }

    /// <summary>
    /// Creates cube geometry with an indexed VertexPositionTexture or VertexPositionNormalTexture VB.
    /// Note: a cube is a simplified version of the "box" geometry where all vertices are shared. 
    /// This has a side effect that since normals need to be shared too. Therefore the normals are pointing 
    /// form the cube center towards the vertex as if the cube is a sphere. The "box" geometry has physically 
    /// correct normals (pointing orthogonal from the surface out) at the cost of having no shared vertices
    /// between box sides.
    /// </summary>
    /// <returns></returns>
    public static (Vertex[] vertices, uint[] indices) CreateCube()
    {
        var vertices = CreateCubeWithNormals();
        uint[] indices =
        {
            // front quad
            2, 1, 0,
            2, 3, 1,

            // left quad
            0, 5, 4,
            0, 1, 5,

            // back quad
            4, 7, 6,
            4, 5, 7,

            // right quad
            6, 3, 2,
            6, 7, 3,

            // up quad            
            3, 8, 1,
            3, 9, 8,

            // down quad                                
            11, 0, 10,
            11, 2, 0
        };

        return (vertices, indices);
    }

    /// <summary>
    /// Creates a box geometry consisting of six surfaces each having own vertices in order to support different normals per surface.
    /// Note: a box is similar to a "cube" but the "cube" has less vertices as each vertex is shared between three sides of the box.
    /// </summary>
    /// <returns></returns>
    public static (Vertex[] vertices, uint[] indices) CreateBox()
    {
        var vertices = CreateBoxVerticesWithNormals();
        uint[] indices =
        {
            // front quad
            2, 1, 0,
            2, 3, 1,

            // back quad
            4, 7, 6,
            4, 5, 7,

            // left quad
            10, 9, 8,
            10, 11, 9,

            // right quad
            14, 13, 12,
            14, 15, 13,
            
            // up quad            
            18, 17, 16,
            18, 19, 17,

            // down quad                                
            22, 21, 20,
            22, 23, 21
        };
        return (vertices, indices);
    }

    public static (Vertex[] vertices, uint[] indices) CreateSphere(int stacks, int slices)
    {
        var vertices = new List<Vertex>();
        var indices = new List<uint>();

        for (var stack = 0; stack <= stacks; stack++)
        {
            var theta = stack * MathF.PI / stacks;
            var sinTheta = MathF.Sin(theta);
            var cosTheta = MathF.Cos(theta);

            for (var slice = 0; slice <= slices; slice++)
            {
                var phi = slice * 2 * MathF.PI / slices;
                var sinPhi = MathF.Sin(phi);
                var cosPhi = MathF.Cos(phi);

                var x = cosPhi * sinTheta;
                var y = cosTheta;
                var z = sinPhi * sinTheta;

                var nx = x;
                var ny = y;
                var nz = z;

                var s = 1 - (float)slice / slices;
                var t = 1 - (float)stack / stacks;

                vertices.Add(new Vertex(x, y, z, nx, ny, nz, s, t));
            }
        }

        for (var ring = 0; ring < stacks; ring++)
        {
            var ringStart = ring * (slices + 1);
            var nextRingStart = (ring + 1) * (slices + 1);

            for (var side = 0; side < slices; side++)
            {
                if (ring != 0)
                {
                    indices.Add((uint)(ringStart + side));
                    indices.Add((uint)(ringStart + side) + 1);
                    indices.Add((uint)(nextRingStart + side));
                }

                if (ring != stacks - 1)
                {
                    indices.Add((uint)(ringStart + side) + 1);
                    indices.Add((uint)(nextRingStart + side) + 1);
                    indices.Add((uint)(nextRingStart + side));
                }
            }
        }
        return (vertices.ToArray(), indices.ToArray());
    }

    private static Vertex[] CreateBoxVerticesWithNormals()
    {
        Vertex[] vertices =
        {
            // Position           Normal        Texture
            //  FRONT SIDE (z = +)
            new Vertex(-HALF, -HALF,  HALF,   0,  0,  1,   0, 1),   // lower left - 0
            new Vertex(-HALF,  HALF,  HALF,   0,  0,  1,   0, 0),   // upper left - 1
            new Vertex( HALF, -HALF,  HALF,   0,  0,  1,   1, 1),   // lower right - 2
            new Vertex( HALF,  HALF,  HALF,   0,  0,  1,   1, 0),   // upper right - 3
                                           
            //  BACK SIDE (z = -)
            new Vertex(-HALF, -HALF, -HALF,   0,  0, -1,   0, 1),   // lower left - 4
            new Vertex(-HALF,  HALF, -HALF,   0,  0, -1,   0, 0),   // upper left - 5
            new Vertex( HALF, -HALF, -HALF,   0,  0, -1,   1, 1),   // lower right - 6
            new Vertex( HALF,  HALF, -HALF,   0,  0, -1,   1, 0),   // upper right - 7
                                                               
            //  LEFT SIDE (X = -)
            new Vertex(-HALF, -HALF, -HALF,  -1,  0,  0,   0, 1),   // lower left  - 8
            new Vertex(-HALF,  HALF, -HALF,  -1,  0,  0,   0, 0),   // upper left - 9
            new Vertex(-HALF, -HALF,  HALF,  -1,  0,  0,   1, 1),   // lower right - 10
            new Vertex(-HALF,  HALF,  HALF,  -1,  0,  0,   1, 0),   // upper right - 11

            //  RIGHT SIDE (X = +)
            new Vertex(HALF, -HALF,  HALF,   1,  0,  0,   0, 1),   // lower left  - 12
            new Vertex(HALF,  HALF,  HALF,   1,  0,  0,   0, 0),   // upper left - 13
            new Vertex(HALF, -HALF, -HALF,   1,  0,  0,   1, 1),   // lower right - 14
            new Vertex(HALF,  HALF, -HALF,   1,  0,  0,   1, 0),   // upper right - 15            
            
            //  UPPER SIDE (Y = +)
            new Vertex(-HALF,  HALF,  HALF,   0,  1,  0,   0, 1),   // lower left - 16
            new Vertex(-HALF,  HALF, -HALF,   0,  1,  0,   0, 0),   // upper left - 17
            new Vertex( HALF,  HALF,  HALF,   0,  1,  0,   1, 1),   // lower right - 18
            new Vertex( HALF,  HALF, -HALF,   0,  1,  0,   1, 0),   // upper right - 19

            //  lower SIDE (Y = -)
            new Vertex(-HALF, -HALF, -HALF,   0, -1,  0,   0, 1),   // lower left - 20
            new Vertex(-HALF, -HALF,  HALF,   0, -1,  0,   0, 0),   // upper left - 21
            new Vertex( HALF, -HALF, -HALF,   0, -1,  0,   1, 1),   // lower right - 22
            new Vertex( HALF, -HALF,  HALF,   0, -1,  0,   1, 0),   // upper right - 23             
        };
        return vertices;
    }

    //public static float[] CreateBoxVerticesWithoutNormals()
    //{
    //    float[] vertices =
    //    {
    //        // Position            Texture
    //        //  FRONT SIDE (z = +)
    //        -HALF, -HALF,  HALF,   0, 1,   // lower left - 0
    //        -HALF,  HALF,  HALF,   0, 0,   // upper left - 1
    //         HALF, -HALF,  HALF,   1, 1,   // lower right - 2
    //         HALF,  HALF,  HALF,   1, 0,   // upper right - 3

    //        //  BACK SIDE (z = -)
    //        -HALF, -HALF, -HALF,   1, 1,   // lower left - 4
    //        -HALF,  HALF, -HALF,   1, 0,   // upper left - 5
    //         HALF, -HALF, -HALF,   0, 1,   // lower right - 6
    //         HALF,  HALF, -HALF,   0, 0,   // upper right - 7

    //        //  LEFT SIDE (X = -)
    //        -HALF, -HALF, -HALF,   0, 1,   // lower left  - 8
    //        -HALF,  HALF, -HALF,   0, 0,   // upper left - 9
    //        -HALF, -HALF,  HALF,   1, 1,   // lower right - 10
    //        -HALF,  HALF,  HALF,   1, 0,   // upper right - 11

    //        //  RIGHT SIDE (X = +)
    //         HALF, -HALF, -HALF,   0, 1,   // lower left  - 12
    //         HALF,  HALF, -HALF,   0, 0,   // upper left - 13
    //         HALF, -HALF,  HALF,   1, 1,   // lower right - 14
    //         HALF,  HALF,  HALF,   1, 0,   // upper right - 15

    //        //  UPPER SIDE (Y = +)
    //        -HALF,  HALF, -HALF,   0, 1,   // lower left  - 16
    //        -HALF,  HALF,  HALF,   0, 0,   // upper left - 17
    //         HALF,  HALF, -HALF,   1, 1,   // lower right - 18
    //         HALF,  HALF,  HALF,   1, 0,   // upper right - 19

    //        //  lower SIDE (Y = -)
    //         HALF, -HALF,  HALF,   0, 1,   // lower left  - 16
    //         HALF, -HALF, -HALF,   0, 0,   // upper left - 17
    //        -HALF, -HALF,  HALF,   1, 1,   // lower right - 18
    //        -HALF, -HALF, -HALF,   1, 0,   // upper right - 19
    //    };
    //    return vertices;
    //}

    private static Vertex[] CreateCubeWithNormals()
    {
        Vertex[] vertices =
        {
            // Position           Normal        Texture

            //  FRONT SIDE VERTICES
            new Vertex(-HALF, -HALF,  HALF,  -1, -1, 1,    0, 1),   // lower left - 0
            new Vertex(-HALF,  HALF,  HALF,  -1,  1, 1,    0, 0),   // upper left - 1
            new Vertex( HALF, -HALF,  HALF,   1, -1, 1,    1, 1),   // lower right - 2
            new Vertex( HALF,  HALF,  HALF,   1,  1, 1,    1, 0),   // upper right - 3
            
            //  BACK SIDE VERTICES
            new Vertex(-HALF, -HALF, -HALF,  -1, -1, -1,   1, 1),   // lower left
            new Vertex(-HALF,  HALF, -HALF,  -1,  1, -1,   1, 0),   // upper left
            new Vertex( HALF, -HALF, -HALF,   1, -1, -1,   0, 1),   // lower right
            new Vertex( HALF,  HALF, -HALF,   1,  1, -1,   0, 0),   // upper right
                                                             
            new Vertex(-HALF,  HALF, -HALF,  -1,  1, -1,   0, 1),   // upper left 2nd
            new Vertex( HALF,  HALF, -HALF,   1,  1, -1,   1, 1),   // upper right 2nd
            new Vertex(-HALF, -HALF, -HALF,  -1, -1, -1,   0, 0),   // lower left 2nd
            new Vertex( HALF, -HALF, -HALF,   1, -1, -1,   1, 0),   // lower right 2nd            
        };
        return vertices;
    }

    //private static float[] CreateCubeWithoutNormals()
    //{
    //    float[] vertices =
    //    {
    //        // Position           Texture

    //        //  FRONT SIDE VERTICES
    //        -HALF, -HALF,  HALF,   0, 1,   // lower left - 0
    //        -HALF,  HALF,  HALF,   0, 0,   // upper left - 1
    //         HALF, -HALF,  HALF,   1, 1,   // lower right - 2
    //         HALF,  HALF,  HALF,   1, 0,   // upper right - 3

    //        //  BACK SIDE VERTICES
    //        -HALF, -HALF, -HALF,   1, 1,   // lower left
    //        -HALF,  HALF, -HALF,   1, 0,   // upper left
    //         HALF, -HALF, -HALF,   0, 1,   // lower right
    //         HALF,  HALF, -HALF,   0, 0,   // upper right

    //        -HALF,  HALF, -HALF,   0, 1,   // upper left 2nd
    //         HALF,  HALF, -HALF,   1, 1,   // upper right 2nd
    //        -HALF, -HALF, -HALF,   0, 0,   // lower left 2nd
    //         HALF, -HALF, -HALF,   1, 0,   // lower right 2nd  
    //    };
    //    return vertices;
    //}

    //private static float[] CreateQuadWithoutNormals()
    //{
    //    //  create the 4 vertices with: 3 floats for position + 2 floats for uv
    //    var vertices = new float[]
    //    {
    //        -HALF, -HALF, 0,    0, 1,   // lower left corner
    //        -HALF,  HALF, 0,    0, 0,   // upper left corner
    //         HALF, -HALF, 0,    1, 1,   // lower right corner
    //         HALF,  HALF, 0,    1, 0,   // upper right corner
    //    };
    //    return vertices;
    //}

    private static Vertex2D[] Create2dQuadWithoutNormals()
    {
        //  create the 4 vertices with: 2 floats for position + 2 floats for uv
        //  note: the coordinates go from 0 to 1 because we want the sprite to align with screen coordinates.
        var vertices = new Vertex2D[]
        {
             new Vertex2D(0, 0,  0, 1),   // lower left corner
             new Vertex2D(0, 1,  0, 0),   // upper left corner
             new Vertex2D(1, 0,  1, 1),   // lower right corner
             new Vertex2D(1, 1,  1, 0),   // upper right corner
        };
        return vertices;
    }

    private static Vertex[] CreateQuadWithNormals()
    {
        //  create the 4 vertices with: 3 floats for position + 3 floats for normal + 2 floats for uv 
        var vertices = new Vertex[]
        {
            new Vertex(-HALF, -HALF, 0,    -HALF, -HALF, 1,     0, 1),   // lower left corner
            new Vertex(-HALF,  HALF, 0,    -HALF, -HALF, 1,     0, 0),   // upper left corner
            new Vertex( HALF, -HALF, 0,     HALF, -HALF, 1,     1, 1),   // lower right corner
            new Vertex( HALF,  HALF, 0,     HALF,  HALF, 1,     1, 0),   // upper right corner
        };
        return vertices;
    }
}
