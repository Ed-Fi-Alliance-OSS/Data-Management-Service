// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Net.Sockets;

namespace EdFi.DataManagementService.Tests.Integration.Plugins;

/// <summary>
/// An in-process stand-in for an external unique-id system, reachable at
/// <c>GET {BaseAddress}/{resourceName}/{escapedUniqueId}</c>.
/// </summary>
/// <remarks>
/// A reference custom validator plugin calls out to a real unique-id service over HTTP. Rather than
/// pull a real dependency into the integration suite, this stub answers the same contract from an
/// <see cref="HttpListener"/> bound to a loopback ephemeral port, the same way
/// <c>DocumentCacheAdminTestConfigurationService</c> stands in for the configuration service in the
/// DocumentCacheAdmin CLI harness.
/// </remarks>
public sealed class UniqueIdServiceStub : IAsyncDisposable
{
    /// <summary>
    /// The fixed path a redirect issued in <see cref="RedirectMode"/> points to, and the only path
    /// that answers 200 while that mode is on.
    /// </summary>
    public const string RedirectTargetPath = "/redirect-target";

    private readonly HttpListener _listener;
    private readonly string? _pathPrefix;
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private readonly Task _serveTask;
    private readonly object _sync = new();
    private readonly HashSet<(string ResourceName, string UniqueId)> _knownIds = [];
    private readonly List<string> _requestPaths = [];
    private bool _answerServerError;
    private bool _redirectMode;
    private TimeSpan _responseDelay = TimeSpan.Zero;
    private bool _disposed;

    private UniqueIdServiceStub(HttpListener listener, Uri baseAddress, string? pathPrefix)
    {
        _listener = listener;
        _pathPrefix = pathPrefix;
        BaseAddress = baseAddress;
        _serveTask = Task.Run(RunAsync);
    }

    /// <summary>The stub's own address, with a trailing slash so relative composition just works.</summary>
    public Uri BaseAddress { get; }

    /// <summary>
    /// <see cref="BaseAddress"/> without its trailing slash, for a test that must configure a
    /// deployment setting the way an operator who forgot the trailing slash would write it.
    /// </summary>
    public string BaseAddressWithoutTrailingSlash => BaseAddress.ToString().TrimEnd('/');

    /// <summary>
    /// The raw, still-escaped request path of every request received so far, in arrival order.
    /// </summary>
    /// <remarks>
    /// Read from <see cref="HttpListenerRequest.RawUrl"/> rather than the decoded
    /// <c>Url.AbsolutePath</c>, so a test can assert that a uniqueId containing a slash, a space, or a
    /// percent sign arrived as one escaped path segment instead of being silently unescaped by the
    /// time it is observed. Logged for every request this stub receives, including one outside its
    /// configured path prefix, so a misrouted request is still observable.
    /// </remarks>
    public IReadOnlyList<string> RequestPaths
    {
        get
        {
            lock (_sync)
            {
                return [.. _requestPaths];
            }
        }
    }

    /// <summary>When set, every request answers 500 regardless of whether the id it names is known.</summary>
    public bool AnswerServerError
    {
        get
        {
            lock (_sync)
            {
                return _answerServerError;
            }
        }
        set
        {
            lock (_sync)
            {
                _answerServerError = value;
            }
        }
    }

    /// <summary>
    /// When set, every request other than <see cref="RedirectTargetPath"/> answers 302 with a
    /// <c>Location</c> header pointing at <see cref="RedirectTargetPath"/>, which itself answers 200.
    /// </summary>
    /// <remarks>
    /// This exists to prove a client does not follow the redirect: the target answers 200 so a client
    /// that did follow it would see what looks like success, and the target is logged the same as any
    /// other request, so a test can assert its path never appears in <see cref="RequestPaths"/>.
    /// </remarks>
    public bool RedirectMode
    {
        get
        {
            lock (_sync)
            {
                return _redirectMode;
            }
        }
        set
        {
            lock (_sync)
            {
                _redirectMode = value;
            }
        }
    }

    /// <summary>
    /// How long the stub waits before answering a request, simulating a slow upstream. Zero by
    /// default, which answers immediately.
    /// </summary>
    public TimeSpan ResponseDelay
    {
        get
        {
            lock (_sync)
            {
                return _responseDelay;
            }
        }
        set
        {
            lock (_sync)
            {
                _responseDelay = value;
            }
        }
    }

    /// <summary>Starts the stub on a loopback ephemeral port.</summary>
    /// <remarks>
    /// The port is chosen by briefly binding a <see cref="TcpListener"/> to port 0 and reading back
    /// what the OS assigned, then releasing it so the <see cref="HttpListener"/> can bind to it.
    /// Another process can in principle claim that same port in the gap between the two binds, so a
    /// start that fails with <see cref="HttpListenerException"/> is retried against a freshly chosen
    /// port rather than surfaced to the caller.
    /// </remarks>
    /// <param name="pathPrefix">
    /// An optional path segment every request must be routed under, for example <c>"uid"</c> to serve
    /// <c>http://127.0.0.1:&lt;port&gt;/uid/</c>. Null serves from the root, the stub's original
    /// behavior.
    /// </param>
    public static Task<UniqueIdServiceStub> StartAsync(string? pathPrefix = null)
    {
        const int maxAttempts = 5;
        string? normalizedPrefix = string.IsNullOrEmpty(pathPrefix) ? null : pathPrefix.Trim('/');

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            int port = GetEphemeralPort();
            Uri baseAddress = new(
                normalizedPrefix is null
                    ? $"http://127.0.0.1:{port}/"
                    : $"http://127.0.0.1:{port}/{normalizedPrefix}/"
            );
            HttpListener listener = new();
            listener.Prefixes.Add(baseAddress.ToString());

            try
            {
                listener.Start();
                return Task.FromResult(
                    new UniqueIdServiceStub(listener, baseAddress, normalizedPrefix)
                );
            }
            catch (HttpListenerException) when (attempt < maxAttempts)
            {
                // Another process claimed the port between the probe above and this listener's own
                // bind; retry against a freshly chosen port.
            }
        }

