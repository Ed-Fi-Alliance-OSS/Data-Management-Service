// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.InstanceManagement.Tests.E2E.Management;
using EdFi.InstanceManagement.Tests.E2E.Models;
using FluentAssertions;

namespace EdFi.InstanceManagement.Tests.E2E.UnitTests;

[TestFixture]
[Category("InstanceFixtureUnit")]
public class Given_Dms_Token_Cache
{
    private ManualTimeProvider _time = null!;
    private DmsTokenCache _cache = null!;
    private int _acquisitions;

    [SetUp]
    public void Setup()
    {
        _time = new();
        _acquisitions = 0;
        _cache = new(
            (_, _, _) => Task.FromResult(new TokenResponse($"token-{++_acquisitions}", "bearer", 120)),
            _time
        );
    }

    [Test]
    public async Task It_reuses_a_valid_token_for_the_same_endpoint_and_client()
    {
        var first = await _cache.GetReusableDmsTokenAsync("endpoint", "client", "secret");
        var second = await _cache.GetReusableDmsTokenAsync("endpoint", "client", "secret");

        first.Should().Be("token-1");
        second.Should().Be("token-1");
        _acquisitions.Should().Be(1);
    }

    [Test]
    public async Task It_keeps_different_endpoints_and_clients_separate()
    {
        var first = await _cache.GetReusableDmsTokenAsync("endpoint-1", "client-1", "secret");
        var otherEndpoint = await _cache.GetReusableDmsTokenAsync("endpoint-2", "client-1", "secret");
        var otherClient = await _cache.GetReusableDmsTokenAsync("endpoint-1", "client-2", "secret");
        var repeated = await _cache.GetReusableDmsTokenAsync("endpoint-1", "client-1", "secret");

        first.Should().Be("token-1");
        otherEndpoint.Should().Be("token-2");
        otherClient.Should().Be("token-3");
        repeated.Should().Be("token-1");
        _acquisitions.Should().Be(3);
    }

    [Test]
    public async Task It_refreshes_at_the_conservative_expiry_boundary()
    {
        await _cache.GetReusableDmsTokenAsync("endpoint", "client", "secret");
        _time.Advance(TimeSpan.FromSeconds(89));
        var stillValid = await _cache.GetReusableDmsTokenAsync("endpoint", "client", "secret");
        _time.Advance(TimeSpan.FromSeconds(1));
        var refreshed = await _cache.GetReusableDmsTokenAsync("endpoint", "client", "secret");

        stillValid.Should().Be("token-1");
        refreshed.Should().Be("token-2");
        _acquisitions.Should().Be(2);
    }

    [Test]
    public async Task It_refreshes_a_token_past_its_expiry()
    {
        await _cache.GetReusableDmsTokenAsync("endpoint", "client", "secret");
        _time.Advance(TimeSpan.FromSeconds(121));

        (await _cache.GetReusableDmsTokenAsync("endpoint", "client", "secret")).Should().Be("token-2");
    }

    [Test]
    public async Task It_coalesces_concurrent_refreshes_for_a_stale_key()
    {
        var refresh = new TaskCompletionSource<TokenResponse>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var cache = new DmsTokenCache(
            (_, _, _) =>
                ++_acquisitions is 1
                    ? Task.FromResult(new TokenResponse("original", "bearer", 120))
                    : refresh.Task,
            _time
        );
        await cache.GetReusableDmsTokenAsync("endpoint", "client", "secret");
        _time.Advance(TimeSpan.FromSeconds(90));

        var callers = Enumerable
            .Range(0, 10)
            .Select(_ => cache.GetReusableDmsTokenAsync("endpoint", "client", "secret"))
            .ToArray();
        var acquisitionsBeforeCompletion = _acquisitions;
        refresh.SetResult(new TokenResponse("replacement", "bearer", 120));
        var tokens = await Task.WhenAll(callers);

        acquisitionsBeforeCompletion.Should().Be(2);
        tokens.Should().OnlyContain(token => token == "replacement");
        (await cache.GetReusableDmsTokenAsync("endpoint", "client", "secret")).Should().Be("replacement");
        _acquisitions.Should().Be(2);
    }

