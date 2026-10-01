// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.Services;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.Services;

[TestFixture]
public class SecretValueCacheTests
{
    private const int ExpirationSeconds = 300;

    private FakeTimeProvider _time = null!;
    private SecretValueCache _cache = null!;
    private readonly List<(string? Tenant, string Name)> _fetches = [];

    private static SecretValueCache CreateCache(int expirationSeconds, TimeProvider timeProvider) =>
        new(Options.Create(new SecretsOptions { CacheExpirationSeconds = expirationSeconds }), timeProvider);

    /// <summary>
    /// Reads through the cache with a fetch that records the key it was asked for and returns a value
    /// naming the fetch, so a test can tell a cached value from a refetched one.
    /// </summary>
    private Task<string> Read(string? tenant, string name) =>
        _cache.GetOrFetchAsync(
            tenant,
            name,
            () =>
            {
                _fetches.Add((tenant, name));
                return Task.FromResult($"{tenant}/{name}#{_fetches.Count}");
            }
        );

    [SetUp]
    public void CreateDefaultCache()
    {
        _fetches.Clear();
        _time = new FakeTimeProvider();
        _cache = CreateCache(ExpirationSeconds, _time);
    }

    [TestFixture]
    public class Given_two_reads_inside_the_window : SecretValueCacheTests
    {
        private string _first = null!;
        private string _second = null!;

        [SetUp]
        public async Task Act()
        {
            _first = await Read("tenant", "name");
            _time.Advance(TimeSpan.FromSeconds(ExpirationSeconds - 1));
            _second = await Read("tenant", "name");
        }

        [Test]
        public void It_fetches_once() => _fetches.Should().ContainSingle();

        [Test]
        public void It_returns_the_cached_value() => _second.Should().Be(_first);
    }

    [TestFixture]
    public class Given_a_read_after_the_window : SecretValueCacheTests
    {
        private string _second = null!;

        [SetUp]
        public async Task Act()
        {
            await Read("tenant", "name");
            _time.Advance(TimeSpan.FromSeconds(ExpirationSeconds));
            _second = await Read("tenant", "name");
        }

        [Test]
        public void It_fetches_again() => _fetches.Should().HaveCount(2);

        [Test]
        public void It_returns_the_new_value() => _second.Should().Be("tenant/name#2");
    }

    /// <summary>
    /// Reads inside the window do not extend it, so a value read just before it expires is still
    /// fetched again once the window measured from the fetch has passed.
    /// </summary>
    [TestFixture]
    public class Given_reads_spread_across_the_window : SecretValueCacheTests
    {
        [SetUp]
        public async Task Act()
        {
            await Read("tenant", "name");
            _time.Advance(TimeSpan.FromSeconds(200));
            await Read("tenant", "name");
            _time.Advance(TimeSpan.FromSeconds(ExpirationSeconds - 200));
            await Read("tenant", "name");
        }

        [Test]
        public void It_expires_from_the_fetch_and_not_from_the_last_read() => _fetches.Should().HaveCount(2);
    }

    [TestFixture]
    public class Given_a_value_fetched_late_in_a_slow_fetch : SecretValueCacheTests
    {
        [SetUp]
        public async Task Act()
        {
            // The window starts when the value arrives, not when it was asked for.
            await _cache.GetOrFetchAsync(
                "tenant",
                "name",
                () =>
                {
                    _fetches.Add(("tenant", "name"));
                    _time.Advance(TimeSpan.FromSeconds(60));
                    return Task.FromResult("value");
                }
            );
            _time.Advance(TimeSpan.FromSeconds(ExpirationSeconds - 1));
            await Read("tenant", "name");
        }

        [Test]
        public void It_measures_the_window_from_completion() => _fetches.Should().ContainSingle();
    }

    [TestFixture]
    public class Given_keys_that_differ_only_by_tenant : SecretValueCacheTests
    {
        private string[] _values = [];

        [SetUp]
        public async Task Act()
        {
            _values =
            [
                await Read("district-a", "name"),
                await Read("district-b", "name"),
                await Read(null, "name"),
                await Read("", "name"),
                await Read("District-A", "name"),
                await Read("district-a", "name"),
                await Read("district-b", "name"),
                await Read(null, "name"),
            ];
        }

