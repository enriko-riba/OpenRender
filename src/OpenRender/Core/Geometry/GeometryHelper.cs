﻿using OpenRender.Core.Rendering;

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
    public static VertexArrayObject Create2dQuad()
    {
        VertexDeclaration vxDeclaration;
        float[] vertices;
        vertices = Create2dQuadWithoutNormals();
        vxDeclaration = VertexDeclarations.VertexPosition2DTexture;

        uint[] indices =
        {
            0, 1, 3,
            0, 3, 2
        };
        var vao = new VertexArrayObject();
        vao.AddBuffer(new Buffer<float>(vertices, vxDeclaration));
        vao.AddIndexBuffer(new IndexBuffer(indices));
        return vao;
    }

    /// <summary>
    /// Creates quad geometry with an indexed VertexPositionTexture or VertexPositionNormalTexture VB.
    /// </summary>
    /// <param name="createNormals"></param>
    /// <returns></returns>
    public static VertexArrayObject CreateQuad(bool createNormals)
    {
        VertexDeclaration vxDeclaration;
        float[] vertices;
        if (createNormals)
        {
            vertices = CreateQuadWithNormals();
            vxDeclaration = VertexDeclarations.VertexPositionNormalTexture;
        }
        else
        {
            vertices = CreateQuadWithoutNormals();
            vxDeclaration = VertexDeclarations.VertexPositionTexture;
        }

        uint[] indices =
        {
            0, 1, 3,
            0, 3, 2
        };
        var vao = new VertexArrayObject();
        vao.AddBuffer(new Buffer<float>(vertices, vxDeclaration));
        vao.AddIndexBuffer(new IndexBuffer(indices));
        return vao;
    }

    /// <summary>
    /// Creates cube geometry with an indexed VertexPositionTexture or VertexPositionNormalTexture VB.
    /// Note: a cube is a simplified version of the "box" geometry where all vertices are shared. 
    /// This has a side effect that since normals need to be shared too. Therefore the normals are pointing 
    /// form the cube center towards the vertex as if the cube is a sphere. The "box" geometry has physically 
    /// correct normals (pointing orthogonal from the surface out) at the cost of having no shared vertices
    /// between box sides.
    /// </summary>
    /// <param name="createNormals"></param>
    /// <returns></returns>
    public static VertexArrayObject CreateCube(bool createNormals)
    {
        VertexDeclaration vxDeclaration;
        float[] vertices;
        if (createNormals)
        {
            vertices = CreateCubeWithNormals();
            vxDeclaration = VertexDeclarations.VertexPositionNormalTexture;
        }
        else
        {
            vertices = CreateCubeWithoutNormals();
            vxDeclaration = VertexDeclarations.VertexPositionTexture;
        }

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
        var vao = new VertexArrayObject();
        vao.AddBuffer(new Buffer<float>(vertices, vxDeclaration));
        vao.AddIndexBuffer(new IndexBuffer(indices));
        return vao;
    }

    /// <summary>
    /// Creates a box geometry consisting of six surfaces each having own vertices in order to support different normals per surface.
    /// Note: a box is similar to a "cube" but the "cube" has less vertices as each vertex is shared between three sides of the box.
    /// </summary>
    /// <param name="createNormals"></param>
    /// <returns></returns>
    public static VertexArrayObject CreateBox(bool createNormals)
    {
        VertexDeclaration vxDeclaration;
        float[] vertices;
        if (createNormals)
        {
            vertices = CreateBoxVerticesWithNormals();
            vxDeclaration = VertexDeclarations.VertexPositionNormalTexture;
        }
        else
        {
            vertices = CreateBoxVerticesWithoutNormals();
            vxDeclaration = VertexDeclarations.VertexPositionTexture;
        }

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
        var vao = new VertexArrayObject();
        vao.AddBuffer(new Buffer<float>(vertices, vxDeclaration));
        vao.AddIndexBuffer(new IndexBuffer(indices));
        return vao;
    }

    public static VertexArrayObject CreateSphere(int stacks, int slices)
    {
        var vertices = new List<float>();
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

                vertices.AddRange(new float[] { x, y, z, nx, ny, nz, s, t });
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
        var vao = new VertexArrayObject();
        vao.AddBuffer(new Buffer<float>(vertices.ToArray(), VertexDeclarations.VertexPositionNormalTexture));
        vao.AddIndexBuffer(new IndexBuffer(indices.ToArray()));
        return vao;
    }

    public static float[] CreateBoxVerticesWithNormals()
    {
        float[] vertices =
        {
            // Position           Normal        Texture
            //  FRONT SIDE (z = +)
            -HALF, -HALF,  HALF,   0,  0,  1,   0, 1,   // lower left - 0
            -HALF,  HALF,  HALF,   0,  0,  1,   0, 0,   // upper left - 1
             HALF, -HALF,  HALF,   0,  0,  1,   1, 1,   // lower right - 2
             HALF,  HALF,  HALF,   0,  0,  1,   1, 0,   // upper right - 3
                                           
            //  BACK SIDE (z = -)
            -HALF, -HALF, -HALF,   0,  0, -1,   0, 1,   // lower left - 4
            -HALF,  HALF, -HALF,   0,  0, -1,   0, 0,   // upper left - 5
             HALF, -HALF, -HALF,   0,  0, -1,   1, 1,   // lower right - 6
             HALF,  HALF, -HALF,   0,  0, -1,   1, 0,   // upper right - 7

            //  LEFT SIDE (X = -)
            -HALF, -HALF, -HALF,  -1,  0,  0,   0, 1,   // lower left  - 8
            -HALF,  HALF, -HALF,  -1,  0,  0,   0, 0,   // upper left - 9
            -HALF, -HALF,  HALF,  -1,  0,  0,   1, 1,   // lower right - 10
            -HALF,  HALF,  HALF,  -1,  0,  0,   1, 0,   // upper right - 11

            //  RIGHT SIDE (X = +)
             HALF, -HALF,  HALF,   1,  0,  0,   0, 1,   // lower left  - 12
             HALF,  HALF,  HALF,   1,  0,  0,   0, 0,   // upper left - 13
             HALF, -HALF, -HALF,   1,  0,  0,   1, 1,   // lower right - 14
             HALF,  HALF, -HALF,   1,  0,  0,   1, 0,   // upper right - 15            
            
            //  UPPER SIDE (Y = +)
            -HALF,  HALF,  HALF,   0,  1,  0,   0, 1,   // lower left - 16
            -HALF,  HALF, -HALF,   0,  1,  0,   0, 0,   // upper left - 17
             HALF,  HALF,  HALF,   0,  1,  0,   1, 1,   // lower right - 18
             HALF,  HALF, -HALF,   0,  1,  0,   1, 0,   // upper right - 19

            //  lower SIDE (Y = -)
            -HALF, -HALF, -HALF,   0, -1,  0,   0, 1,   // lower left - 20
            -HALF, -HALF,  HALF,   0, -1,  0,   0, 0,   // upper left - 21
             HALF, -HALF, -HALF,   0, -1,  0,   1, 1,   // lower right - 22
             HALF, -HALF,  HALF,   0, -1,  0,   1, 0,   // upper right - 23             
        };
        return vertices;
    }

    public static float[] CreateBoxVerticesWithoutNormals()
    {
        float[] vertices =
        {
            // Position            Texture
            //  FRONT SIDE (z = +)
            -HALF, -HALF,  HALF,   0, 1,   // lower left - 0
            -HALF,  HALF,  HALF,   0, 0,   // upper left - 1
             HALF, -HALF,  HALF,   1, 1,   // lower right - 2
             HALF,  HALF,  HALF,   1, 0,   // upper right - 3
                                  
            //  BACK SIDE (z = -)
            -HALF, -HALF, -HALF,   1, 1,   // lower left - 4
            -HALF,  HALF, -HALF,   1, 0,   // upper left - 5
             HALF, -HALF, -HALF,   0, 1,   // lower right - 6
             HALF,  HALF, -HALF,   0, 0,   // upper right - 7

            //  LEFT SIDE (X = -)
            -HALF, -HALF, -HALF,   0, 1,   // lower left  - 8
            -HALF,  HALF, -HALF,   0, 0,   // upper left - 9
            -HALF, -HALF,  HALF,   1, 1,   // lower right - 10
            -HALF,  HALF,  HALF,   1, 0,   // upper right - 11

            //  RIGHT SIDE (X = +)
             HALF, -HALF, -HALF,   0, 1,   // lower left  - 12
             HALF,  HALF, -HALF,   0, 0,   // upper left - 13
             HALF, -HALF,  HALF,   1, 1,   // lower right - 14
             HALF,  HALF,  HALF,   1, 0,   // upper right - 15

            //  UPPER SIDE (Y = +)
            -HALF,  HALF, -HALF,   0, 1,   // lower left  - 16
            -HALF,  HALF,  HALF,   0, 0,   // upper left - 17
             HALF,  HALF, -HALF,   1, 1,   // lower right - 18
             HALF,  HALF,  HALF,   1, 0,   // upper right - 19

            //  lower SIDE (Y = -)
             HALF, -HALF,  HALF,   0, 1,   // lower left  - 16
             HALF, -HALF, -HALF,   0, 0,   // upper left - 17
            -HALF, -HALF,  HALF,   1, 1,   // lower right - 18
            -HALF, -HALF, -HALF,   1, 0,   // upper right - 19
        };
        return vertices;
    }

    private static float[] CreateCubeWithNormals()
    {
        float[] vertices =
        {
            // Position           Normal        Texture

            //  FRONT SIDE VERTICES
            -HALF, -HALF,  HALF,  -1, -1, 1,    0, 1,   // lower left - 0
            -HALF,  HALF,  HALF,  -1,  1, 1,    0, 0,   // upper left - 1
             HALF, -HALF,  HALF,   1, -1, 1,    1, 1,   // lower right - 2
             HALF,  HALF,  HALF,   1,  1, 1,    1, 0,   // upper right - 3
            
            //  BACK SIDE VERTICES
            -HALF, -HALF, -HALF,  -1, -1, -1,   1, 1,   // lower left
            -HALF,  HALF, -HALF,  -1,  1, -1,   1, 0,   // upper left
             HALF, -HALF, -HALF,   1, -1, -1,   0, 1,   // lower right
             HALF,  HALF, -HALF,   1,  1, -1,   0, 0,   // upper right

            -HALF,  HALF, -HALF,  -1,  1, -1,   0, 1,   // upper left 2nd
             HALF,  HALF, -HALF,   1,  1, -1,   1, 1,   // upper right 2nd
            -HALF, -HALF, -HALF,  -1, -1, -1,   0, 0,   // lower left 2nd
             HALF, -HALF, -HALF,   1, -1, -1,   1, 0,   // lower right 2nd            
        };
        return vertices;
    }

    private static float[] CreateCubeWithoutNormals()
    {
        float[] vertices =
        {
            // Position           Texture

            //  FRONT SIDE VERTICES
            -HALF, -HALF,  HALF,   0, 1,   // lower left - 0
            -HALF,  HALF,  HALF,   0, 0,   // upper left - 1
             HALF, -HALF,  HALF,   1, 1,   // lower right - 2
             HALF,  HALF,  HALF,   1, 0,   // upper right - 3
            
            //  BACK SIDE VERTICES
            -HALF, -HALF, -HALF,   1, 1,   // lower left
            -HALF,  HALF, -HALF,   1, 0,   // upper left
             HALF, -HALF, -HALF,   0, 1,   // lower right
             HALF,  HALF, -HALF,   0, 0,   // upper right

            -HALF,  HALF, -HALF,   0, 1,   // upper left 2nd
             HALF,  HALF, -HALF,   1, 1,   // upper right 2nd
            -HALF, -HALF, -HALF,   0, 0,   // lower left 2nd
             HALF, -HALF, -HALF,   1, 0,   // lower right 2nd  
        };
        return vertices;
    }

    private static float[] CreateQuadWithoutNormals()
    {
        //  create the 4 vertices with: 3 floats for position + 2 floats for uv
        var vertices = new float[]
        {
            -HALF, -HALF, 0,    0, 1,   // lower left corner
            -HALF,  HALF, 0,    0, 0,   // upper left corner
             HALF, -HALF, 0,    1, 1,   // lower right corner
             HALF,  HALF, 0,    1, 0,   // upper right corner
        };
        return vertices;
    }

    private static float[] Create2dQuadWithoutNormals()
    {
        //  create the 4 vertices with: 2 floats for position + 2 floats for uv
        //  note: the coordinates go from 0 to 1 because we want the sprite to align with screen coordinates.
        var vertices = new float[]
        {
             0, 0,  0, 1,   // lower left corner
             0, 1,  0, 0,   // upper left corner
             1, 0,  1, 1,   // lower right corner
             1, 1,  1, 0,   // upper right corner
        };
        return vertices;
    }

    private static float[] CreateQuadWithNormals()
    {
        //  create the 4 vertices with: 3 floats for position + 3 floats for normal + 2 floats for uv 
        var vertices = new float[]
        {
            -HALF, -HALF, 0,    -HALF, -HALF, 1,     0, 1,   // lower left corner
            -HALF,  HALF, 0,    -HALF, -HALF, 1,     0, 0,   // upper left corner
             HALF, -HALF, 0,     HALF, -HALF, 1,     1, 1,   // lower right corner
             HALF,  HALF, 0,     HALF,  HALF, 1,     1, 0,   // upper right corner
        };
        return vertices;
    }
}
