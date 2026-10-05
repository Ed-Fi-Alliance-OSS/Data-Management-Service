// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Specialized;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Web;

namespace EdFi.DataManagementService.Tests.Integration.Plugins.Identity;

/// <summary>
/// A loopback stand-in for the control channel the identity fixture plugin reaches over HTTP.
/// </summary>
/// <remarks>
/// A loaded plugin lives in its own load context, so a test cannot share statics with it. The
/// control channels are configuration and this stub, following <see cref="UniqueIdServiceStub"/>.
/// It serves the endpoints the fixture README defines: <c>GET policy</c> answers whether a client is
/// granted a namespace, <c>GET jobs/expiry</c> answers whether a job has been expired, and anything
/// else answers 404. <c>GET jobs/state</c> answers whether a job has failed terminally and whether its
/// next poll must fail transiently; <c>POST events</c> records what the provider reports doing. A
/// test steers the answers with <see cref="Grant"/>, <see cref="Revoke"/>,
/// <see cref="FailPolicySource"/>, <see cref="ExpireJob"/>, <see cref="FailJob"/> and
/// <see cref="FailNextPoll"/>, reads <see cref="Events"/>, and waits for a report with
/// <see cref="WaitForEventAsync"/>. The raw request line of every request except an event report is
/// logged, so a test can show that a denied request caused no further lookup.
/// </remarks>
internal sealed class IdentityFixtureControlStub : IAsyncDisposable
{
    private readonly HttpListener _listener;
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private readonly Task _serveTask;
    private readonly object _sync = new();
    private readonly List<string> _requestPaths = [];
    private readonly HashSet<(string ClientId, string? Tenant, string Namespace)> _grants = [];
    private readonly HashSet<string> _expiredJobs = new(StringComparer.Ordinal);
    private readonly HashSet<string> _failedJobs = new(StringComparer.Ordinal);
    private readonly HashSet<string> _failNextPoll = new(StringComparer.Ordinal);
    private readonly List<IdentityFixtureEvent> _events = [];
    private readonly List<(string Operation, string Kind, TaskCompletionSource Reported)> _eventWaiters = [];
    private bool _policySourceFailing;
    private bool _disposed;

    private IdentityFixtureControlStub(HttpListener listener, Uri baseAddress)
    {
        _listener = listener;
        BaseAddress = baseAddress;
        _serveTask = Task.Run(RunAsync);
    }

    /// <summary>The stub's own address, with a trailing slash so relative composition just works.</summary>
    public Uri BaseAddress { get; }

    /// <summary>The raw request path and query of every request received so far, in arrival order.</summary>
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

    /// <summary>Starts the stub on a loopback ephemeral port, retrying when another process takes the port first.</summary>
    public static Task<IdentityFixtureControlStub> StartAsync()
    {
        const int maxAttempts = 5;

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            Uri baseAddress = new($"http://127.0.0.1:{GetEphemeralPort()}/");
            HttpListener listener = new();
            listener.Prefixes.Add(baseAddress.ToString());

            try
            {
                listener.Start();
                return Task.FromResult(new IdentityFixtureControlStub(listener, baseAddress));
            }
            catch (HttpListenerException) when (attempt < maxAttempts)
            {
                // Another process claimed the port between the probe and this bind; retry on a new one.
            }
        }

