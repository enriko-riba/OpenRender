using OpenRender.Core.Rendering;
using OpenTK.Mathematics;

namespace DarkVox.Client.Rendering;

public sealed class CombatDamageTextHelper(
    RuntimeEventTextDisplay display,
    Func<Vector2> viewportSize,
    Func<ICamera?> camera)
{
    private static readonly Vector3 DealtColor = new(1f, 0f, 0f);   // Red
    private static readonly Vector3 TookColor = new(1f, 1f, 0f);    // Yellow

    private const float DealtUpPixels = 80f;
    private const float TookUpPixels = 60f;

    public void ShowPlayerDealtDamage(int damage, Vector3 worldPosition, int fontSize = 26, float durationSeconds = 0.7f)
    {
        if (damage <= 0) return;

        var cam = camera();
        if (cam == null) return;

        var viewport = viewportSize();
        if (viewport.X <= 0 || viewport.Y <= 0) return;

        if (!TryWorldToScreen(worldPosition, cam, viewport, out var screenPos))
        {
            // Fallback: show at crosshair if we can't project.
            screenPos = new Vector2(viewport.X / 2f, viewport.Y / 2f);
        }

        display.DisplayWithMotionAndScale(
            $"-{damage}HP",
            fontSize,
            DealtColor,
            startPosition: screenPos,
            endPosition: screenPos + new Vector2(0, -DealtUpPixels),
            startScale: 2.0f,
            endScale: 0.6f,
            durationSeconds);
    }

    public void ShowPlayerTookDamage(int damage, int fontSize = 28, float durationSeconds = 0.8f)
    {
        if (damage <= 0) return;

        var viewport = viewportSize();
        if (viewport.X <= 0 || viewport.Y <= 0) return;

        // 1/4 width/height offset right + down from crosshair (screen center)
        var start = new Vector2(viewport.X / 2f, viewport.Y / 2f) + new Vector2(viewport.X * 0.25f, viewport.Y * 0.25f);

        display.DisplayWithMotionAndScale(
            $"-{damage}HP",
            fontSize,
            TookColor,
            startPosition: start,
            endPosition: start + new Vector2(0, -TookUpPixels),
            startScale: 3.0f,
            endScale: 1.0f,
            durationSeconds);
    }

    private static bool TryWorldToScreen(Vector3 worldPosition, ICamera camera, Vector2 viewport, out Vector2 screenPosition)
    {
        // Match shader convention: clip = projection * view * world
        var view = camera.ViewMatrix;
        var projection = camera.ProjectionMatrix;

        var clip = Vector4.TransformRow(new Vector4(worldPosition, 1f), view);
        clip = Vector4.TransformRow(clip, projection);

        if (MathF.Abs(clip.W) < 1e-5f || clip.W <= 0)
        {
            screenPosition = default;
            return false;
        }

        var ndc = new Vector3(clip.X, clip.Y, clip.Z) / clip.W;

        // Convert NDC (-1..1) to screen pixels, with Y down.
        screenPosition = new Vector2(
            (ndc.X * 0.5f + 0.5f) * viewport.X,
            (-ndc.Y * 0.5f + 0.5f) * viewport.Y);

        // If way off-screen, treat as not visible.
        return screenPosition.X >= -viewport.X && screenPosition.X <= viewport.X * 2 && screenPosition.Y >= -viewport.Y && screenPosition.Y <= viewport.Y * 2;
    }
}
