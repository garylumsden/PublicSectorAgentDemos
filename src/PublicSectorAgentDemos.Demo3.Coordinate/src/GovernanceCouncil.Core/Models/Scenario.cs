namespace GovernanceCouncil.Core.Models;

using System.Text.Json;

/// <summary>
/// Loads and holds the active <see cref="ScenarioConfig"/>. Resolution order:
/// <c>COUNCIL_SCENARIO_PATH</c> env var → the nearest <c>config/scenario.json</c> walking up from the
/// app base directory → neutral defaults (generic branding + empty council). Call <see cref="Initialise"/>
/// once at startup; <see cref="Current"/> lazily loads on first access otherwise.
/// </summary>
public static class Scenario
{
    private static readonly object _gate = new();
    private static ScenarioConfig? _current;
    private static string? _promptsDir;

    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    /// <summary>The active scenario (neutral defaults until a <c>scenario.json</c> is found).</summary>
    public static ScenarioConfig Current
    {
        get { if (_current is null) Initialise(); return _current!; }
    }

    /// <summary>Directory holding the scenario's persona prompt files (sibling <c>prompts/</c> of the scenario file).</summary>
    public static string PromptsDirectory
    {
        get { if (_promptsDir is null) Initialise(); return _promptsDir!; }
    }

    /// <summary>True when a scenario file was found and parsed (i.e. the template has been configured).</summary>
    public static bool IsConfigured { get; private set; }

    /// <summary>Loads the scenario once. Idempotent — subsequent calls re-read from disk (used after the agent writes config).</summary>
    public static void Initialise()
    {
        lock (_gate)
        {
            var path = ResolveScenarioPath();
            if (path is not null && File.Exists(path))
            {
                try
                {
                    var cfg = JsonSerializer.Deserialize<ScenarioConfig>(File.ReadAllText(path), Options);
                    if (cfg is not null)
                    {
                        _current = cfg;
                        _promptsDir = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, "prompts");
                        IsConfigured = true;
                        return;
                    }
                }
                catch { /* fall through to neutral defaults */ }
            }

            _current = new ScenarioConfig();
            _promptsDir = Path.Combine(FindConfigDir() ?? AppContext.BaseDirectory, "prompts");
            IsConfigured = false;
        }
    }

    private static string? ResolveScenarioPath()
    {
        var env = Environment.GetEnvironmentVariable("COUNCIL_SCENARIO_PATH");
        if (!string.IsNullOrWhiteSpace(env)) return env.Trim();
        var configDir = FindConfigDir();
        return configDir is null ? null : Path.Combine(configDir, "scenario.json");
    }

    /// <summary>Walks up from the app base directory looking for a <c>config/</c> folder (covers dev bin/ runs + container).</summary>
    private static string? FindConfigDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "config");
            if (Directory.Exists(candidate)) return candidate;
        }
        return null;
    }
}
