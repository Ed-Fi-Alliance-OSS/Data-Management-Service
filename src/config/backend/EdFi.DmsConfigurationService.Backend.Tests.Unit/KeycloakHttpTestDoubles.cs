// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit;

public sealed record RecordedRequest(
    HttpMethod Method,
    Uri? Uri,
    string? Authorization,
    string? ContentType,
    IReadOnlyList<KeyValuePair<string, string>> Form,
    string RawText,
    bool ConnectionClose
);

/// <summary>
/// Records each request, including its form body, then runs <paramref name="onRequest"/> before handing
/// it to <see cref="Respond"/>.
/// </summary>
public sealed class RecordingHandler(
    Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond,
    Action? onRequest = null
) : HttpMessageHandler
{
    public List<RecordedRequest> Requests { get; } = [];

    public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Respond { get; set; } =
        respond;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken
    )
    {
        string body = request.Content is null
            ? ""
            : await request.Content.ReadAsStringAsync(cancellationToken);
        List<KeyValuePair<string, string>> form =
        [
            .. body.Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(pair => pair.Split('=', 2))
                .Select(parts => new KeyValuePair<string, string>(
                    WebUtility.UrlDecode(parts[0]),
                    WebUtility.UrlDecode(parts.Length > 1 ? parts[1] : "")
                )),
        ];
        Requests.Add(
            new RecordedRequest(
                request.Method,
                request.RequestUri,
                request.Headers.Authorization?.ToString(),
                request.Content?.Headers.ContentType?.MediaType,
                form,
                $"{request.RequestUri} {request.Headers} {request.Content?.Headers} {body}",
                request.Headers.ConnectionClose == true
            )
        );
        onRequest?.Invoke();
        return await Respond(request, cancellationToken);
    }
}

/// <summary>A non-seekable body that records whether it was read and can fail or stall on demand.</summary>
public sealed class ProbeStream(
    byte[] data,
    Exception? failure = null,
    bool stall = false,
    Action? onRead = null
) : Stream
{
    private int _position;

    public bool WasRead { get; private set; }

    public static ProbeStream Failing(Exception failure, byte[]? before = null) => new(before ?? [], failure);

    public static ProbeStream Stalling(Action? onRead = null) => new([], stall: true, onRead: onRead);

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default
    )
    {
        WasRead = true;
        onRead?.Invoke();
        if (_position < data.Length)
        {
            int count = Math.Min(buffer.Length, data.Length - _position);
            data.AsMemory(_position, count).CopyTo(buffer);
            _position += count;
            return count;
        }

        if (stall)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }

        if (failure is not null)
        {
            throw failure;
        }

        return 0;
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush() { }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

public sealed record KeycloakCapturedLogEntry(
    LogLevel Level,
    string Message,
    IReadOnlyList<KeyValuePair<string, object?>> State,
    IReadOnlyList<object?> Scopes,
    Exception? Exception
);

/// <summary>
/// Records every field a sink could write: the rendered message, every state value, every active
/// scope and the attached exception with its inner chain.
/// </summary>
public sealed class KeycloakCapturingLogger<T> : ILogger<T>
{
    private readonly List<object?> _scopes = [];

    public List<KeycloakCapturedLogEntry> Entries { get; } = [];

    public IDisposable BeginScope<TState>(TState state)
        where TState : notnull
    {
        _scopes.Add(state);
        return new ScopeExit(() => _scopes.Remove(state));
    }

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter
    ) =>
        Entries.Add(
            new KeycloakCapturedLogEntry(
                logLevel,
                formatter(state, exception),
                state is IEnumerable<KeyValuePair<string, object?>> pairs ? [.. pairs] : [],
                [.. _scopes],
                exception
            )
        );

    public string AllText()
    {
        var text = new StringBuilder();
        foreach (KeycloakCapturedLogEntry entry in Entries)
        {
            text.AppendLine(entry.Message);
            foreach (KeyValuePair<string, object?> pair in entry.State)
            {
                text.AppendLine(pair.Value?.ToString());
            }

            foreach (string scope in entry.Scopes.Select(value => $"{value}"))
            {
                text.AppendLine(scope);
            }

            if (entry.Exception is { } exception)
            {
                AppendExceptionChain(text, exception);
            }
        }

        return text.ToString();
    }

    private static void AppendExceptionChain(StringBuilder text, Exception exception)
    {
        text.AppendLine(exception.ToString());
        if (exception.InnerException is { } inner)
        {
            AppendExceptionChain(text, inner);
        }
    }

    private sealed class ScopeExit(Action exit) : IDisposable
    {
        public void Dispose() => exit();
    }
}
