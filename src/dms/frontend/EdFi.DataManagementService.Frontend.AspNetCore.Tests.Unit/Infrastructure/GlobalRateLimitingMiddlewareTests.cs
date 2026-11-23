// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text.Json.Nodes;
using System.Threading.RateLimiting;
using EdFi.DataManagementService.Frontend.AspNetCore.Configuration;
using EdFi.DataManagementService.Frontend.AspNetCore.Infrastructure;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using NUnit.Framework;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit.Infrastructure;

public class GlobalRateLimitingMiddlewareTests
{
    /// <summary>
    /// A limiter that answers each acquisition from a script, in order, and counts the calls. It has
    /// no timer and no window, so no outcome below depends on timing.
    /// </summary>
    private sealed class ScriptedLimiter(params Func<CancellationToken, ValueTask<RateLimitLease>>[] attempts)
        : PartitionedRateLimiter<HttpContext>
    {
        private int _acquireCalls;

        public int AcquireCalls => _acquireCalls;

        public override RateLimiterStatistics? GetStatistics(HttpContext resource) => null;

        protected override RateLimitLease AttemptAcquireCore(HttpContext resource, int permitCount) =>
            throw new NotSupportedException("The middleware acquires asynchronously.");

        protected override ValueTask<RateLimitLease> AcquireAsyncCore(
            HttpContext resource,
            int permitCount,
            CancellationToken cancellationToken
        )
        {
            int call = Interlocked.Increment(ref _acquireCalls) - 1;
            return attempts[call](cancellationToken);
        }
    }

    private sealed class TestLease(bool isAcquired) : RateLimitLease
    {
        public override bool IsAcquired => isAcquired;

        public override IEnumerable<string> MetadataNames => [];

        public override bool TryGetMetadata(string metadataName, out object? metadata)
        {
            metadata = null;
            return false;
        }
    }

    private static Func<CancellationToken, ValueTask<RateLimitLease>> Grants() =>
        _ => ValueTask.FromResult<RateLimitLease>(new TestLease(isAcquired: true));

    /// <summary>What the framework's partitioned limiter throws when a partition was disposed under it.</summary>
    private static Func<CancellationToken, ValueTask<RateLimitLease>> ThrowsDisposed() =>
        _ =>
            ValueTask.FromException<RateLimitLease>(
                new ObjectDisposedException(nameof(FixedWindowRateLimiter))
            );

    private sealed record Run(DefaultHttpContext Context, ScriptedLimiter Limiter, int NextCalls);

