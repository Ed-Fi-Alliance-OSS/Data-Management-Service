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
    private bool _answerOkForCollectionAndRoot;
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
    /// When set, a request whose path (after any configured prefix is stripped) ends with a slash -
    /// the root <c>/</c> or a resource collection such as <c>/Student/</c> - answers 200 instead of
    /// the ordinary known/unknown lookup below.
    /// </summary>
    /// <remarks>
    /// This exists to prove that a UniqueId of <c>"."</c> or <c>".."</c> is rejected without ever
    /// dialing the upstream: URI dot-segment resolution turns a lookup for either value into a
    /// request for the collection or the root, so a validator that skipped that special case would
    /// be answered 200 here and would incorrectly accept the write. Off by default, so no other case
    /// in this stub's ordinary known/unknown contract changes behavior.
    /// </remarks>
    public bool AnswerOkForCollectionAndRoot
    {
        get
        {
            lock (_sync)
            {
                return _answerOkForCollectionAndRoot;
            }
        }
        set
        {
            lock (_sync)
            {
                _answerOkForCollectionAndRoot = value;
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
            string rootAddress = $"http://127.0.0.1:{port}/";
            Uri baseAddress = new(
                normalizedPrefix is null ? rootAddress : $"{rootAddress}{normalizedPrefix}/"
            );
            HttpListener listener = new();
            listener.Prefixes.Add(baseAddress.ToString());

            if (normalizedPrefix is not null)
            {
                // HttpListener only routes a request to Handle when its path matches a registered
                // prefix; with only the prefixed address registered, a request outside it never
                // reaches this listener at all, so it could never appear in RequestPaths. Registering
                // the root prefix too routes such a request here instead, where the path-prefix check
                // below both answers it 404 and logs it.
                listener.Prefixes.Add(rootAddress);
            }

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
            _answerOkForCollectionAndRoot = false;
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
        bool answerOkForCollectionAndRoot;
        lock (_sync)
        {
            responseDelay = _responseDelay;
            answerServerError = _answerServerError;
            redirectMode = _redirectMode;
            answerOkForCollectionAndRoot = _answerOkForCollectionAndRoot;
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

            // Reached for a request outside the prefix only because StartAsync also registered the
            // root prefix with the listener; without that, HttpListener itself would have already
            // rejected the request before Handle ever ran, and it would never have been logged above.
            if (!pathOnly.StartsWith(prefixSegment, StringComparison.Ordinal))
            {
                context.Response.StatusCode = (int)HttpStatusCode.NotFound;
                context.Response.Close();
                return;
            }

            pathOnly = pathOnly[(prefixSegment.Length - 1)..];
        }

        // Checked ahead of the known/unknown lookup below rather than folded into it: a trailing
        // slash means the request landed on a collection or the root rather than on one document, a
        // shape the ordinary lookup below never answers 200 for, since that lookup needs both a
        // resource segment and a document segment to find anything known. Reachable only while a
        // case has opted in, so no other case's request to a genuine resource-and-document path,
        // which never ends in a slash, changes behavior.
        if (answerOkForCollectionAndRoot && pathOnly.EndsWith('/'))
        {
            context.Response.StatusCode = (int)HttpStatusCode.OK;
            context.Response.Close();
            return;
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

/// <summary>
/// A loopback address nothing answers, for tests of the unreachable-upstream case.
/// </summary>
/// <remarks>
/// A prior version of this address picked a free ephemeral port and immediately released it before
/// handing the address back, racing every other process on the machine for that same port until the
/// test finally dialed it - a real listener could win that race and turn an unreachable-upstream case
/// into a reachable one. This holder instead binds a <see cref="Socket"/> to the address and never
/// calls <see cref="Socket.Listen(int)"/> on it, which keeps the port reserved for as long as this
/// instance lives - nothing else can bind it. What a connection attempt then meets depends on the
/// operating system: macOS drops the handshake, so the caller's own request timeout ends it, while
/// Linux refuses it at once. Either way the lookup fails, which is all the unreachable case needs.
/// </remarks>
public sealed class UnreachableAddressHolder : IDisposable
{
    private readonly Socket _socket;
    private bool _disposed;

    private UnreachableAddressHolder(Socket socket, Uri address)
    {
        _socket = socket;
        Address = address;
    }

    /// <summary>The address of the bound-but-not-listening socket.</summary>
    public Uri Address { get; }

    /// <summary>Binds a socket to a loopback ephemeral port without listening on it.</summary>
    public static UnreachableAddressHolder Create()
    {
        Socket socket = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)socket.LocalEndPoint!).Port;
        return new UnreachableAddressHolder(socket, new Uri($"http://127.0.0.1:{port}/"));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _socket.Dispose();
    }
}
