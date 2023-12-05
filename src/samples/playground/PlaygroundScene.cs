using OpenRender.Components;
using OpenRender.Core;
using OpenRender.Core.Buffers;
using OpenRender.Core.Geometry;
using OpenRender.Core.Rendering;
using OpenRender.Core.Textures;
using OpenRender.SceneManagement;
using OpenRender.Text;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace Samples.Playground;

internal class PlaygroundScene : Scene
{
    private readonly Vector3 textColor1 = new(1, 1, 1);
    private readonly Vector3 textColor2 = new(0.8f, 0.8f, 0.65f);
    private Sprite? smiley;
    private AnimatedSprite? animatedSprite;
    private bool isMouseMoving;
    private TextRenderer tr = default!;
    private (Vertex[] vertices, uint[] indices) boxData;
    private (Vertex[] vertices, uint[] indices) sphereData;

    public PlaygroundScene()
    {
        boxData = GeometryHelper.CreateBox();
        sphereData = GeometryHelper.CreateSphere(24, 48);
    }

    public override void Load()
    {
        base.Load();
        GL.ClearColor(Color4.DarkSlateBlue);

        AddRotatingBoxes();
        AddRandomNodes();
        AddMetallicBoxes();
        AddSprites();

        var paths = new string[] {
            "Resources/xpos.png",
            "Resources/xneg.png",
            "Resources/ypos.png",
            "Resources/yneg.png",
            "Resources/zpos.png",
            "Resources/zneg.png",
        };
        var skyBox = SkyBox.Create(paths);
        AddNode(skyBox);

        var dirLight = new LightUniform()
        {
            Direction = new Vector3(-0.95f, -0.995f, 0.85f),
            Ambient = new Vector3(0.15f, 0.10f, 0.15f),
            Diffuse = new Vector3(0.8f),
            Specular = new Vector3(1),
        };
        AddLight(dirLight);

        camera = new Camera3D(Vector3.Zero, SceneManager.Size.X / (float)SceneManager.Size.Y, farPlane: 2000);

        var fontAtlas = FontAtlasGenerator.Create("Resources/consola.ttf", 18, new Color4(0f, 0f, 0f, 0.5f));
        tr = new TextRenderer(TextRenderer.CreateTextRenderingProjection(Width, Height), fontAtlas);
    }

    public override void OnResize(ResizeEventArgs e)
    {
        base.OnResize(e);
        tr.Projection = TextRenderer.CreateTextRenderingProjection(Width, Height);
    }

    private const int Padding = 51;
    private readonly string helpText1 = $"WASD: move, L shift: down, space: up".PadRight(Padding);
    private readonly string helpText2 = $"mouse: rotate, scroll: zoom, Q: roll L, E: roll R".PadRight(Padding);
    private readonly string helpText3 = $"F1: bounding sphere (wire), F11: toggle full screen".PadRight(Padding);

    public override void RenderFrame(double elapsedSeconds)
    {
        base.RenderFrame(elapsedSeconds);
        const int LineHeight = 18;
        const int TextStartY = 10;

        var nodesText = $"nodes: {VisibleNodes}/{nodes.Count}, pos: {camera!.Position:N2}".PadRight(Padding);
        tr.Render(nodesText, 5, TextStartY, textColor1);
        var fpsText = $"avg frame duration: {SceneManager.AvgFrameDuration:G3} ms, fps: {SceneManager.Fps:N0}".PadRight(Padding);
        tr.Render(fpsText, 5, TextStartY + LineHeight * 1, textColor1);
        tr.Render(helpText1, 5, TextStartY + LineHeight * 2, textColor2);
        tr.Render(helpText2, 5, TextStartY + LineHeight * 3, textColor2);
        tr.Render(helpText3, 5, TextStartY + LineHeight * 4, textColor2);
    }