        // Unreachable: the final attempt either returns or lets its exception propagate.
        throw new InvalidOperationException(
            $"Could not start {nameof(IdentityFixtureControlStub)} after {maxAttempts} attempts."
        );
    }

    /// <summary>
    /// Answers granted for the client on the namespace within the tenant. A client id of <c>*</c> is
    /// the explicit tenant-wide grant: it answers granted for every client in that tenant and namespace.
    /// </summary>
    public void Grant(string clientId, string? tenant, string namespaceName)
    {
        lock (_sync)
        {
            _grants.Add((clientId, tenant?.ToUpperInvariant(), namespaceName));
        }
    }

    /// <summary>Withdraws a grant given by <see cref="Grant"/>; the policy endpoint then answers denied.</summary>
    public void Revoke(string clientId, string? tenant, string namespaceName)
    {
        lock (_sync)
        {
            _grants.Remove((clientId, tenant?.ToUpperInvariant(), namespaceName));
        }
    }

    /// <summary>Makes the policy endpoint answer 503 while <paramref name="failing"/> is true.</summary>
    public void FailPolicySource(bool failing = true)
    {
        lock (_sync)
        {
            _policySourceFailing = failing;
        }
    }

    /// <summary>Makes the job-expiry endpoint answer expired for the token.</summary>
    public void ExpireJob(string token)
    {
        lock (_sync)
        {
            _expiredJobs.Add(token);
        }
    }

    /// <summary>Makes the job-state endpoint answer failed for the token on every read, until <see cref="Reset"/>.</summary>
    public void FailJob(string token)
    {
        lock (_sync)
        {
            _failedJobs.Add(token);
        }
    }

    /// <summary>
    /// Makes the next read of the token's job state answer that the poll must fail; later reads answer
    /// normally.
    /// </summary>
    public void FailNextPoll(string token)
    {
        lock (_sync)
        {
            _failNextPoll.Add(token);
        }
    }

    /// <summary>Every operation invocation, issuance and job creation the provider reported, in arrival order.</summary>
    public IReadOnlyList<IdentityFixtureEvent> Events
    {
        get
        {
            lock (_sync)
            {
                return [.. _events];
            }
        }
    }

    /// <summary>
    /// Completes once the provider has reported the operation and kind, whether the report arrived
    /// before this call or arrives after it, so a test can wait for a known point inside an operation
    /// rather than for time. It completes when the stub records the report, which is before the
    /// provider's report call returns.
    /// </summary>
    public Task WaitForEventAsync(string operation, string kind)
    {
        lock (_sync)
        {
            if (_events.Exists(reported => reported.Operation == operation && reported.Kind == kind))
            {
                return Task.CompletedTask;
            }

            TaskCompletionSource reported = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _eventWaiters.Add((operation, kind, reported));
            return reported.Task;
        }
    }

    /// <summary>The request token of every results invocation the provider reported, in arrival order.</summary>
    public IReadOnlyList<string> ReceivedResultTokens =>
        [
            .. Events
                .Where(reported => reported is { Operation: "results", Kind: "invocation", Token: not null })
                .Select(reported => reported.Token!),
        ];

    /// <summary>The number of invocations reported for the operation (<c>create</c>, <c>getById</c>, <c>find</c>, <c>search</c> or <c>results</c>), or all of them when null.</summary>
    public int InvocationCount(string? operation = null) =>
        Events.Count(reported =>
            reported.Kind == "invocation" && (operation is null || reported.Operation == operation)
        );

    /// <summary>The number of authorized lookups reported for the operation, or all of them when null. A denied call reports an invocation and no lookup.</summary>
    public int LookupCount(string? operation = null) =>
        Events.Count(reported =>
            reported.Kind == "lookup" && (operation is null || reported.Operation == operation)
        );

    /// <summary>The number of persons issued, as reported by the provider.</summary>
    public int IssuanceCount => Events.Count(reported => reported.Kind == "issuance");

    /// <summary>The number of async jobs created, as reported by the provider.</summary>
    public int JobCount => Events.Count(reported => reported.Kind == "job");

    /// <summary>The number of policy lookups received so far.</summary>
    public int PolicyLookupCount
    {
        get
        {
            lock (_sync)
            {
                return _requestPaths.Count(path => path.StartsWith("/policy?", StringComparison.Ordinal));
            }
        }
    }

    /// <summary>Clears the request log, every grant, the failure switch and every expiry, back to a fresh stub's state.</summary>
    public void Reset()
    {
        lock (_sync)
        {
            _requestPaths.Clear();
            _grants.Clear();
            _expiredJobs.Clear();
            _failedJobs.Clear();
            _failNextPoll.Clear();
            _events.Clear();
            _policySourceFailing = false;
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
        catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException)
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
                context = await _listener.GetContextAsync().WaitAsync(_cancellationTokenSource.Token);
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

            Answer(context);
        }
    }

    private void Answer(HttpListenerContext context)
    {
        string path = context.Request.Url?.AbsolutePath ?? string.Empty;
        NameValueCollection query = HttpUtility.ParseQueryString(context.Request.Url?.Query ?? string.Empty);
        int status = (int)HttpStatusCode.OK;
        string? body = null;

        lock (_sync)
        {
            if (path == "/events")
            {
                RecordEvent(context);
            }
            else
            {
                _requestPaths.Add(context.Request.RawUrl ?? string.Empty);
            }

            if (path == "/events")
            {
                // Recorded above; the provider needs no body back.
            }
            else if (path == "/policy")
            {
                string clientId = query["clientId"] ?? string.Empty;
                string? tenant = query["tenant"]?.ToUpperInvariant();
                string namespaceName = query["namespace"] ?? string.Empty;

                if (_policySourceFailing)
                {
                    status = (int)HttpStatusCode.ServiceUnavailable;
                }
                else
                {
                    bool granted =
                        _grants.Contains((clientId, tenant, namespaceName))
                        || _grants.Contains(("*", tenant, namespaceName));
                    body = new JsonObject { ["granted"] = granted }.ToJsonString();
                }
            }
            else if (path == "/jobs/expiry")
            {
                body = new JsonObject
                {
                    ["expired"] = _expiredJobs.Contains(query["token"] ?? string.Empty),
                }.ToJsonString();
            }
            else if (path == "/jobs/state")
            {
                string token = query["token"] ?? string.Empty;
                body = new JsonObject
                {
                    ["failed"] = _failedJobs.Contains(token),
                    // Consumed by this read, so only the next poll fails.
                    ["failNextPoll"] = _failNextPoll.Remove(token),
                }.ToJsonString();
            }
            else
            {
                status = (int)HttpStatusCode.NotFound;
            }
        }

        context.Response.StatusCode = status;
        if (body is not null)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(body);
            context.Response.ContentType = "application/json";
            context.Response.OutputStream.Write(bytes, 0, bytes.Length);
        }
        context.Response.Close();
    }

    // Called with the lock held.
    private void RecordEvent(HttpListenerContext context)
    {
        using StreamReader reader = new(context.Request.InputStream, Encoding.UTF8);
        JsonNode? body = JsonNode.Parse(reader.ReadToEnd());
        IdentityFixtureEvent reported = new(
            body?["operation"]?.GetValue<string>() ?? string.Empty,
            body?["kind"]?.GetValue<string>() ?? string.Empty,
            body?["token"]?.GetValue<string>()
        );

        _events.Add(reported);

        for (int index = _eventWaiters.Count - 1; index >= 0; index--)
        {
            (string operation, string kind, TaskCompletionSource waiter) = _eventWaiters[index];
            if (operation == reported.Operation && kind == reported.Kind)
            {
                // Its continuations run asynchronously, so none of them runs under the lock.
                waiter.TrySetResult();
                _eventWaiters.RemoveAt(index);
            }
        }
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
/// One thing the identity fixture reported doing: an operation <c>invocation</c> (before its grant was
/// checked), a <c>lookup</c> (after the grant check passed), an <c>issuance</c> of a UniqueId, a
/// <c>job</c> creation, or an <c>awaiting-cancellation</c> wait. A results invocation carries the
/// request token the provider received.
/// </summary>
internal sealed record IdentityFixtureEvent(string Operation, string Kind, string? Token);