        [Test]
        public void It_keeps_one_entry_per_tenant() =>
            _fetches
                .Should()
                .Equal(
                    ("district-a", "name"),
                    ("district-b", "name"),
                    (null, "name"),
                    ("", "name"),
                    ("District-A", "name")
                );

        [Test]
        public void It_returns_each_tenant_its_own_value() =>
            _values[5..].Should().Equal("district-a/name#1", "district-b/name#2", "/name#3");
    }

    [TestFixture]
    public class Given_keys_that_differ_only_by_name_case : SecretValueCacheTests
    {
        [SetUp]
        public async Task Act()
        {
            await Read("tenant", "name");
            await Read("tenant", "Name");
        }

        [Test]
        public void It_matches_names_case_sensitively() => _fetches.Should().HaveCount(2);
    }

    [TestFixture]
    public class Given_a_tenant_added_after_others_were_cached : SecretValueCacheTests
    {
        private string _value = null!;

        [SetUp]
        public async Task Act()
        {
            await Read("existing", "name");
            _value = await Read("added", "name");
            await Read("added", "name");
        }

        [Test]
        public void It_populates_on_the_first_read() => _value.Should().Be("added/name#2");

        [Test]
        public void It_serves_the_second_read_from_the_cache() => _fetches.Should().HaveCount(2);
    }

    /// <summary>
    /// Tenant administration does not tell the cache a tenant was removed. Its entries are reachable
    /// only by its own name, and once expired they are released the next time any value is stored.
    /// </summary>
    [TestFixture]
    public class Given_a_tenant_that_is_no_longer_read : SecretValueCacheTests
    {
        private int _countBeforeExpiry;
        private int _countAfterExpiry;

        [SetUp]
        public async Task Act()
        {
            await Read("removed", "a");
            await Read("removed", "b");
            await Read("kept", "a");
            _countBeforeExpiry = _cache.Count;

            _time.Advance(TimeSpan.FromSeconds(ExpirationSeconds));
            await Read("kept", "a");
            _countAfterExpiry = _cache.Count;
        }

        [Test]
        public void It_does_not_share_its_entries_with_another_tenant() =>
            _fetches.Should().Contain(("kept", "a"));

        [Test]
        public void It_holds_every_entry_inside_the_window() => _countBeforeExpiry.Should().Be(3);

        [Test]
        public void It_releases_the_expired_entries() => _countAfterExpiry.Should().Be(1);
    }

    [TestFixture]
    public class Given_caching_is_disabled : SecretValueCacheTests
    {
        [SetUp]
        public async Task Act()
        {
            _cache = CreateCache(0, _time);
            await Read("tenant", "name");
            await Read("tenant", "name");
            await Read("tenant", "name");
        }

        [Test]
        public void It_fetches_on_every_read() => _fetches.Should().HaveCount(3);

        [Test]
        public void It_holds_nothing() => _cache.Count.Should().Be(0);
    }

    [TestFixture]
    public class Given_a_fetch_returning_an_empty_value : SecretValueCacheTests
    {
        private string _value = null!;

        [SetUp]
        public async Task Act()
        {
            _value = await _cache.GetOrFetchAsync("tenant", "name", () => Task.FromResult(string.Empty));
            await Read("tenant", "name");
        }

        [Test]
        public void It_returns_it_to_the_caller() => _value.Should().BeEmpty();

        [Test]
        public void It_does_not_cache_it() => _fetches.Should().ContainSingle();
    }

    [TestFixture]
    public class Given_concurrent_misses_on_one_key : SecretValueCacheTests
    {
        private TaskCompletionSource<string> _gate = null!;
        private int _calls;
        private Task<string>[] _readers = [];
        private bool _anyCompletedBeforeRelease;
        private int _callsBeforeRelease;

        [SetUp]
        public async Task Act()
        {
            _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _calls = 0;
            _readers =
            [
                .. Enumerable
                    .Range(0, 8)
                    .Select(_ =>
                        _cache.GetOrFetchAsync(
                            "tenant",
                            "name",
                            () =>
                            {
                                Interlocked.Increment(ref _calls);
                                return _gate.Task;
                            }
                        )
                    ),
            ];
            _anyCompletedBeforeRelease = Array.Exists(_readers, reader => reader.IsCompleted);
            _callsBeforeRelease = _calls;

            _gate.SetResult("shared");
            await Task.WhenAll(_readers);
        }

