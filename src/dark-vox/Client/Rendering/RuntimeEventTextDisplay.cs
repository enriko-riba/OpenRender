using OpenRender.Core.Rendering.Text;
using OpenTK.Mathematics;

namespace DarkVox.Client.Rendering;

public sealed class RuntimeEventTextDisplay(ITextRenderer textRenderer)
{
    private const float DefaultUpPixels = 60f;

    private readonly List<Entry> entries = [];

    private struct Entry
    {
        public string Text;
        public int FontSize;
        public Vector3 Color;
        public Vector2 StartPos;
        public Vector2 EndPos;
        public float StartScale;
        public float EndScale;
        public float AgeSeconds;
        public float DurationSeconds;
    }

    public void Display(string text, int fontSize, Vector3 color, Vector2 position, float durationSeconds)
    {
        DisplayWithMotionAndScale(
            text,
            fontSize,
            color,
            position,
            position + new Vector2(0, -DefaultUpPixels),
            startScale: 1.0f,
            endScale: 1.0f,
            durationSeconds);
    }

    internal void DisplayWithMotionAndScale(
        string text,
        int fontSize,
        Vector3 color,
        Vector2 startPosition,
        Vector2 endPosition,
        float startScale,
        float endScale,
        float durationSeconds)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        entries.Add(new Entry
        {
            Text = text,
            FontSize = Math.Max(1, fontSize),
            Color = color,
            StartPos = startPosition,
            EndPos = endPosition,
            StartScale = MathF.Max(0.01f, startScale),
            EndScale = MathF.Max(0.01f, endScale),
            AgeSeconds = 0f,
            DurationSeconds = MathF.Max(0.01f, durationSeconds),
        });
    }

    internal void Update(double elapsedSeconds)
    {
        if (entries.Count == 0) return;

        var dt = (float)Math.Max(0.0, elapsedSeconds);
        for (var i = entries.Count - 1; i >= 0; i--)
        {
            var e = entries[i];
            e.AgeSeconds += dt;

            if (e.AgeSeconds >= e.DurationSeconds)
            {
                entries.RemoveAt(i);
                continue;
            }

            entries[i] = e;
        }
    }

    internal void Render()
    {
        if (entries.Count == 0) return;

        for (var i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            var t = Math.Clamp(e.AgeSeconds / e.DurationSeconds, 0f, 1f);

            var pos = e.StartPos + (e.EndPos - e.StartPos) * t;
            var scale = e.StartScale + (e.EndScale - e.StartScale) * t;

            var scaledFontSize = Math.Max(1, (int)MathF.Round(e.FontSize * scale));
            textRenderer.Render(e.Text, scaledFontSize, pos.X, pos.Y, e.Color);
        }
    }
}
