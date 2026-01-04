using OpenTK.Mathematics;

namespace DarkVox.Client.Content.Animations;

// Animation scaffolding: JSON-defined clips and controllers.
// Not yet integrated into rendering; this is the "data model" foundation.

internal sealed record AnimationFile(
    string? FormatVersion,
    IReadOnlyList<AnimationClip> Animations);

internal sealed record AnimationClip(
    string Name,
    float LengthSeconds,
    bool Loop,
    IReadOnlyDictionary<string, BoneAnimation> Bones);

internal sealed record BoneAnimation(
    IReadOnlyList<KeyframeVec3>? RotationDeg = null,
    IReadOnlyList<KeyframeVec3>? Position = null,
    IReadOnlyList<KeyframeVec3>? Scale = null,
    // Placeholder for future Molang-like expression support.
    // Example: "cos(time * 38.17) * 80".
    string? RotationExpression = null);

internal sealed record KeyframeVec3(float TimeSeconds, Vector3 Value);

internal sealed record AnimationControllerFile(
    string? FormatVersion,
    IReadOnlyList<AnimationController> Controllers);

internal sealed record AnimationController(
    string Name,
    string InitialState,
    IReadOnlyDictionary<string, AnimationControllerState> States);

internal sealed record AnimationControllerState(
    IReadOnlyList<string> PlayAnimations,
    IReadOnlyList<AnimationTransition> Transitions);

internal sealed record AnimationTransition(string TargetState, string ConditionExpression);
