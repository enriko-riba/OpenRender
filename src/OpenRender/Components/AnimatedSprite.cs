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
    }

    public int Fps { get; set; } = 4;

    public bool IsLooping { get; set; }

    public bool IsPlaying => !string.IsNullOrEmpty(currentSequence);

    public override void OnUpdate(Scene scene, double elapsedSeconds)
    {
        base.OnUpdate(scene, elapsedSeconds);

        if (IsPlaying)
        {
            accumulator += (float)elapsedSeconds;
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
                if (currentFrame.UV is null)
                {
                    //  TODO: convert frame to UV and pass to shader
                }
                //  TODO: pass UV to shader
            }
            base.OnDraw(scene, elapsed);
        }
    }

    public void AddAnimation(string animationName, Frame[] frames)
    {
        animationSequences.Add(animationName, frames);
        // TODO: create vertex buffer with quads and UVs for each frame
    }

    public void Stop()
    {
        currentSequence = null;
        accumulator = 0;
        frameIndex = -1;
    }

    public void Play(string animationName, int? fps, bool loop = true)
    {
        ArgumentNullException.ThrowIfNull(animationName, nameof(animationName));

        if (!string.IsNullOrWhiteSpace(currentSequence) || currentSequence != animationName)
        {
            Stop();
        }

        if (animationSequences.ContainsKey(animationName))
        {
            currentSequence = animationName;
            currentFrames = animationSequences[currentSequence];
            Fps = fps ?? Fps;
            IsLooping = loop;
        }
        else
        {
            Log.Warn("animation sequence: '{0}' not found", animationName);
        }
    }

    public class Frame
    {
        public int X { get; set; }
        public int Y { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public Vector2i? UV { get; set; }
    }
}
