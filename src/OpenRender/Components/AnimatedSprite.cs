﻿using OpenRender.Core;
using OpenRender.SceneManagement;

namespace OpenRender.Components;


/// <summary>
/// 2D sprite with textures atlas containing multiple frames drawn in a sequence.
/// </summary>
public class AnimatedSprite(Mesh mesh, Material material, Action? onComplete, Action? onUpdate) : Sprite(mesh, material)
{
    private float accumulator;
    private int frameIndex = -1;
    private int lastIndex = -1;
    private string? currentSequenceName;
    private readonly Action? onUpdateAction = onUpdate;
    private readonly Action? onCompleteAction = onComplete;
    private readonly Dictionary<string, Rectangle[]> animationSequences = [];
    private Rectangle[]? currentSequence;
    private Rectangle currentFrame;

    public static AnimatedSprite Create(string textureName, Action? onComplete = null, Action? onUpdate = null)
    {
        ArgumentNullException.ThrowIfNull(textureName);
        var (mesh, material) = CreateMeshAndMaterial(textureName);
        var sprite = new AnimatedSprite(mesh, material, onComplete, onUpdate);
        return sprite;
    }

    public int Fps { get; set; } = 4;

    public bool IsLooping { get; set; }

    public bool IsPlaying => !string.IsNullOrEmpty(currentSequenceName);

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
                if (frameIndex == currentSequence?.Length)
                {
                    frameIndex = 0;

                    //  end the animation if not looping
                    if (!IsLooping)
                    {
                        currentSequenceName = null;
                        lastIndex = -1;
                        onCompleteAction?.Invoke();
                    }
                }
            }
        }
    }

    public override void OnDraw(double elapsed)
    {
        if (!string.IsNullOrWhiteSpace(currentSequenceName) && currentSequence is not null)
        {
            if (lastIndex != frameIndex)
            {
                currentFrame = currentSequence[Math.Max(0, frameIndex)];
                SourceRectangle = new Rectangle(currentFrame.X, currentFrame.Y, currentFrame.Width, currentFrame.Height);
                UpdateMatrix();
            }
        }
        base.OnDraw(elapsed);
    }

    public void AddAnimation(string animationName, Rectangle[] frames) => animationSequences.Add(animationName, frames);

    public void Stop()
    {
        currentSequenceName = null;
        accumulator = 0;
        frameIndex = -1;
        lastIndex = -1;
    }

    public void Play(string animationName, int? fps, bool loop = true)
    {
        ArgumentNullException.ThrowIfNull(animationName, nameof(animationName));
        if (currentSequenceName == animationName)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(currentSequenceName))
        {
            Stop();
        }

        if (animationSequences.ContainsKey(animationName))
        {
            currentSequenceName = animationName;
            currentSequence = animationSequences[currentSequenceName];
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
}
