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
    bool HasCookie
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

    public TaskCompletionSource RequestReceived { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public IReadOnlyList<RecordedRequest> Requests => [.. _requests];

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

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken
    )
    {
        _requests.Enqueue(
            new RecordedRequest(
                request.Method,
                request.RequestUri!,
                request.Headers.Accept.ToString(),
                request.Headers.Authorization is not null,
                request.Headers.Contains("Cookie")
            )
        );
        RequestReceived.TrySetResult();
        return respond(request, cancellationToken);
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
/// <see cref="ReadFailure"/>.
/// </summary>
public sealed class StalledStream(Exception? readFailure = null) : Stream
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
