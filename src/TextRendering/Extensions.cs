using OpenTK.Mathematics;

namespace OpenRender.Text;

internal static class Extensions
{
    public static Color ToImageSharpColor(this in Color4 color)
    {
        var r = (byte)(color.R * 255f);
        var g = (byte)(color.G * 255f);
        var b = (byte)(color.B * 255f);
        var a = (byte)(color.A * 255f);
        return Color.FromRgba(r, g, b, a);
    }    
}
