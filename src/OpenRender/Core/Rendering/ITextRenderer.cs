using OpenTK.Mathematics;

namespace OpenRender.Core.Rendering.Text;

public interface ITextRenderer
{
    Matrix4 Projection { get; set; }
    
    Rectangle Measure(string text);
    Rectangle Measure(string text, int fontSize);

    void Render(string text, int fontSize, float x, float y, Vector3 color);
    void Render(string text, float x, float y, Vector3 color);
}