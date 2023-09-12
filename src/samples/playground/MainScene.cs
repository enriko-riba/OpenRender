﻿using OpenRender.Components;
using OpenRender.Core;
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

internal class MainScene : Scene
{
    private readonly Vector3 textColor1 = new(1, 1, 1);
    private readonly Vector3 textColor2 = new(0.8f, 0.8f, 0.65f);
    private Sprite? smiley;
    private AnimatedSprite? animatedSprite;
    private bool isMouseMoving;
    private TextRenderer tr = default!;
      

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
        var skyBox = new SkyBox(paths);
        AddNode(skyBox);

        var dirLight = new LightUniform()
        {
            Direction = new Vector3(-0.95f, -0.995f, 0.75f),
            Ambient = new Vector3(0.065f, 0.06f, 0.065f),
            Diffuse = new Vector3(0.8f),
            Specular = new Vector3(1),
        };
        AddLight(dirLight);

        camera = new Camera3D(Vector3.Zero, SceneManager.Size.X / (float)SceneManager.Size.Y, farPlane: 2000);

        var fontAtlas = FontAtlasGenerator.Create("Resources/consola.ttf", 18, new Color4(0f, 0f, 0f, 0.5f));
        tr = new TextRenderer(TextRenderer.CreateTextRenderingProjection(SceneManager.ClientSize.X, SceneManager.ClientSize.Y), fontAtlas);
    }

    public override void OnResize(ResizeEventArgs e)
    {
        base.OnResize(e);
        tr.Projection = TextRenderer.CreateTextRenderingProjection(SceneManager.ClientSize.X, SceneManager.ClientSize.Y);
    }

    const int Padding = 51;
    readonly string helpText1 = $"WASD: move, L shift: down, space: up".PadRight(Padding);
    readonly string helpText2 = $"mouse: rotate, scroll: zoom, Q: roll L, E: roll R".PadRight(Padding);
    readonly string helpText3 = $"F1: bounding sphere (wire), F11: toggle full screen".PadRight(Padding);

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
        camera!.Fov -= e.OffsetY * 5;
    }

    private void HandleMovement(double elapsedTime)
    {
        const float MovementSpeed = 50;
        var movementPerSecond = (float)elapsedTime * MovementSpeed;

        var input = SceneManager.KeyboardState;
        if (input.IsKeyDown(Keys.W))
        {
            camera!.Position += camera.Front * movementPerSecond; // Forward
        }

        if (input.IsKeyDown(Keys.S))
        {
            camera!.Position -= camera.Front * movementPerSecond; // Backwards
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
        const float RotationSpeed = 45;

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
        var vbBox = GeometryHelper.CreateBox(true);
        var vbQuad = GeometryHelper.CreateQuad(true);
        var vbCube = GeometryHelper.CreateCube(true);

        var mat1 = Material.Create(
            new TextureDescriptor[] {
                new TextureDescriptor ("Resources/container.png", TextureType: TextureType.Diffuse),
                new TextureDescriptor("Resources/awesomeface.png", TextureType: TextureType.Detail)
            },
            detailTextureFactor: 10f,
            shininess: 0.15f
        );
        var mat2 = Material.Create(
            new TextureDescriptor[] {
                new TextureDescriptor ("Resources/awesomeface.png", TextureType: TextureType.Diffuse),
                new TextureDescriptor("Resources/container.png", TextureType: TextureType.Detail)
            },
            detailTextureFactor: 3f,
            shininess: 0.25f
        );

        var n1 = new SceneNode(new Mesh(vbBox), mat1, new Vector3(3, 0, -3))
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

        var n2 = new SceneNode(new Mesh(vbCube), mat2, new Vector3(1.75f, 0.2f, 0))
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

        var n3 = new SceneNode(new Mesh(vbQuad), mat1, new Vector3(0.75f, 0.25f, 0))
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
        var vbBox = GeometryHelper.CreateBox(true);
        var vbSphere = GeometryHelper.CreateSphere(32, 48);
        var matSphere = Material.Create(
            new TextureDescriptor("Resources/ball13.jpg", TextureType: TextureType.Diffuse),
            detailTextureFactor: 0f,
            shininess: 0.45f
        );

        for (var i = 0; i < NodeCount; i++)
        {
            if (i % 5 == 0)
            {
                var sphere = new RandomNode(new Mesh(vbSphere), matSphere);
                AddNode(sphere);
            }
            else
            {
                var texture = (i % 7) switch
                {
                    0 => new TextureDescriptor[] { new TextureDescriptor("Resources/awesomeface.png", TextureType: TextureType.Diffuse) },
                    1 => new TextureDescriptor[] { new TextureDescriptor("Resources/container.png", TextureType: TextureType.Diffuse) },
                    2 => new TextureDescriptor[] { new TextureDescriptor("Resources/metallic.png", TextureType: TextureType.Diffuse) },
                    3 => new TextureDescriptor[] { new TextureDescriptor("Resources/ball13.jpg", TextureType: TextureType.Diffuse) },
                    4 => new TextureDescriptor[] { new TextureDescriptor("Resources/xneg.png", TextureType: TextureType.Diffuse) },
                    5 => new TextureDescriptor[] { new TextureDescriptor("Resources/xpos.png", TextureType: TextureType.Diffuse) },
                    6 => new TextureDescriptor[] { new TextureDescriptor("Resources/yneg.png", TextureType: TextureType.Diffuse) },
                    7 => new TextureDescriptor[] { new TextureDescriptor("Resources/ypos.jpg", TextureType: TextureType.Diffuse) },
                    _ => null
                };
                var mat1 = Material.Create(
                    texture,
                    new Vector3((float)Random.Shared.NextDouble(), (float)Random.Shared.NextDouble(), (float)Random.Shared.NextDouble()),
                    Vector3.One,
                    (float)Random.Shared.NextDouble() * 0.7f);
                var cube = new RandomNode(new Mesh(vbBox), mat1);
                AddNode(cube);
            }
        }
    }

    private void AddMetallicBoxes()
    {
        var vbBox = GeometryHelper.CreateBox(true);
        var mat = Material.Create(new TextureDescriptor[] { new TextureDescriptor("Resources/metallic.png", TextureType: TextureType.Diffuse) },
            new Vector3(0.25f, 0.25f, 0.35f),
            new Vector3(0.055f, 0.055f, 0.055f),
            1.97f);
        mat.EmissiveColor = new(0.05f, 0.01f, 0.012f);

        for (var i = 0; i < 50; i++)
        {
            var cube = new SceneNode(new Mesh(vbBox), mat);
            cube.SetPosition(new(-250 + i * 10, 0, -10));
            AddNode(cube);
        }
    }

    private void AddSprites()
    {
        smiley = new Sprite("Resources/awesomeface-sprite.png");
        smiley.SetPosition(new(950, 200));
        smiley.Size = new(70, 70); //  scale and size are interchangeable
        AddNode(smiley);

        var child = new Sprite("Resources/awesomeface-sprite.png");
        child.SetPosition(new(100, 80));
        child.SetScale(new Vector3(0.5f));  //  scale and size are interchangeable
        child.AngleRotation = -45;
        smiley.AddChild(child);

        animatedSprite = new AnimatedSprite("Resources/test-sprite-sheet.png")
        {
            Size = new Vector2i(60, 65)
        };
        AddNode(animatedSprite);
        animatedSprite.SetPosition(new(770, 210));
        animatedSprite.AddAnimation("left", new Rectangle[] {
            new (0, 0, 50, 50),
            new (50, 0, 50, 50),
            new (100, 0, 50, 50)
        });
        animatedSprite.AddAnimation("right", new Rectangle[] {
            new (0, 50, 50, 50),
            new (50, 50, 50, 50),
            new (100, 50, 50, 50)
        });
        animatedSprite.Play("left", 3);
    }
}