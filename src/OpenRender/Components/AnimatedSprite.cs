using OpenRender.Core.Rendering;
using OpenRender.SceneManagement;
using OpenTK.Mathematics;

namespace OpenRender.Components;


/// <summary>
/// 2D sprite with textures atlas containing multiple frames drawn in a sequence.
/// </summary>
public class AnimatedSprite : Sprite
{
    private float accumulator;
    private int frameIndex = -1;
    private int lastIndex = -1;
    private string? currentSequence;
    private readonly Action? onUpdateAction;
    private readonly Action? onCompleteAction;
    private readonly Dictionary<string, Frame[]> animationSequences = new();
    private Frame[]? currentFrames;
    private Frame? currentFrame;

    public AnimatedSprite(string textureName) : this(textureName, null, null) { }
    public AnimatedSprite(string textureName, Action? onComplete) : this(textureName, onComplete, null) { }
    public AnimatedSprite(string textureName, Action? onComplete, Action? onUpdate) : base(textureName)
    {
        onUpdateAction = onUpdate;
        onCompleteAction = onComplete;

        shader = new Shader("Shaders/sprite.vert", "Shaders/animated-sprite.frag");
        Material.Shader = shader;
        Tint = Color4.White;    //  need to re set the tint in order to setup the shaders uniform
        var projection = Matrix4.CreateOrthographicOffCenter(0, 800, 600, 0, -1, 1);
        shader.SetMatrix4("projection", ref projection); 
    }

    public int Fps { get; set; } = 4;

    public bool IsLooping { get; set; }

    public bool IsPlaying => !string.IsNullOrEmpty(currentSequence);

    public override void OnUpdate(Scene scene, double elapsed)
    {
        base.OnUpdate(scene, elapsed);

        if (IsPlaying)
        {
            accumulator += (float)elapsed;
            var secForFrame = 1f / Fps;
            if (accumulator >= secForFrame)
            {
                accumulator -= secForFrame;
                lastIndex = frameIndex;
                frameIndex++;
                onUpdateAction?.Invoke();
                if (frameIndex == currentFrames?.Length)
                {
                    frameIndex = 0;

                    //  end the animation if not looping
                    if (!IsLooping)
                    {
                        currentSequence = null;
                        lastIndex = -1;
                        onCompleteAction?.Invoke();
                    }
                }
            }
        }
    }

    public override void OnDraw(Scene scene, double elapsed)
    {
        if (!string.IsNullOrWhiteSpace(currentSequence) && currentFrames is not null)
        {
            if (lastIndex != frameIndex)
            {
                currentFrame = currentFrames[Math.Max(0, frameIndex)];
                var uvInfo = currentFrame.UV!.Value;
                shader.SetVector4("uvInfo", ref uvInfo);
                size.Width = currentFrame.Width;
                size.Height = currentFrame.Height;
                UpdateMatrix();
            }
        }
        base.OnDraw(scene, elapsed);
    }

    public void AddAnimation(string animationName, Frame[] frames)
    {
        animationSequences.Add(animationName, frames);
        CalcFrameUVs(frames);
    }

    public void Stop()
    {
        currentSequence = null;
        accumulator = 0;
        frameIndex = -1;
        lastIndex = -1;
    }

    public void Play(string animationName, int? fps, bool loop = true)
    {
        ArgumentNullException.ThrowIfNull(animationName, nameof(animationName));
        if(currentSequence == animationName)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(currentSequence))
        {
            Stop();
        }

        if (animationSequences.ContainsKey(animationName))
        {
            currentSequence = animationName;
            currentFrames = animationSequences[currentSequence];
            Fps = fps ?? Fps;
            IsLooping = loop;
            frameIndex = 0;
            lastIndex = -1;
        }
        else
        {
            Log.Warn("animation sequence: '{0}' not found", animationName);
        }
    }

    private void CalcFrameUVs(Frame[] frames)
    {
        foreach (var frame in frames)
        {
            var minX = frame.X / (float)size.Width;
            var minY = frame.Y / (float)size.Height;
            frame.UV = new Vector4(minX, minY, 
                (frame.X + frame.Width) / (float)size.Width - minX, 
                (frame.Y + frame.Height) / (float)size.Height - minY);
        }
    }

    public class Frame
    {
        public Frame(int x, int y, int width, int height)
        {
            X = x;
            Y = y;
            Width = width;
            Height = height;
        }
        public int X { get; set; }
        public int Y { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public Vector4? UV { get; set; }
    }
}