    [Test]
    public async Task It_evicts_a_failed_shared_acquisition_so_the_next_call_retries()
    {
        var acquisition = new TaskCompletionSource<TokenResponse>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var cache = new DmsTokenCache(
            (_, _, _) =>
                ++_acquisitions is 1
                    ? acquisition.Task
                    : Task.FromResult(new TokenResponse("retry", "bearer", 120)),
            _time
        );
        var first = cache.GetReusableDmsTokenAsync("endpoint", "client", "secret");
        var second = cache.GetReusableDmsTokenAsync("endpoint", "client", "secret");
        acquisition.SetException(new HttpRequestException("Acquisition failed"));

        var firstResult = async () => await first;
        var secondResult = async () => await second;
        await firstResult.Should().ThrowAsync<HttpRequestException>();
        await secondResult.Should().ThrowAsync<HttpRequestException>();
        (await cache.GetReusableDmsTokenAsync("endpoint", "client", "secret")).Should().Be("retry");
        _acquisitions.Should().Be(2);
    }

    [Test]
    public async Task It_evicts_a_failed_shared_refresh_of_a_stale_token_so_the_next_call_retries()
    {
        var refresh = new TaskCompletionSource<TokenResponse>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var cache = new DmsTokenCache(
            (_, _, _) =>
                ++_acquisitions switch
                {
                    1 => Task.FromResult(new TokenResponse("original", "bearer", 120)),
                    2 => refresh.Task,
                    _ => Task.FromResult(new TokenResponse("retry", "bearer", 120)),
                },
            _time
        );
        (await cache.GetReusableDmsTokenAsync("endpoint", "client", "secret")).Should().Be("original");
        _time.Advance(TimeSpan.FromSeconds(90));

        var callers = Enumerable
            .Range(0, 10)
            .Select(_ => cache.GetReusableDmsTokenAsync("endpoint", "client", "secret"))
            .ToArray();
        var acquisitionsBeforeFailure = _acquisitions;
        refresh.SetException(new HttpRequestException("Refresh failed"));

        foreach (var caller in callers)
        {
            var failure = async () => await caller;
            await failure.Should().ThrowAsync<HttpRequestException>();
        }

        acquisitionsBeforeFailure.Should().Be(2);
        (await cache.GetReusableDmsTokenAsync("endpoint", "client", "secret")).Should().Be("retry");
        _acquisitions.Should().Be(3);
    }

    [TestCase(0)]
    [TestCase(-1)]
    public async Task It_does_not_reuse_a_response_without_a_positive_lifetime(int lifetime)
    {
        var cache = new DmsTokenCache(
            (_, _, _) => Task.FromResult(new TokenResponse($"token-{++_acquisitions}", "bearer", lifetime)),
            _time
        );
        await cache.GetReusableDmsTokenAsync("endpoint", "client", "secret");

        (await cache.GetReusableDmsTokenAsync("endpoint", "client", "secret")).Should().Be("token-2");
    }

    [Test]
    public async Task It_keeps_short_lived_tokens_reusable_before_the_refresh_skew()
    {
        var cache = new DmsTokenCache(
            (_, _, _) => Task.FromResult(new TokenResponse($"token-{++_acquisitions}", "bearer", 10)),
            _time
        );
        await cache.GetReusableDmsTokenAsync("endpoint", "client", "secret");
        _time.Advance(TimeSpan.FromSeconds(4));
        var stillValid = await cache.GetReusableDmsTokenAsync("endpoint", "client", "secret");
        _time.Advance(TimeSpan.FromSeconds(1));
        var refreshed = await cache.GetReusableDmsTokenAsync("endpoint", "client", "secret");

        stillValid.Should().Be("token-1");
        refreshed.Should().Be("token-2");
    }

    [Test]
    public async Task It_counts_acquisition_time_against_the_reusable_lifetime()
    {
        var acquisition = new TaskCompletionSource<TokenResponse>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var cache = new DmsTokenCache(
            (_, _, _) =>
                ++_acquisitions is 1
                    ? acquisition.Task
                    : Task.FromResult(new TokenResponse("replacement", "bearer", 120)),
            _time
        );
        var first = cache.GetReusableDmsTokenAsync("endpoint", "client", "secret");
        _time.Advance(TimeSpan.FromSeconds(100));
        acquisition.SetResult(new TokenResponse("original", "bearer", 120));
        await first;

        (await cache.GetReusableDmsTokenAsync("endpoint", "client", "secret")).Should().Be("replacement");
    }

    [Test]
    public async Task It_rejects_a_response_missing_its_access_token()
    {
        var cache = new DmsTokenCache(
            (_, _, _) => Task.FromResult(new TokenResponse(null!, "bearer", 120)),
            _time
        );
        var act = () => cache.GetReusableDmsTokenAsync("endpoint", "client", "secret");

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 2, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan elapsed) => _now += elapsed;
    }
}
