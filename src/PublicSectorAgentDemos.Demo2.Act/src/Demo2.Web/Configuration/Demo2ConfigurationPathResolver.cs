namespace Demo2.Web.Configuration;

public sealed class Demo2ConfigurationPathResolver(IHostEnvironment environment)
{
    public string ResolveExistingFile(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        if (Path.IsPathRooted(relativePath) ||
            relativePath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries)
                .Any(segment => segment is "." or ".."))
        {
            throw new InvalidOperationException("Demo2 configuration paths must be safe relative paths.");
        }

        string[] candidates =
        [
            Path.GetFullPath(Path.Combine(environment.ContentRootPath, relativePath)),
            Path.GetFullPath(Path.Combine(environment.ContentRootPath, "..", "..", relativePath)),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, relativePath))
        ];

        string? match = candidates.FirstOrDefault(File.Exists);
        return match ?? throw new FileNotFoundException(
            $"Required Demo2 configuration file '{relativePath}' was not found.");
    }
}
