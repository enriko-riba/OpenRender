﻿using OpenRender.Core.Rendering;
using OpenRender.SceneManagement;
using OpenRender.Text;
using OpenTK.Mathematics;

namespace Samples.Instancing;

internal class MainScene : Scene
{
    private TextRenderer tr = default!;

    public MainScene() : base(nameof(MainScene)) { }

    public override void Load()
    {
        camera = new Camera3D(Vector3.Zero, SceneManager.Size.X / (float)SceneManager.Size.Y, farPlane: 2000);

        var fontAtlas = FontAtlasGenerator.Create("Resources/consola.ttf", 18, new Color4(0f, 0f, 0f, 0.5f));
        tr = new TextRenderer(TextRenderer.CreateTextRenderingProjection(SceneManager.ClientSize.X, SceneManager.ClientSize.Y), fontAtlas);
    }
}