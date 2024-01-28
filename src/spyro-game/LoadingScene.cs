using OpenRender;
using OpenRender.Components;
using OpenRender.Core;
using OpenRender.Core.Rendering;
using OpenRender.Core.Rendering.Text;
using OpenRender.SceneManagement;
using OpenTK.Mathematics;
using SpyroGame.World;
using System.Diagnostics;

namespace SpyroGame;

internal class LoadingScene : Scene
{
    private readonly BigButton btnStart;
    private readonly ITextRenderer textRenderer;
    private readonly VoxelWorld world;

    private Vector3 textColor = Vector3.One;
    private Vector3 doneColor = new (0.3f, 0.8f, 0.3f);

    private double totalTime = 0;
    private double totalInitializationTime = 0;
    private bool hasCompleteChunksStarted;
    private int lineY;
    private List<Chunk> completedChunks = [];
    private Task worldStartTask = default!;

    public LoadingScene(ITextRenderer textRenderer)
    {
        this.textRenderer = textRenderer;
        btnStart = new BigButton("Start")
        {
            TextRenderer = textRenderer,
            IsEnabled = false
        };

        Log.Highlight($"initializing voxel world...");
        world = new VoxelWorld(1338);        
    }

    public override void Load()
    {
        base.Load();
        BackgroundColor = Color4.DarkSlateGray;
        camera = new Camera2D(Vector3.Zero, Width, Height);

        var mainScene = (MainScene)SceneManager.GetScene("MainScene");
        mainScene.World = world;
        btnStart.SetPosition(new Vector2(150, 410));
        btnStart.OnClick = () => SceneManager.ActivateScene(mainScene);
        AddNode(btnStart);

        var startPosition = new Vector3(VoxelHelper.ChunkSideSize * VoxelHelper.WorldChunksXZ / 2f, 0, VoxelHelper.ChunkSideSize * VoxelHelper.WorldChunksXZ / 2f);
        worldStartTask = Task.Run(() => world.AddStartingChunks(startPosition));
    }
    
    public override void RenderFrame(double elapsedSeconds)
    {
        base.RenderFrame(elapsedSeconds);
        lineY = 150;

        //  prepare for next state
        if (worldStartTask.IsCompleted && !hasCompleteChunksStarted)
        {
            totalInitializationTime = totalTime;
            hasCompleteChunksStarted = true;
            var surroundingChunks = world.SurroundingChunks;
            var unprocessedChunks = surroundingChunks.Where(x => !x.IsProcessed).Count();
            Debug.Assert(unprocessedChunks == 0, "unprocessed chunk");
            completedChunks = [.. surroundingChunks];
        }

        //  we have 3 states:
        //  1. world is still initializing
        //  2. world is initialized, and chunks are added to ChunkRenderer
        //  3. complete
        const int StateInitializing = 0;
        const int StateAddingChunks = 1;
        const int StateCompleted = 2;

        var state = !worldStartTask.IsCompleted ? StateInitializing : (hasCompleteChunksStarted && completedChunks.Count > 0) ? StateAddingChunks : StateCompleted;
        switch (state)
        {
            case StateInitializing:
                totalTime += elapsedSeconds;
                RenderUiInitializing();
                break;

            case StateAddingChunks:
                totalTime += elapsedSeconds;
                RenderUiAddingChunks();
                var counter = 0;
                while(counter++ < 10 && completedChunks.Count > 0)
                {
                    var chunk = completedChunks[0];
                    chunk.State = ChunkState.Loaded;
                    Debug.Assert(chunk.IsProcessed, "not initialized!");
                    completedChunks.Remove(chunk);
                    world.ChunkRenderer.AddChunkDirect(chunk);
                }
                break;

            case StateCompleted:
                RenderUiCompleted();
                btnStart.IsEnabled = true;
                break;
        }       
    }

    private void RenderUiCompleted()
    {
        textColor = Vector3.One;
        var text = "World loaded!";
        WriteLine(text, textColor);
        text = $"elapsed time: {totalTime:N2} seconds";
        WriteLine(text, Vector3.UnitY);
        WriteLine("", textColor);

        text = $"initialized {world.TotalStartingChunks} chunks in {totalInitializationTime:N2} seconds";
        WriteLine(text, doneColor);

        text = $"added {world.TotalStartingChunks} chunks in {totalTime- totalInitializationTime:N2} seconds";
        WriteLine(text, doneColor);
    }

    private void RenderUiAddingChunks()
    {
        var text = $"LOADING WORLD...";
        WriteLine(text, textColor);

        text = $"elapsed time: {totalTime:N2} seconds";
        WriteLine(text, textColor);        
        WriteLine("", textColor);

        text = $"initialized {world.TotalStartingChunks} chunks in {totalInitializationTime:N2} seconds";
        WriteLine(text, doneColor);

        text = $"adding completed chunks {world.TotalStartingChunks - completedChunks.Count} / {world.TotalStartingChunks}";
        WriteLine(text, textColor);
    }

    private void RenderUiInitializing()
    {
        var text = $"LOADING WORLD...";
        WriteLine(text, textColor);

        text = $"elapsed time: {totalTime:N2} seconds";
        WriteLine(text, textColor);
        WriteLine("", textColor);

        text = $"initializing chunk {world.ProcessedStartingChunks} / {world.TotalStartingChunks}";
        WriteLine(text, textColor);
    }

    private void WriteLine(string text, in Vector3 color, int size = 22, float lineStart = 150)
    {
        textRenderer.Render(text, size, lineStart, lineY, color);
        lineY += 45;
    }
}

public class BigButton : Button
{
    internal const int BtnWidth = 200;
    internal const int BtnHeight = 60;
    private static (Mesh mesh, Material material) mm = CreateMeshAndMaterial("Resources/btnAtlas.png", "Shaders/sprite.vert", "Shaders/nine-slice.frag");

    public BigButton(string caption) : base(mm.mesh, mm.material, caption, 30, BtnWidth, BtnHeight)
    {
        SourceRectangle = new Rectangle(0, 0, 200, 60);
        Update = (node, elapsed) =>
        {
            var btn = (node as Button)!;
            if (!IsEnabled)
            {
                btn.CaptionColor = Color4.DarkGray;
                return;
            }

            var rect = btn.SourceRectangle;
            rect.Y = btn.IsPressed ? 120 :
                     btn.IsHovering ? 60 : 0;
            btn.SourceRectangle = rect;
            btn.CaptionColor = btn.IsPressed ? Color4.YellowGreen : Color4.DarkKhaki;
        };
    }
}