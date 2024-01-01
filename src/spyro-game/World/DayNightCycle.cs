using OpenRender.Core.Rendering;
using OpenRender.SceneManagement;
using OpenTK.Mathematics;

namespace SpyroGame.World;

internal class DayNightCycle(Scene scene)
{
    private LightUniform dirLight = scene.Lights.ElementAt(0);
    private DateTimeOffset timeOfDay = new(DateTime.UtcNow.Date.AddHours(8));  //  we start at morning 08:00h
    private DateTimeOffset lastTime = DateTimeOffset.UtcNow;

    public void Update()
    {
        var cycleTime = DateTimeOffset.UtcNow - lastTime;
        if (cycleTime.TotalSeconds > 1)
        {
            lastTime = DateTimeOffset.UtcNow;
            timeOfDay = timeOfDay.AddMinutes(cycleTime.TotalSeconds);   //  one second wall clock time == one minute in game
            UpdateSunDirection(timeOfDay);
        }
    }

    public LightUniform DirLight => dirLight;

    public TimeSpan TimeOfDay => timeOfDay.TimeOfDay;

    private void UpdateSunDirection(DateTimeOffset dayTime)
    {
        // Define constants for sunrise, sunset, and noon times
        var sunriseTime = new TimeSpan(6, 0, 0);
        var sunsetTime = new TimeSpan(19, 0, 0);

        var ambientDayValue = new Vector3(0.02f);

        var ambientNightMinValue = new Vector3(0.025f);
        var ambientNightMaxValue = new Vector3(0.25f);


        // Calculate time difference from sunrise and sunset
        var timeDifferenceFromSunrise = dayTime.TimeOfDay - sunriseTime;

        // Calculate normalized direction based on the time of day        
        if (timeOfDay.TimeOfDay >= sunriseTime && timeOfDay.TimeOfDay < sunsetTime) // day cycle
        {
            // Between sunrise and sunset, calculate direction based on time difference
            var t = (float)(timeDifferenceFromSunrise.TotalHours / (sunsetTime.TotalHours - sunriseTime.TotalHours));
            var x = -1 + 2 * t;
            var y = -MathF.Sqrt(1 - x * x);

            dirLight.Direction = new Vector3(x, y, 0).Normalized();

            //  ambient intensity

            // Calculate the duration of the day cycle and distance from noon
            var differenceFromSunrise = (float)(dayTime.TimeOfDay - sunriseTime).TotalHours;
            var differenceFromSunset = (float)(sunsetTime - dayTime.TimeOfDay).TotalHours;
            var timeDifference = differenceFromSunrise > differenceFromSunset ? differenceFromSunset : differenceFromSunrise;
            t = MathHelper.Clamp(timeDifference / 2f, 0.0f, 1.0f);
            dirLight.Ambient = Vector3.Lerp(ambientNightMaxValue, ambientDayValue, t);
        }
        else  // night cycle
        {
            dirLight.Direction = Vector3.Zero;

            // Calculate the duration of the night cycle and distance from midnight
            var minMidnightDistance = MathHelper.Min(24f - sunsetTime.TotalHours, sunriseTime.TotalHours);
            var midnightDistance = dayTime.TimeOfDay < sunriseTime ? dayTime.TimeOfDay.TotalHours : 24 - dayTime.TimeOfDay.TotalHours;

            // Calculate normalized t for interpolation during the night cycle
            var t = MathHelper.Clamp((float)(midnightDistance / minMidnightDistance), 0.0f, 1.0f);
            dirLight.Ambient = Vector3.Lerp(ambientNightMinValue, ambientNightMaxValue, t);
        }
        
        scene.UpdateLight(0, dirLight);
    }
}
