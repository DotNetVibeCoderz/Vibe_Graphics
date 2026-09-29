using System.Numerics;
using ThreeNet;

namespace DemoGraphics.Framework;

/// <summary>
/// The weather and the clock, shared by every scene. Wind is one number for the
/// whole app, so raising it moves the water, the trees and the smoke together
/// instead of each scene inventing its own.
/// </summary>
public sealed class EnvironmentState
{
    private float _hour = 9.4f;
    private float _sunAzimuth = 115f;
    private float _windSpeed = 5f;
    private float _windDirection = 40f;
    private float _cloudCover = 0.25f;
    private float _fogDensity = 0.0035f;
    private float _wetness;
    private float _precipitation;
    private float _snow;
    private float _temperature = 24f;

    /// <summary>Raised whenever any value changes, so scenes can re-apply it.</summary>
    public event Action? Changed;

    /// <summary>Local time in hours, 0-24.</summary>
    public float Hour
    {
        get => _hour;
        set => Set(ref _hour, ((value % 24f) + 24f) % 24f);
    }

    /// <summary>Compass direction the sun rises towards, in degrees.</summary>
    public float SunAzimuth
    {
        get => _sunAzimuth;
        set => Set(ref _sunAzimuth, value);
    }

    /// <summary>Wind speed in metres per second.</summary>
    public float WindSpeed
    {
        get => _windSpeed;
        set => Set(ref _windSpeed, MathF.Max(0f, value));
    }

    /// <summary>Direction the wind blows towards, in degrees.</summary>
    public float WindDirection
    {
        get => _windDirection;
        set => Set(ref _windDirection, value);
    }

    /// <summary>0 = clear sky, 1 = solid overcast.</summary>
    public float CloudCover
    {
        get => _cloudCover;
        set => Set(ref _cloudCover, Math.Clamp(value, 0f, 1f));
    }

    public float FogDensity
    {
        get => _fogDensity;
        set => Set(ref _fogDensity, MathF.Max(0f, value));
    }

    /// <summary>How wet surfaces are: lowers roughness and lifts reflections.</summary>
    public float Wetness
    {
        get => _wetness;
        set => Set(ref _wetness, Math.Clamp(value, 0f, 1f));
    }

    /// <summary>Rain or snow rate, 0-1, driving particle counts.</summary>
    public float Precipitation
    {
        get => _precipitation;
        set => Set(ref _precipitation, Math.Clamp(value, 0f, 1f));
    }

    /// <summary>How much snow has settled, 0-1.</summary>
    public float SnowAmount
    {
        get => _snow;
        set => Set(ref _snow, Math.Clamp(value, 0f, 1f));
    }

    /// <summary>Air temperature in degrees Celsius; below zero precipitation is snow.</summary>
    public float Temperature
    {
        get => _temperature;
        set => Set(ref _temperature, value);
    }

    /// <summary>Unit vector from the scene towards the sun.</summary>
    public Vector3 SunPosition
    {
        get
        {
            // A simple day arc: up at 6, highest at noon, down at 18.
            float elevation = 74f.ToRadians() * MathF.Sin(MathF.PI * (Hour - 6f) / 12f);
            float azimuth = SunAzimuth.ToRadians() + (MathF.PI * (Hour - 12f) / 12f);
            float cos = MathF.Cos(elevation);
            return Vector3.Normalize(new Vector3(MathF.Sin(azimuth) * cos, MathF.Sin(elevation), MathF.Cos(azimuth) * cos));
        }
    }

    /// <summary>Unit vector from the scene towards the moon (opposite the sun).</summary>
    public Vector3 MoonPosition => Vector3.Normalize(new Vector3(-SunPosition.X, MathF.Abs(SunPosition.Y) + 0.25f, -SunPosition.Z));

    /// <summary>How far below the horizon the light is: 0 in daylight, 1 at night.</summary>
    public float NightFactor => 1f - Math.Clamp((SunPosition.Y + 0.12f) / 0.32f, 0f, 1f);

    /// <summary>Sunlight colour, warm near the horizon and neutral overhead.</summary>
    public Vector3 SunColor
    {
        get
        {
            float height = Math.Clamp(SunPosition.Y, 0f, 1f);
            float kelvin = float.Lerp(1900f, 6400f, MathF.Pow(height, 0.35f));
            return Palette.Kelvin(kelvin);
        }
    }