    public override void UpdateFrame(double elapsedSeconds)
    {
        if (!SceneManager.IsFocused)
        {
            return;
        }

        base.UpdateFrame(elapsedSeconds);

        HandleRotation(elapsedSeconds);
        HandleMovement(elapsedSeconds);

        if (SceneManager.KeyboardState.IsKeyDown(Keys.Escape))
        {
            SceneManager.Close();
        }

        if (SceneManager.KeyboardState.IsKeyPressed(Keys.F1))
        {
            ShowBoundingSphere = !ShowBoundingSphere;
        }

        if (SceneManager.KeyboardState.IsKeyPressed(Keys.F11))
        {
            SceneManager.WindowState = SceneManager.WindowState == WindowState.Fullscreen ?
                    WindowState.Normal : WindowState.Fullscreen;
        }
    }

    public override void OnMouseWheel(MouseWheelEventArgs e)
    {
        const float Sensitivity = 3.5f;
        camera!.Fov -= e.OffsetY * Sensitivity;
    }

    private void HandleMovement(double elapsedTime)
    {
        const float MovementSpeed = 30;
        var movementPerSecond = (float)elapsedTime * MovementSpeed;

        var input = SceneManager.KeyboardState;
        if (input.IsKeyDown(Keys.W))
        {
            camera!.MoveForward(movementPerSecond);
        }
        if (input.IsKeyDown(Keys.S))
        {
            camera!.MoveForward(-movementPerSecond);
        }
        if (input.IsKeyDown(Keys.A))
        {
            camera!.Position -= camera.Right * movementPerSecond; // Left
            animatedSprite?.Play("left", 3);
        }
        if (input.IsKeyDown(Keys.D))
        {
            camera!.Position += camera.Right * movementPerSecond; // Right
            animatedSprite?.Play("right", 3);
        }
        if (input.IsKeyDown(Keys.Space))
        {
            camera!.Position += camera.Up * movementPerSecond; // Up
        }
        if (input.IsKeyDown(Keys.LeftShift))
        {
            camera!.Position -= camera.Up * movementPerSecond; // Down
        }
    }

    private void HandleRotation(double elapsedTime)
    {
        const float RotationSpeed = 20;

        var mouseState = SceneManager.MouseState;
        var rotationPerSecond = (float)(elapsedTime * RotationSpeed);
        smiley!.AngleRotation += rotationPerSecond;
        if (SceneManager.KeyboardState.IsKeyDown(Keys.Q))
        {
            camera!.AddRotation(0, 0, rotationPerSecond);
        }
        if (SceneManager.KeyboardState.IsKeyDown(Keys.E))
        {
            camera!.AddRotation(0, 0, -rotationPerSecond);
        }
        if (isMouseMoving && mouseState.IsButtonDown(MouseButton.Left))
        {
            if (SceneManager.MouseState.Delta.LengthSquared > 0)
            {
                camera!.AddRotation(mouseState.Delta.X * rotationPerSecond, mouseState.Delta.Y * rotationPerSecond, 0);
            }
        }
        else
        {
            isMouseMoving = false;
        }

        if (!isMouseMoving && mouseState.IsButtonPressed(MouseButton.Left))
        {
            isMouseMoving = true;
        }
    }

    private void AddRotatingBoxes()
    {
        var (vertices, indices) = GeometryHelper.CreateQuad();

        var mat1 = Material.Create(
            defaultShader,
            [
                new ("Resources/container.png", TextureType: TextureType.Diffuse),
                new ("Resources/awesomeface.png", TextureType: TextureType.Detail)
            ],
            detailTextureScaleFactor: 10f,
            shininess: 0.15f
        );
        var mat2 = Material.Create(
            defaultShader,
            [
                new("Resources/awesomeface.png", TextureType: TextureType.Diffuse),
                new("Resources/container.png", TextureType: TextureType.Detail)
            ],
            detailTextureScaleFactor: 3f,
            shininess: 0.25f
        );

        var n1 = new SceneNode(new Mesh(VertexDeclarations.VertexPositionNormalTexture, boxData.vertices, boxData.indices), mat1, new Vector3(3, 0, -3))
        {
            Update = (n, e) =>
            {
                var rot = n.AngleRotation;
                rot.Z += (float)e / 8.0f;
                rot.X += (float)e / 4.0f;
                n.SetRotation(rot);
            }
        };
        AddNode(n1);

        var n2 = new SceneNode(new Mesh(VertexDeclarations.VertexPositionNormalTexture, boxData.vertices, boxData.indices), mat2, new Vector3(1.75f, 0.2f, 0))
        {
            Update = (n, e) =>
            {
                var rot = n.AngleRotation;
                rot.Z += (float)e / 4;
                n.SetRotation(rot);
            }
        };
        n2.SetScale(new Vector3(0.5f));
        n1.AddChild(n2);

        var n3 = new SceneNode(new Mesh(VertexDeclarations.VertexPositionNormalTexture, vertices, indices), mat1, new Vector3(0.75f, 0.25f, 0))
        {
            Update = (n, e) =>
            {
                var rot = n.AngleRotation;
                rot.Y += (float)e;
                n.SetRotation(rot);
            }
        };
        n2.AddChild(n3);
    }