    private static async Task<Run> InvokeAsync(
        ScriptedLimiter limiter,
        RequestDelegate? next = null,
        CancellationToken requestAborted = default
    )
    {
        DefaultHttpContext context = RateLimitRejectionTestContext.CreateHttpContext();
        context.RequestAborted = requestAborted;
        int nextCalls = 0;

        GlobalRateLimitingMiddleware middleware = new(
            httpContext =>
            {
                nextCalls++;
                return next?.Invoke(httpContext) ?? Task.CompletedTask;
            },
            limiter
        );

        await middleware.InvokeAsync(context);
        return new Run(context, limiter, nextCalls);
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Partition_Disposed_Under_The_First_Acquisition : GlobalRateLimitingMiddlewareTests
    {
        private static Func<CancellationToken, ValueTask<RateLimitLease>> Rejects() =>
            _ => ValueTask.FromResult<RateLimitLease>(new TestLease(isAcquired: false));

        [Test]
        public async Task It_admits_the_request_when_the_retry_is_granted()
        {
            Run run = await InvokeAsync(new ScriptedLimiter(ThrowsDisposed(), Grants()));

            run.NextCalls.Should().Be(1);
            run.Limiter.AcquireCalls.Should().Be(2);
            run.Context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        }

        [Test]
        public async Task It_serves_the_normal_rejection_when_the_retry_is_rejected()
        {
            Run run = await InvokeAsync(new ScriptedLimiter(ThrowsDisposed(), Rejects()));

            run.NextCalls.Should().Be(0);
            run.Limiter.AcquireCalls.Should().Be(2);
            run.Context.Response.StatusCode.Should().Be(StatusCodes.Status429TooManyRequests);
            JsonNode body = await RateLimitRejectionTestContext.ReadBody(run.Context);
            body["type"]!.GetValue<string>().Should().Be("urn:ed-fi:api:too-many-requests");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Disposed_Failure_On_Both_Acquisitions : GlobalRateLimitingMiddlewareTests
    {
        [Test]
        public async Task It_propagates_the_second_failure_after_exactly_two_attempts()
        {
            ScriptedLimiter limiter = new(ThrowsDisposed(), ThrowsDisposed(), Grants());
            int nextCalls = 0;
            GlobalRateLimitingMiddleware middleware = new(
                _ =>
                {
                    nextCalls++;
                    return Task.CompletedTask;
                },
                limiter
            );

            Func<Task> invoke = () =>
                middleware.InvokeAsync(RateLimitRejectionTestContext.CreateHttpContext());

            await invoke.Should().ThrowAsync<ObjectDisposedException>();
            limiter.AcquireCalls.Should().Be(2);
            nextCalls.Should().Be(0);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Disposed_Failure_Thrown_Downstream : GlobalRateLimitingMiddlewareTests
    {
        [Test]
        public async Task It_propagates_without_acquiring_again()
        {
            ScriptedLimiter limiter = new(Grants(), Grants());

            Func<Task> invoke = () =>
                InvokeAsync(limiter, next: _ => throw new ObjectDisposedException("downstream"));

            await invoke.Should().ThrowAsync<ObjectDisposedException>().WithMessage("*downstream*");
            limiter.AcquireCalls.Should().Be(1);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Request_Aborted_During_The_Retry : GlobalRateLimitingMiddlewareTests
    {
        [Test]
        public async Task It_returns_without_admitting_or_writing_a_response()
        {
            using CancellationTokenSource requestAborted = new();
            ScriptedLimiter limiter = new(
                ThrowsDisposed(),
                cancellationToken =>
                {
                    // The client goes away while the retry waits for a permit.
                    requestAborted.Cancel();
                    return ValueTask.FromCanceled<RateLimitLease>(cancellationToken);
                }
            );

            Run run = await InvokeAsync(limiter, requestAborted: requestAborted.Token);

            run.NextCalls.Should().Be(0);
            run.Limiter.AcquireCalls.Should().Be(2);
            run.Context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
            run.Context.Response.Body.Length.Should().Be(0);
        }
    }

    /// <summary>
    /// Runs the middleware over the production limiter with one permit and a queue of one per
    /// partition. The sixty-second window never elapses during a test, so a second request from an
    /// address waits in that address's queue until it is cancelled, and every step below is gated on
    /// task state rather than on time. Teardown cancels every request still waiting and disposes the
    /// limiter.
    /// </summary>
    [TestFixture]
    [Parallelizable]
    public class Given_The_Production_Limiter_With_A_Queue_Per_Partition : GlobalRateLimitingMiddlewareTests
    {
        private static readonly IPAddress _addressA = IPAddress.Parse("10.0.0.1");
        private static readonly IPAddress _addressB = IPAddress.Parse("10.0.0.2");

        private sealed record PendingRequest(
            DefaultHttpContext Context,
            CancellationTokenSource RequestAborted,
            Task Invocation
        );

        private PartitionedRateLimiter<HttpContext> _limiter = null!;
        private GlobalRateLimitingMiddleware _middleware = null!;
        private readonly List<HttpContext> _admitted = [];
        private readonly List<PendingRequest> _requests = [];

        [SetUp]
        public void SetUp()
        {
            _limiter = WebApplicationBuilderExtensions.CreateGlobalLimiter(
                new RateLimitOptions
                {
                    PermitLimit = 1,
                    QueueLimit = 1,
                    Window = 60,
                }
            );
            _admitted.Clear();
            _requests.Clear();
            _middleware = new GlobalRateLimitingMiddleware(
                context =>
                {
                    lock (_admitted)
                    {
                        _admitted.Add(context);
                    }

                    return Task.CompletedTask;
                },
                _limiter
            );
        }

        [TearDown]
        public async Task TearDown()
        {
            foreach (PendingRequest request in _requests)
            {
                await request.RequestAborted.CancelAsync();
            }

            try
            {
                await Task.WhenAll(_requests.Select(request => request.Invocation))
                    .WaitAsync(TimeSpan.FromSeconds(30));
            }
            finally
            {
                await _limiter.DisposeAsync();
                foreach (PendingRequest request in _requests)
                {
                    request.RequestAborted.Dispose();
                }
            }
        }

        private PendingRequest Send(IPAddress address)
        {
            DefaultHttpContext context = RateLimitRejectionTestContext.CreateHttpContext();
            context.Connection.RemoteIpAddress = address;
            CancellationTokenSource requestAborted = new();
            context.RequestAborted = requestAborted.Token;

            PendingRequest request = new(context, requestAborted, _middleware.InvokeAsync(context));
            _requests.Add(request);
            return request;
        }

        private bool WasAdmitted(PendingRequest request)
        {
            lock (_admitted)
            {
                return _admitted.Contains(request.Context);
            }
        }

        private async Task<PendingRequest> SendCompletedAsync(IPAddress address)
        {
            PendingRequest request = Send(address);
            request
                .Invocation.IsCompleted.Should()
                .BeTrue("a request that is not queued is answered at once");
            await request.Invocation;
            return request;
        }

        private PendingRequest SendQueued(IPAddress address)
        {
            PendingRequest request = Send(address);
            request.Invocation.IsCompleted.Should().BeFalse("the request waits in its partition's queue");
            return request;
        }

        [Test]
        public async Task It_admits_another_address_while_one_address_has_a_full_queue()
        {
            PendingRequest a1 = await SendCompletedAsync(_addressA);
            PendingRequest a2 = SendQueued(_addressA);
            PendingRequest a3 = await SendCompletedAsync(_addressA);

            PendingRequest b1 = await SendCompletedAsync(_addressB);

            WasAdmitted(a1).Should().BeTrue();
            a3.Context.Response.StatusCode.Should().Be(StatusCodes.Status429TooManyRequests);
            WasAdmitted(a3).Should().BeFalse();
            WasAdmitted(b1).Should().BeTrue();
            b1.Context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
            a2.Invocation.IsCompleted.Should().BeFalse();
        }

        [Test]
        public async Task It_returns_silently_when_a_queued_request_is_aborted_and_frees_its_queue_slot()
        {
            await SendCompletedAsync(_addressA);
            PendingRequest a2 = SendQueued(_addressA);

            await a2.RequestAborted.CancelAsync();
            await a2.Invocation;

            WasAdmitted(a2).Should().BeFalse();
            a2.Context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
            a2.Context.Response.Body.Length.Should().Be(0);

            SendQueued(_addressA);
            PendingRequest a5 = await SendCompletedAsync(_addressA);
            a5.Context.Response.StatusCode.Should().Be(StatusCodes.Status429TooManyRequests);
        }
    }
}
