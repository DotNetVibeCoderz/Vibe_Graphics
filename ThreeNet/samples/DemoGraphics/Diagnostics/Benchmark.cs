using System.Globalization;
using System.Text;
using System.Text.Json;
using DemoGraphics.Framework;
using ThreeNet;

namespace DemoGraphics.Diagnostics;

public enum BenchmarkPhase
{
    Idle,
    /// <summary>Running the camera path without recording, to let caches settle.</summary>
    Warmup,
    Measuring,
    Finished,
}

/// <summary>One scene's result. Frame times are milliseconds, rates are per second.</summary>
public readonly record struct BenchmarkEntry(
    string SceneId,
    string SceneTitle,
    int Frames,
    float AverageFps,
    float MinFps,
    float MaxFps,
    float OnePercentLowFps,
    float MedianMs,
    float P95Ms,
    float P99Ms,
    float CpuMs,
    float GpuMs,
    int DrawCalls,
    int Triangles,
    int Lights,
    int Stutters);

/// <summary>What the run was measured on, so a result can be reproduced.</summary>
public readonly record struct BenchmarkHeader(
    string Adapter,
    string Driver,
    string Backend,
    string DeviceType,
    string Quality,
    string RenderPath,
    int Width,
    int Height,
    int MsaaSamples,
    bool Shadows,
    bool Ssao,
    bool Bloom,
    bool GpuTiming,
    string CoreVersion,
    DateTimeOffset StartedAt);

/// <summary>
/// Plays a fixed camera path through a list of scenes and records what each one
/// costs. The host drives it one frame at a time: it loads the scene the runner
/// asks for, then hands over every frame time until the runner is finished.
/// </summary>
public sealed class BenchmarkRunner
{
    private readonly List<BenchmarkEntry> _results = [];
    private readonly FrameLog _log = new(4096);
    private readonly float _warmupSeconds;
    private readonly float _measureSeconds;

    private IReadOnlyList<DemoScene> _scenes = [];
    private float _phaseElapsed;
    private double _cpuSum;
    private double _gpuSum;
    private long _drawSum;
    private long _triangleSum;
    private int _lights;
    private int _statFrames;

    public BenchmarkRunner(float warmupSeconds = 1.5f, float measureSeconds = 6f)
    {
        _warmupSeconds = warmupSeconds;
        _measureSeconds = measureSeconds;
    }

    public BenchmarkPhase Phase { get; private set; } = BenchmarkPhase.Idle;

    public int Index { get; private set; }

    public int Total => _scenes.Count;

    /// <summary>The scene being measured, or null when idle.</summary>
    public DemoScene? Current => Index >= 0 && Index < _scenes.Count ? _scenes[Index] : null;

    /// <summary>Set when the host has to load <see cref="Current"/> before the next frame.</summary>
    public bool WantsSceneLoad { get; private set; }

    /// <summary>Seconds into the current scene, for the camera path.</summary>
    public float SceneElapsed { get; private set; }

    public IReadOnlyList<BenchmarkEntry> Results => _results;

    public bool IsRunning => Phase is BenchmarkPhase.Warmup or BenchmarkPhase.Measuring;

    /// <summary>0-1 over the whole run.</summary>
    public float Progress
    {
        get
        {
            if (_scenes.Count == 0)
            {
                return Phase == BenchmarkPhase.Finished ? 1f : 0f;
            }

            float perScene = _warmupSeconds + _measureSeconds;
            float done = (Index * perScene) + Math.Clamp(SceneElapsed, 0f, perScene);
            return Math.Clamp(done / (_scenes.Count * perScene), 0f, 1f);
        }
    }

    /// <summary>Seconds one scene takes, warmup included.</summary>
    public float SecondsPerScene => _warmupSeconds + _measureSeconds;

    public void Start(IReadOnlyList<DemoScene> scenes)
    {
        _scenes = scenes;
        _results.Clear();
        Index = 0;
        Phase = scenes.Count == 0 ? BenchmarkPhase.Finished : BenchmarkPhase.Warmup;
        WantsSceneLoad = scenes.Count > 0;
        ResetScene();
    }

    public void Cancel()
    {
        Phase = BenchmarkPhase.Idle;
        WantsSceneLoad = false;
        _scenes = [];
    }

    /// <summary>Called by the host once the scene the runner asked for is live.</summary>
    public void SceneLoaded()
    {
        WantsSceneLoad = false;
        ResetScene();
    }

