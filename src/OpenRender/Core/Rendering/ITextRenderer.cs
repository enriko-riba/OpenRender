using OpenTK.Mathematics;

namespace OpenRender.Core.Rendering.Text;

public interface ITextRenderer
{
    Matrix4 Projection { get; set; }
    
    Rectangle Measure(string text);

    void Render(string text, float x, float y, Vector3 color);
}