    private void AddRandomNodes()
    {
        const int NodeCount = 5000;
        var materials = new Material[]
        {
            Material.Create(defaultShader, [
                new("Resources/ball13.jpg"),
                new("Resources/container.png", TextureType: TextureType.Detail)
            ], shininess: 2.0f, detailTextureScaleFactor: 10f, detailTextureBlendFactor: 0.45f),
            Material.Create(defaultShader, new TextureDescriptor("Resources/metallic.png"), shininess: 1.85f),
            Material.Create(defaultShader, new TextureDescriptor("Resources/awesomeface.png"), shininess: 0.60f),
            Material.Create(defaultShader, new TextureDescriptor("Resources/xneg.png"), shininess: 0.15f),
            Material.Create(defaultShader, new TextureDescriptor("Resources/xpos.png"), shininess: 0.15f),
            Material.Create(defaultShader, new TextureDescriptor("Resources/yneg.png"), shininess: 0.15f),
            Material.Create(defaultShader, new TextureDescriptor("Resources/ypos.png"), shininess: 0.15f),
            Material.Create(defaultShader, new TextureDescriptor("Resources/container.png"), shininess: 0.10f)
        };

        for (var i = 0; i < NodeCount; i++)
        {
            if (i % 5 == 0)
            {
                var sphere = new RandomNode(new Mesh(VertexDeclarations.VertexPositionNormalTexture, sphereData.vertices, sphereData.indices), materials[0]);
                AddNode(sphere);
            }
            else
            {
                var cube = new RandomNode(new Mesh(VertexDeclarations.VertexPositionNormalTexture, boxData.vertices, boxData.indices), materials[i % 7]);
                AddNode(cube);
            }
        }
    }

    private void AddMetallicBoxes()
    {
        var mat = Material.Create(defaultShader,
            [new ("Resources/metallic.png", TextureType: TextureType.Diffuse)],
            0.70f);
        mat.EmissiveColor = new(0.05f, 0.07f, 0.005f);

        for (var i = 0; i < 50; i++)
        {
            var cube = new SceneNode(new Mesh(VertexDeclarations.VertexPositionNormalTexture, boxData.vertices, boxData.indices), mat);
            cube.SetPosition(new(-250 + i * 10, 0, -10));
            AddNode(cube);
        }
    }

    private void AddSprites()
    {
        smiley = Sprite.Create("Resources/awesomeface-sprite.png");
        smiley.SetPosition(new(950, 200));
        smiley.Size = new(70, 70); //  scale and size are interchangeable
        AddNode(smiley);

        var child = Sprite.Create("Resources/awesomeface-sprite.png");
        child.SetPosition(new(100, 80));
        child.SetScale(new Vector3(0.5f));  //  scale and size are interchangeable
        child.AngleRotation = -45;
        smiley.AddChild(child);

        animatedSprite = AnimatedSprite.Create("Resources/test-sprite-sheet.png");
        AddNode(animatedSprite);
        animatedSprite.Size = new(60, 65);
        animatedSprite.SetPosition(new(770, 210));
        animatedSprite.AddAnimation("left", [
            new (0, 0, 50, 50),
            new (50, 0, 50, 50),
            new (100, 0, 50, 50)
        ]);
        animatedSprite.AddAnimation("right", [
            new (0, 50, 50, 50),
            new (50, 50, 50, 50),
            new (100, 50, 50, 50)
        ]);
        animatedSprite.Play("left", 3);
    }
}
