namespace GovernanceCouncil.Web;

public static class DotEnvLoader
{
    public static void Load(string path)
    {
        if (!File.Exists(path)) return;
        foreach (var line in File.ReadAllLines(path))
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith('#')) continue;
            var idx = trimmed.IndexOf('=');
            if (idx <= 0) continue;
            var key = trimmed[..idx].Trim();
            var value = trimmed[(idx + 1)..].Trim().Trim('"');
            Environment.SetEnvironmentVariable(key, value);
        }
    }
}
