// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using System.Threading.RateLimiting;
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
}
