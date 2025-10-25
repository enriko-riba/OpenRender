using OpenRender.Core.Rendering;
using OpenRender.SceneManagement;
using OpenTK.Mathematics;

internal static class MathUtil
{
    public static float SmoothStep(float edge0, float edge1, float x)
    {
        float t = Math.Clamp((x - edge0) / (edge1 - edge0), 0.0f, 1.0f);
        return t * t * (3.0f - 2.0f * t);
    }
}

internal class DayNightCycle(Scene scene)
{
    private LightUniform dirLight = scene.Lights.ElementAt(0);
    private DateTimeOffset timeOfDay = new(DateTime.UtcNow.Date.AddHours(19));

    // Call this *each frame* with elapsedSeconds
    public void Tick(double elapsedSeconds)
    {
        // 1 real second = 1 game minute (as you had)
        timeOfDay = timeOfDay.AddMinutes(elapsedSeconds);

        UpdateSunDirection(timeOfDay);
    }

    public LightUniform DirLight => dirLight;
    public TimeSpan TimeOfDay => timeOfDay.TimeOfDay;

    private void UpdateSunDirection(DateTimeOffset dayTime)
    {
        float t = (float)dayTime.TimeOfDay.TotalHours / 24.0f;
        float angle = (t - 0.25f) * 2.0f * MathF.PI; // -0.25 to make 6am the sunrise point

        float x = MathF.Cos(angle);
        float y = MathF.Sin(angle);

        // Tilt the sun path a bit for aesthetics
        var sunDir = Vector3.Normalize(new Vector3(x, y, -0.25f));

        dirLight.Direction = -sunDir;

        // Ambient: darker at night, brighter midday
        var ambientDay = new Vector3(0.35f);
        var ambientNight = new Vector3(0.15f);
        float dayAmt = MathUtil.SmoothStep(0.0f, 0.15f, sunDir.Y);
        dirLight.Ambient = Vector3.Lerp(ambientNight, ambientDay, dayAmt);

        dirLight.Diffuse = new Vector3(1.0f) * dayAmt;
        dirLight.Specular = new Vector3(1.0f) * dayAmt;

        scene.UpdateLight(0, dirLight);
    }
}
