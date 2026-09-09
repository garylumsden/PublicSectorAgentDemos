using System.Text.Json;
using System.Text.Json.Serialization;
using Defra.Contracts.V1;

namespace Defra.Audit;

public interface IAuditEventSerializer
{
    byte[] Serialize(SanitizedAuditEvent auditEvent);
}

public sealed class JsonAuditEventSerializer : IAuditEventSerializer
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public byte[] Serialize(SanitizedAuditEvent auditEvent)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);
        return JsonSerializer.SerializeToUtf8Bytes(auditEvent, SerializerOptions);
    }
}

public interface IAppendOnlyAuditWriter
{
    ValueTask AppendAsync(SanitizedAuditEvent auditEvent, CancellationToken cancellationToken = default);
}

public sealed class JsonLinesAuditWriter : IAppendOnlyAuditWriter, IAsyncDisposable
{
    private static readonly byte[] NewLine = "\n"u8.ToArray();
    private readonly Stream _stream;
    private readonly IAuditEventSerializer _serializer;
    private readonly bool _leaveOpen;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private bool _disposed;

    public JsonLinesAuditWriter(
        Stream stream,
        IAuditEventSerializer? serializer = null,
        bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanWrite)
        {
            throw new ArgumentException("Audit stream must be writable.", nameof(stream));
        }

        _stream = stream;
        _serializer = serializer ?? new JsonAuditEventSerializer();
        _leaveOpen = leaveOpen;
    }

    public async ValueTask AppendAsync(
        SanitizedAuditEvent auditEvent,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        byte[] payload = _serializer.Serialize(auditEvent);

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_stream.CanSeek)
            {
                _stream.Seek(0, SeekOrigin.End);
            }

            await _stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
            await _stream.WriteAsync(NewLine, cancellationToken).ConfigureAwait(false);
            await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (!_leaveOpen)
        {
            await _stream.DisposeAsync().ConfigureAwait(false);
        }

        _writeLock.Dispose();
    }
}
