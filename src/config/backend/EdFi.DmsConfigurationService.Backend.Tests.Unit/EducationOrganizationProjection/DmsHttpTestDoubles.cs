// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Concurrent;
using System.Net;
using System.Text;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.EducationOrganizationProjection;

/// <summary>What a fake DMS saw of one request.</summary>
public sealed record RecordedRequest(
    HttpMethod Method,
    Uri Uri,
    string Accept,
    bool HasAuthorization,
    bool HasCookie,
    string? Authorization = null,
    string? ContentType = null,
    string Body = ""
);

/// <summary>
/// A primary handler standing in for DMS. Every request is recorded and signals <see cref="RequestReceived"/> before
/// <c>respond</c> runs, so a test can act while a request is outstanding without any delay.
/// </summary>
public sealed class FakeDmsHandler(
    Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond
) : HttpMessageHandler
{
    private readonly ConcurrentQueue<RecordedRequest> _requests = new();
    private readonly SemaphoreSlim _received = new(0);

    public TaskCompletionSource RequestReceived { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public IReadOnlyList<RecordedRequest> Requests => [.. _requests];

    /// <summary>Completes once <paramref name="count"/> more requests have been recorded since the last call.</summary>
    public async Task WaitForRequestsAsync(int count)
    {
        for (int i = 0; i < count; i++)
        {
            await _received.WaitAsync();
        }
    }

    /// <summary>A handler that answers every request with a fresh response from <paramref name="response"/>.</summary>
    public static FakeDmsHandler Answering(Func<HttpResponseMessage> response) =>
        new((_, _) => Task.FromResult(response()));

    /// <summary>A handler that never answers on its own: it waits until the request's token is cancelled.</summary>
    public static FakeDmsHandler NeverAnswering() =>
        new(
            async (_, cancellationToken) =>
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
                throw new InvalidOperationException("unreachable");
            }
        );

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken
    )
    {
        string body = request.Content is null
            ? string.Empty
            : await request.Content.ReadAsStringAsync(cancellationToken);
        _requests.Enqueue(
            new RecordedRequest(
                request.Method,
                request.RequestUri!,
                request.Headers.Accept.ToString(),
                request.Headers.Authorization is not null,
                request.Headers.Contains("Cookie"),
                request.Headers.Authorization?.ToString(),
                request.Content?.Headers.ContentType?.MediaType,
                body
            )
        );
        RequestReceived.TrySetResult();
        _received.Release();
        return await respond(request, cancellationToken);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _received.Dispose();
        }
        base.Dispose(disposing);
    }
}

/// <summary>An <see cref="IHttpClientFactory"/> whose clients all send through one handler.</summary>
public sealed class SingleHandlerHttpClientFactory(HttpMessageHandler handler, string expectedName)
    : IHttpClientFactory
{
    public HttpClient CreateClient(string name) =>
        name == expectedName
            ? new HttpClient(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan }
            : throw new InvalidOperationException($"Unexpected client name {name}.");
}

/// <summary>Response builders for a fake DMS.</summary>
public static class DmsResponses
{
    public static HttpResponseMessage Text(
        HttpStatusCode status,
        string body,
        string mediaType = "application/json"
    ) => new(status) { Content = new StringContent(body, Encoding.UTF8, mediaType) };

    public static HttpResponseMessage Problem(
        HttpStatusCode status,
        string type,
        string correlationId = "0HN:01"
    ) =>
        Text(
            status,
            $$"""{"type":"{{type}}","title":"t","status":{{(int)status}},"correlationId":"{{correlationId}}"}""",
            "application/problem+json"
        );

    public static HttpResponseMessage Stream(HttpStatusCode status, Stream body) =>
        new(status) { Content = new StreamContent(body) };
}

/// <summary>A non-seekable stream, so its content has no declared length.</summary>
public sealed class UnknownLengthStream(byte[] bytes) : Stream
{
    private readonly MemoryStream _inner = new(bytes);

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

    public override void Flush() { }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>
/// A body whose first read signals <see cref="ReadStarted"/> and then either waits for cancellation or throws
/// <see cref="ReadFailure"/>, after running <c>beforeFailure</c> when given.
/// </summary>
public sealed class StalledStream(Exception? readFailure = null, Action? beforeFailure = null) : Stream
{
    public TaskCompletionSource ReadStarted { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Exception? ReadFailure { get; } = readFailure;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default
    )
    {
        ReadStarted.TrySetResult();
        if (ReadFailure is not null)
        {
            beforeFailure?.Invoke();
            throw ReadFailure;
        }
        await Task.Delay(Timeout.Infinite, cancellationToken);
        return 0;
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override void Flush() { }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>
/// A non-seekable body that returns <c>bytes</c> and, on reaching its end, runs <c>atEnd</c> once and then ends
/// normally, ignoring any cancellation <c>atEnd</c> caused.
/// </summary>
public sealed class EndOfBodyStream(byte[] bytes, Action atEnd) : Stream
{
    private readonly MemoryStream _inner = new(bytes);
    private bool _ended;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default
    )
    {
        int read = _inner.Read(buffer.Span);
        if (read == 0 && !_ended)
        {
            _ended = true;
            atEnd();
        }
        return ValueTask.FromResult(read);
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override void Flush() { }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>
/// The time of another provider, with timers that fire only when <see cref="FireAll"/> is called. A test can move the
/// clock past a deadline whose timer has not run, or run a timer before the clock reaches its due time.
/// </summary>
public sealed class ManualTimersTimeProvider(TimeProvider clock) : TimeProvider
{
    private readonly ConcurrentQueue<(TimerCallback Callback, object? State)> _timers = new();

    public override DateTimeOffset GetUtcNow() => clock.GetUtcNow();

    public override ITimer CreateTimer(
        TimerCallback callback,
        object? state,
        TimeSpan dueTime,
        TimeSpan period
    )
    {
        _timers.Enqueue((callback, state));
        return new ManualTimer();
    }

    /// <summary>Runs every timer created so far, whatever its due time.</summary>
    public void FireAll()
    {
        foreach ((TimerCallback callback, object? state) in _timers)
        {
            callback(state);
        }
    }

    private sealed class ManualTimer : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period) => true;

        public void Dispose() { }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
