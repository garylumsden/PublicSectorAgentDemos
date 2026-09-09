using System.Net;

namespace PublicSectorAgentDemos.Demo1.Web;

public static class LocalBoundary
{
    public const int Port = 5090;

    public static bool Allows(HttpContext context)
    {
        IPAddress? address = context.Connection.RemoteIpAddress;
        if (address?.IsIPv4MappedToIPv6 == true)
        {
            address = address.MapToIPv4();
        }

        return address is not null && IPAddress.IsLoopback(address) &&
            context.Request.Scheme == "http" &&
            context.Request.Host.Port == Port &&
            (context.Request.Host.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
             context.Request.Host.Host is "127.0.0.1" or "[::1]") &&
            !context.Request.Headers.ContainsKey("Forwarded") &&
            !context.Request.Headers.Keys.Any(key =>
                key.StartsWith("X-Forwarded-", StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsSameOrigin(HttpRequest request) =>
        request.Headers.Origin.Count == 1 &&
        string.Equals(request.Headers.Origin[0], $"http://{request.Host}", StringComparison.OrdinalIgnoreCase) &&
        (!request.Headers.ContainsKey("Sec-Fetch-Site") ||
         request.Headers["Sec-Fetch-Site"].ToString() == "same-origin");
}

public static class DemoLimits
{
    public const int PromptCharacters = 8_000;
    public const int RequestBytes = 32_768;
    public const int ResponseBytes = 1_048_576;
    public const int ConcurrentComparisons = 2;
    public const int ConcurrentAssessments = 1;
    public const int CompletedSnapshots = 8;
    public const int AssessmentInputBytes = 131_072;
    public const int AssessmentResponseBytes = 262_144;
    public const int AssessmentOutputBytes = 16_384;
    public static readonly TimeSpan AgentTimeout = TimeSpan.FromMinutes(3);
    public static readonly TimeSpan AssessmentTimeout = TimeSpan.FromSeconds(90);
    public static readonly TimeSpan SnapshotLifetime = TimeSpan.FromMinutes(30);

    public static async Task<byte[]> ReadBoundedAsync(Stream stream, int limit, CancellationToken cancellationToken)
    {
        using MemoryStream bytes = new();
        byte[] buffer = new byte[8_192];
        while (true)
        {
            int count = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, limit + 1 - (int)bytes.Length)), cancellationToken);
            if (count == 0)
            {
                return bytes.ToArray();
            }

            bytes.Write(buffer, 0, count);
            if (bytes.Length > limit)
            {
                throw new InvalidDataException("The data exceeds the size limit.");
            }
        }
    }
}