        // Unreachable: the final attempt above either returns or lets its exception propagate,
        // because its catch filter only matches while attempt < maxAttempts.
        throw new InvalidOperationException(
            $"Could not start {nameof(UniqueIdServiceStub)} after {maxAttempts} attempts."
        );
    }

    /// <summary>A base address for a port nothing listens on, for tests of the unreachable case.</summary>
    public static Uri UnreachableBaseAddress()
    {
        int port = GetEphemeralPort();
        return new Uri($"http://127.0.0.1:{port}/");
    }

    /// <summary>Makes the stub answer 200 for <paramref name="resourceName"/> and <paramref name="uniqueId"/>.</summary>
    public void Know(string resourceName, string uniqueId)
    {
        lock (_sync)
        {
            _knownIds.Add((resourceName, uniqueId));
        }
    }

    /// <summary>Makes the stub answer 404 again for a pair it previously knew.</summary>
    public void Forget(string resourceName, string uniqueId)
    {
        lock (_sync)
        {
            _knownIds.Remove((resourceName, uniqueId));
        }
    }

    /// <summary>
    /// Clears known ids, the request log, and the server-error, redirect, and delay modes, back to a
    /// fresh stub's state. The path prefix is not reset: it is fixed for the stub's lifetime, chosen
    /// once in <see cref="StartAsync"/>.
    /// </summary>
    public void Reset()
    {
        lock (_sync)
        {
            _knownIds.Clear();
            _requestPaths.Clear();
            _answerServerError = false;
            _redirectMode = false;
            _responseDelay = TimeSpan.Zero;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _cancellationTokenSource.CancelAsync();
        _listener.Stop();
        _listener.Close();

        try
        {
            await _serveTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown, including a request Handle abandoned mid-delay.
        }
        catch (ObjectDisposedException)
        {
            // Expected during shutdown.
        }
        finally
        {
            _cancellationTokenSource.Dispose();
        }
    }

    private async Task RunAsync()
    {
        while (!_cancellationTokenSource.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener
                    .GetContextAsync()
                    .WaitAsync(_cancellationTokenSource.Token);
            }
            catch (Exception exception)
                when (exception
                        is OperationCanceledException
                            or HttpListenerException
                            or ObjectDisposedException
                )
            {
                break;
            }

            await Handle(context);
        }
    }

    private async Task Handle(HttpListenerContext context)
    {
        string rawUrl = context.Request.RawUrl ?? string.Empty;

        lock (_sync)
        {
            _requestPaths.Add(rawUrl);
        }

        TimeSpan responseDelay;
        bool answerServerError;
        bool redirectMode;
        lock (_sync)
        {
            responseDelay = _responseDelay;
            answerServerError = _answerServerError;
            redirectMode = _redirectMode;
        }

        if (responseDelay > TimeSpan.Zero)
        {
            try
            {
                await Task.Delay(responseDelay, _cancellationTokenSource.Token);
            }
            catch (OperationCanceledException)
            {
                // The stub is shutting down mid-delay. Letting go of this request without
                // answering it, rather than waiting out the delay, is what keeps DisposeAsync from
                // hanging on it.
                return;
            }
        }

        if (answerServerError)
        {
            context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
            context.Response.Close();
            return;
        }

        string pathOnly = rawUrl.Split('?', 2)[0];

        if (redirectMode)
        {
            if (pathOnly == RedirectTargetPath)
            {
                context.Response.StatusCode = (int)HttpStatusCode.OK;
            }
            else
            {
                context.Response.StatusCode = (int)HttpStatusCode.Found;
                context.Response.Headers.Add("Location", RedirectTargetPath);
            }
            context.Response.Close();
            return;
        }

        if (_pathPrefix is not null)
        {
            string prefixSegment = $"/{_pathPrefix}/";

            if (!pathOnly.StartsWith(prefixSegment, StringComparison.Ordinal))
            {
                context.Response.StatusCode = (int)HttpStatusCode.NotFound;
                context.Response.Close();
                return;
            }

            pathOnly = pathOnly[(prefixSegment.Length - 1)..];
        }

        // The uniqueId can itself contain an escaped '/', so segments are split on the raw, still
        // escaped path rather than on an unescaped one: unescaping first would turn one escaped
        // segment into several. Only the last segment is unescaped, to recover the caller's uniqueId.
        string[] segments = pathOnly.Split('/', StringSplitOptions.RemoveEmptyEntries);

        bool known = false;

        if (segments.Length >= 2)
        {
            string resourceName = segments[^2];
            string uniqueId = Uri.UnescapeDataString(segments[^1]);

            lock (_sync)
            {
                known = _knownIds.Contains((resourceName, uniqueId));
            }
        }

        context.Response.StatusCode = known ? (int)HttpStatusCode.OK : (int)HttpStatusCode.NotFound;
        context.Response.Close();
    }

    private static int GetEphemeralPort()
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
