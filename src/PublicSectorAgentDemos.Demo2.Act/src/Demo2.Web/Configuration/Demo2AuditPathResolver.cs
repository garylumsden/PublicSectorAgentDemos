namespace Demo2.Web.Configuration;

public static class Demo2AuditPathResolver
{
    public static string Resolve(
        string configuredPath,
        string environmentName,
        string? homeDirectory,
        string appBaseDirectory)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            throw new InvalidOperationException("Audit path is required.");
        }

        if (string.Equals(environmentName, Environments.Development, StringComparison.Ordinal) ||
            string.Equals(environmentName, "Testing", StringComparison.Ordinal))
        {
            string baseDirectory = Path.GetFullPath(appBaseDirectory);
            string localPath = Path.GetFullPath(Path.Combine(baseDirectory, configuredPath));
            if (!localPath.StartsWith(
                    baseDirectory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Development audit path must remain under the application directory.");
            }

            return localPath;
        }

        if (string.IsNullOrWhiteSpace(homeDirectory) ||
            !Path.IsPathFullyQualified(homeDirectory))
        {
            throw new InvalidOperationException(
                "HOME must identify the writable App Service home directory.");
        }

        string fileName = Path.GetFileName(configuredPath);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            throw new InvalidOperationException("Audit path must include a filename.");
        }

        string auditDirectory = Path.GetFullPath(
            Path.Combine(homeDirectory, "LogFiles", "audit"));
        return Path.Combine(auditDirectory, fileName);
    }
}