    /// <summary>Sunlight intensity, fading out under the horizon.</summary>
    public float SunIntensity
    {
        get
        {
            float height = Math.Clamp(SunPosition.Y, 0f, 1f);
            float clouds = float.Lerp(1f, 0.35f, CloudCover);
            return 5.6f * MathF.Pow(height, 0.6f) * clouds;
        }
    }

    /// <summary>Sky light colour and strength, which carries the scene at night.</summary>
    public (Vector3 Color, float Intensity) SkyLight
    {
        get
        {
            float night = NightFactor;
            Vector3 day = new(0.55f, 0.68f, 0.92f);
            Vector3 dark = new(0.16f, 0.22f, 0.40f);
            float intensity = float.Lerp(0.55f, 0.06f, night) * float.Lerp(1f, 1.5f, CloudCover);
            return (Vector3.Lerp(day, dark, night), intensity);
        }
    }

    /// <summary>Wind as a ground plane vector in metres per second.</summary>
    public Vector2 WindVector
    {
        get
        {
            float radians = WindDirection.ToRadians();
            return new Vector2(MathF.Sin(radians), MathF.Cos(radians)) * WindSpeed;
        }
    }

    /// <summary>0-1 wind strength for shader work, saturating around 25 m/s.</summary>
    public float WindStrength => Math.Clamp(WindSpeed / 25f, 0f, 1f);

    /// <summary>Time of day as hh:mm, for read-outs.</summary>
    public string Clock => $"{(int)Hour:00}:{(int)((Hour - (int)Hour) * 60f):00}";

    /// <summary>Applies a named weather preset. Returns false for an unknown name.</summary>
    public bool ApplyWeather(string name)
    {
        switch (name)
        {
            case "Clear":
                (CloudCover, FogDensity, Wetness, Precipitation, SnowAmount, WindSpeed, Temperature) = (0.08f, 0.004f, 0f, 0f, 0f, 3f, 27f);
                break;
            case "Partly cloudy":
                (CloudCover, FogDensity, Wetness, Precipitation, SnowAmount, WindSpeed, Temperature) = (0.35f, 0.006f, 0f, 0f, 0f, 6f, 24f);
                break;
            case "Overcast":
                (CloudCover, FogDensity, Wetness, Precipitation, SnowAmount, WindSpeed, Temperature) = (0.85f, 0.012f, 0.2f, 0f, 0f, 8f, 19f);
                break;
            case "Rain":
                (CloudCover, FogDensity, Wetness, Precipitation, SnowAmount, WindSpeed, Temperature) = (0.95f, 0.02f, 0.85f, 0.6f, 0f, 12f, 16f);
                break;
            case "Storm":
                (CloudCover, FogDensity, Wetness, Precipitation, SnowAmount, WindSpeed, Temperature) = (1f, 0.03f, 1f, 1f, 0f, 24f, 14f);
                break;
            case "Fog":
                (CloudCover, FogDensity, Wetness, Precipitation, SnowAmount, WindSpeed, Temperature) = (0.7f, 0.075f, 0.35f, 0f, 0f, 2f, 11f);
                break;
            case "Snow":
                (CloudCover, FogDensity, Wetness, Precipitation, SnowAmount, WindSpeed, Temperature) = (0.9f, 0.025f, 0.1f, 0.5f, 0.8f, 7f, -6f);
                break;
            default:
                return false;
        }

        return true;
    }

    /// <summary>Weather presets in the order the control sheet lists them.</summary>
    public static IReadOnlyList<string> WeatherNames { get; } =
        ["Clear", "Partly cloudy", "Overcast", "Rain", "Storm", "Fog", "Snow"];

    /// <summary>Every value as strings, for capture sidecars and reports.</summary>
    public Dictionary<string, string> Describe() => new()
    {
        ["hour"] = Clock,
        ["sunAzimuth"] = $"{SunAzimuth:F0} deg",
        ["windSpeed"] = $"{WindSpeed:F1} m/s",
        ["windDirection"] = $"{WindDirection:F0} deg",
        ["cloudCover"] = $"{CloudCover:P0}",
        ["fogDensity"] = $"{FogDensity:F4}",
        ["wetness"] = $"{Wetness:P0}",
        ["precipitation"] = $"{Precipitation:P0}",
        ["snow"] = $"{SnowAmount:P0}",
        ["temperature"] = $"{Temperature:F0} C",
    };

    private void Set(ref float field, float value)
    {
        if (MathF.Abs(field - value) < 1e-5f)
        {
            return;
        }

        field = value;
        Changed?.Invoke();
    }
}