        [Test]
        public void It_calls_the_fetch_once() => _callsBeforeRelease.Should().Be(1);

        [Test]
        public void It_keeps_every_waiter_waiting_on_that_call() =>
            _anyCompletedBeforeRelease.Should().BeFalse();

        [Test]
        public void It_gives_every_waiter_the_result() =>
            _readers.Select(reader => reader.Result).Should().AllBe("shared");

        [Test]
        public void It_does_not_call_again_after_the_call_completes() => _calls.Should().Be(1);
    }

    [TestFixture]
    public class Given_one_key_is_blocked_on_its_fetch : SecretValueCacheTests
    {
        private TaskCompletionSource<string> _gate = null!;
        private string _other = null!;
        private bool _blockedCompletedFirst;

        [SetUp]
        public async Task Act()
        {
            _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Task<string> blocked = _cache.GetOrFetchAsync("tenant", "slow", () => _gate.Task);
            _other = await Read("tenant", "fast");
            _blockedCompletedFirst = blocked.IsCompleted;

            _gate.SetResult("slow-value");
            await blocked;
        }

        [Test]
        public void It_completes_the_other_key_without_waiting() => _other.Should().Be("tenant/fast#1");

        [Test]
        public void It_leaves_the_blocked_key_waiting() => _blockedCompletedFirst.Should().BeFalse();
    }

    [TestFixture]
    public class Given_a_shared_fetch_that_fails : SecretValueCacheTests
    {
        private TaskCompletionSource<string> _gate = null!;
        private readonly InvalidOperationException _failure = new("store unavailable");
        private int _calls;
        private Task<string>[] _readers = [];
        private int _countAfterFailure;
        private string _retried = null!;

        [SetUp]
        public async Task Act()
        {
            _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _calls = 0;
            _readers =
            [
                .. Enumerable
                    .Range(0, 4)
                    .Select(_ =>
                        _cache.GetOrFetchAsync(
                            "tenant",
                            "name",
                            () =>
                            {
                                Interlocked.Increment(ref _calls);
                                return _gate.Task;
                            }
                        )
                    ),
            ];

            _gate.SetException(_failure);

            try
            {
                await Task.WhenAll(_readers);
            }
            catch (InvalidOperationException)
            {
                // Every reader carries the shared failure; each is asserted below.
            }

            _countAfterFailure = _cache.Count;
            _retried = await Read("tenant", "name");
        }

        [Test]
        public void It_calls_the_fetch_once_for_every_waiter() => _calls.Should().Be(1);

        [Test]
        public void It_fails_every_waiter_with_the_fetch_failure() =>
            _readers
                .Select(reader => reader.Exception?.InnerException)
                .Should()
                .AllSatisfy(exception => exception.Should().BeSameAs(_failure));

        [Test]
        public void It_caches_nothing() => _countAfterFailure.Should().Be(0);

        [Test]
        public void It_fetches_again_on_the_next_read() => _retried.Should().Be("tenant/name#1");
    }

    [TestFixture]
    public class Given_concurrent_misses_with_caching_disabled : SecretValueCacheTests
    {
        private TaskCompletionSource<string> _gate = null!;
        private int _calls;

        [SetUp]
        public async Task Act()
        {
            _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _calls = 0;
            _cache = CreateCache(0, _time);
            Task<string>[] readers =
            [
                .. Enumerable
                    .Range(0, 4)
                    .Select(_ =>
                        _cache.GetOrFetchAsync(
                            "tenant",
                            "name",
                            () =>
                            {
                                Interlocked.Increment(ref _calls);
                                return _gate.Task;
                            }
                        )
                    ),
            ];
            _gate.SetResult("value");
            await Task.WhenAll(readers);
            await Read("tenant", "name");
        }

        [Test]
        public void It_still_shares_the_call_in_flight() => _calls.Should().Be(1);

        [Test]
        public void It_fetches_again_once_that_call_completed() => _fetches.Should().ContainSingle();
    }
}
