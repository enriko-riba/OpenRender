using OpenRender.Core.Rendering;
using OpenRender.SceneManagement;
using OpenTK.Mathematics;
using DarkVox.Components;

internal static class MathUtil
{
    public static float SmoothStep(float edge0, float edge1, float x)
    {
        var t = Math.Clamp((x - edge0) / (edge1 - edge0), 0.0f, 1.0f);
        return t * t * (3.0f - 2.0f * t);
    }
}

internal class DayNightCycle : IDayNightTimeProvider
{
    private readonly Scene scene;
    private LightUniform dirLight;
    private int timeOfDaySeconds = 7 * 60 * 60;

    public DayNightCycle(Scene scene)
    {
        this.scene = scene;

        // Create initial directional light (sun)
        dirLight = new LightUniform()
        {
            Direction = new Vector3(0, -1, 0),
            Ambient = new Vector3(0.35f, 0.35f, 0.35f),
            Diffuse = new Vector3(1),
            Specular = new Vector3(1),
        };

        // Add the light to the scene immediately
        scene.AddLight(dirLight);
    }

    public float SunPathTilt { get; set; } = 0.35f;
    public float DayFactor { get; private set; }
    public Vector3 SunDirection { get; private set; }

    public void SetTimeOfDaySeconds(int secondsOfDay)
    {
        const int secondsPerDay = 24 * 60 * 60;
        var normalized = ((secondsOfDay % secondsPerDay) + secondsPerDay) % secondsPerDay;
        timeOfDaySeconds = normalized;
        UpdateSunDirection(timeOfDaySeconds);
    }

    public LightUniform DirLight => dirLight;
    public TimeSpan TimeOfDay => TimeSpan.FromSeconds(timeOfDaySeconds);

    private void UpdateSunDirection(int secondsOfDay)
    {
        const float secondsPerDay = 24.0f * 60.0f * 60.0f;
        var t = secondsOfDay / secondsPerDay;
        var angle = (t - 0.25f) * 2.0f * MathF.PI; // -0.25 to make 6am the sunrise point

        var x = MathF.Cos(angle);
        var y = MathF.Sin(angle);

        // Tilt the sun path a bit for aesthetics
        var sunDir = Vector3.Normalize(new Vector3(x, y, SunPathTilt));
        SunDirection = sunDir;

        dirLight.Direction = -sunDir;

        // Ambient: darker at night, brighter midday
        var ambientDay = new Vector3(0.45f, 0.45f, 0.45f); // Slightly brighter day
        var ambientNight = new Vector3(0.05f, 0.05f, 0.08f); // Playable night (moonlight)
        var dayAmt = MathUtil.SmoothStep(0.0f, 0.15f, sunDir.Y);
        DayFactor = dayAmt;
        dirLight.Ambient = Vector3.Lerp(ambientNight, ambientDay, dayAmt);

        dirLight.Diffuse = new Vector3(1.0f) * dayAmt;
        dirLight.Specular = new Vector3(1.0f) * dayAmt;

        scene.UpdateLight(0, dirLight);
    }
}