    /// <summary>Records one frame. Returns true when the whole run just finished.</summary>
    public bool Frame(float deltaSeconds, FrameStats stats)
    {
        if (!IsRunning || WantsSceneLoad)
        {
            return false;
        }

        SceneElapsed += deltaSeconds;
        _phaseElapsed += deltaSeconds;

        if (Phase == BenchmarkPhase.Warmup)
        {
            if (_phaseElapsed >= _warmupSeconds)
            {
                Phase = BenchmarkPhase.Measuring;
                _phaseElapsed = 0f;
                _log.Clear();
            }

            return false;
        }

        _log.Add(deltaSeconds * 1000f);
        _cpuSum += stats.CpuTimeMs;
        _gpuSum += stats.GpuTimeMs;
        _drawSum += stats.DrawCalls;
        _triangleSum += stats.Triangles;
        _lights = Math.Max(_lights, stats.Lights);
        _statFrames++;

        if (_phaseElapsed < _measureSeconds)
        {
            return false;
        }

        Record();
        Index++;
        if (Index >= _scenes.Count)
        {
            Phase = BenchmarkPhase.Finished;
            return true;
        }

        Phase = BenchmarkPhase.Warmup;
        WantsSceneLoad = true;
        return false;
    }

    private void Record()
    {
        DemoScene? scene = Index < _scenes.Count ? _scenes[Index] : null;
        float median = _log.PercentileMs(50f);
        float min = _log.MinMs;
        float max = _log.MaxMs;
        int frames = Math.Max(1, _statFrames);
        _results.Add(new BenchmarkEntry(
            scene?.Id ?? "-",
            scene?.Title ?? "-",
            _log.Count,
            _log.AverageMs <= 0f ? 0f : 1000f / _log.AverageMs,
            max <= 0f ? 0f : 1000f / max,
            min <= 0f ? 0f : 1000f / min,
            _log.OnePercentLowFps,
            median,
            _log.PercentileMs(95f),
            _log.PercentileMs(99f),
            (float)(_cpuSum / frames),
            (float)(_gpuSum / frames),
            (int)(_drawSum / frames),
            (int)(_triangleSum / frames),
            _lights,
            _log.Stutters()));
    }

    private void ResetScene()
    {
        SceneElapsed = 0f;
        _phaseElapsed = 0f;
        _cpuSum = 0;
        _gpuSum = 0;
        _drawSum = 0;
        _triangleSum = 0;
        _lights = 0;
        _statFrames = 0;
        _log.Clear();
    }
}

/// <summary>Writes a finished run to disk as JSON and CSV.</summary>
public static class BenchmarkReport
{
    /// <summary>Where captures and reports go.</summary>
    public static string OutputDirectory
    {
        get
        {
            string directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "ThreeNet",
                "DemoGraphics");
            Directory.CreateDirectory(directory);
            return directory;
        }
    }

    /// <summary>Writes both files and returns the paths, JSON first.</summary>
    public static (string Json, string Csv) Write(BenchmarkHeader header, IReadOnlyList<BenchmarkEntry> results)
    {
        string stamp = header.StartedAt.ToLocalTime().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        string json = Path.Combine(OutputDirectory, $"benchmark-{stamp}.json");
        string csv = Path.Combine(OutputDirectory, $"benchmark-{stamp}.csv");

        File.WriteAllText(json, JsonSerializer.Serialize(
            new { header, results },
            new JsonSerializerOptions { WriteIndented = true }));

        StringBuilder builder = new();
        builder.AppendLine(
            "scene,title,frames,avg_fps,min_fps,max_fps,one_percent_low_fps,median_ms,p95_ms,p99_ms,cpu_ms,gpu_ms,draw_calls,triangles,lights,stutters");
        foreach (BenchmarkEntry entry in results)
        {
            builder.Append(entry.SceneId).Append(',')
                .Append('"').Append(entry.SceneTitle.Replace("\"", "\"\"")).Append('"').Append(',')
                .Append(entry.Frames).Append(',')
                .Append(Number(entry.AverageFps)).Append(',')
                .Append(Number(entry.MinFps)).Append(',')
                .Append(Number(entry.MaxFps)).Append(',')
                .Append(Number(entry.OnePercentLowFps)).Append(',')
                .Append(Number(entry.MedianMs)).Append(',')
                .Append(Number(entry.P95Ms)).Append(',')
                .Append(Number(entry.P99Ms)).Append(',')
                .Append(Number(entry.CpuMs)).Append(',')
                .Append(Number(entry.GpuMs)).Append(',')
                .Append(entry.DrawCalls).Append(',')
                .Append(entry.Triangles).Append(',')
                .Append(entry.Lights).Append(',')
                .Append(entry.Stutters)
                .AppendLine();
        }

        File.WriteAllText(csv, builder.ToString());
        return (json, csv);
    }

    private static string Number(float value) => value.ToString("F3", CultureInfo.InvariantCulture);
}
