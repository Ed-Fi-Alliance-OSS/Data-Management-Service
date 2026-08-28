// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Configuration;
using FluentAssertions;
using NUnit.Framework;
using static EdFi.DataManagementService.Core.Tests.Unit.Configuration.ValidationCacheSupport;

namespace EdFi.DataManagementService.Core.Tests.Unit.Configuration;

/// <summary>
/// The caching machinery both validation providers share. These fixtures exercise it directly, because
/// the races they cover - a superseded entry faulting late, an expired entry being replaced while
/// another reader holds the old one - are properties of the machinery rather than of either provider.
/// </summary>
[TestFixture]
[Parallelizable]
public class ValidationEntryCacheTests
{
    private static readonly TimeSpan _expiration = TimeSpan.FromSeconds(600);

    private static ValidationEntryCache<string> CacheOf(
        ControlledTimeProvider timeProvider,
        Func<ValidationCacheKey, Exception, bool>? shouldRetainFault = null
    ) => new(timeProvider, _expiration, shouldRetainFault ?? ((_, _) => false));

    [TestFixture]
    [Parallelizable]
    public class Given_Two_Keys_With_Byte_Identical_Text : ValidationEntryCacheTests
    {
        /// <summary>
        /// The reason the policy class is in the key at all. A parent and a derivative may be
        /// configured with the same text - a replica reachable at the same address, a snapshot pointed
        /// back at its source - and giving them one entry would give one of them the other's lifetime.
        /// </summary>
        [TestCase(true)]
        [TestCase(false)]
        public void It_produces_once_for_each_policy_class_in_either_order(bool primaryFirst)
        {
            ControlledTimeProvider time = new(Start);
            var cache = CacheOf(time);
            int productions = 0;

            Task<string> Produce()
            {
                productions++;
                return Task.FromResult("value");
            }

            ValidationCacheKey first = primaryFirst ? PrimaryKey() : DerivativeKey();
            ValidationCacheKey second = primaryFirst ? DerivativeKey() : PrimaryKey();

            cache.Read(first, Produce);
            cache.Read(second, Produce);

            productions.Should().Be(2);
        }

        [Test]
        public async Task It_gives_each_policy_class_its_own_value()
        {
            ControlledTimeProvider time = new(Start);
            var cache = CacheOf(time);

            string primary = await cache.Read(PrimaryKey(), () => Task.FromResult("primary")).Value;
            string derivative = await cache.Read(DerivativeKey(), () => Task.FromResult("derivative")).Value;

            primary.Should().Be("primary");
            derivative.Should().Be("derivative");
        }

        /// <summary>
        /// The lifetimes differ, which is what the shared entry would have destroyed: after the
        /// derivative expires the primary is still the original value.
        /// </summary>
        [Test]
        public async Task It_expires_only_the_derivative()
        {
            ControlledTimeProvider time = new(Start);
            var cache = CacheOf(time);

            await cache.Read(PrimaryKey(), () => Task.FromResult("primary first")).Value;
            await cache.Read(DerivativeKey(), () => Task.FromResult("derivative first")).Value;

            time.Advance(_expiration);

            string primary = await cache.Read(PrimaryKey(), () => Task.FromResult("primary second")).Value;
            string derivative = await cache
                .Read(DerivativeKey(), () => Task.FromResult("derivative second"))
                .Value;

            primary.Should().Be("primary first");
            derivative.Should().Be("derivative second");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Derivative_Entry_And_The_Clock : ValidationEntryCacheTests
    {
        [Test]
        public async Task It_keeps_the_entry_until_the_expiration_elapses()
        {
            ControlledTimeProvider time = new(Start);
            var cache = CacheOf(time);

            await cache.Read(DerivativeKey(), () => Task.FromResult("first")).Value;
            time.Advance(_expiration - TimeSpan.FromSeconds(1));

            string second = await cache.Read(DerivativeKey(), () => Task.FromResult("second")).Value;

            second.Should().Be("first");
        }

        /// <summary>
        /// Exactly at the boundary, not only past it, so a verdict cannot live one tick longer than
        /// the configured expiration.
        /// </summary>
        [Test]
        public async Task It_replaces_the_entry_once_the_expiration_is_reached()
        {
            ControlledTimeProvider time = new(Start);
            var cache = CacheOf(time);

            await cache.Read(DerivativeKey(), () => Task.FromResult("first")).Value;
            time.Advance(_expiration);

            string second = await cache.Read(DerivativeKey(), () => Task.FromResult("second")).Value;

            second.Should().Be("second");
        }

        /// <summary>
        /// A reader that is still holding an expired entry must not remove the replacement another
        /// reader installed. This is the race the exact-version removal exists for.
        /// </summary>
        [Test]
        public async Task It_survives_a_stale_reader_invalidating_after_the_replacement_exists()
        {
            ControlledTimeProvider time = new(Start);
            var cache = CacheOf(time);

            var staleRead = cache.Read(DerivativeKey(), () => Task.FromResult("first"));
            await staleRead.Value;

            time.Advance(_expiration);
            await cache.Read(DerivativeKey(), () => Task.FromResult("second")).Value;

            // The stale reader concludes the database is unusable and invalidates - too late.
            staleRead.Token.Invalidate();

            string current = await cache.Read(DerivativeKey(), () => Task.FromResult("third")).Value;
            current.Should().Be("second");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Production_That_Faults : ValidationEntryCacheTests
    {
        [Test]
        public async Task It_evicts_so_the_next_read_retries()
        {
            ControlledTimeProvider time = new(Start);
            var cache = CacheOf(time);

            Exception? thrown = await CatchAsync(
                cache.Read(DerivativeKey(), () => Task.FromException<string>(new TimeoutException())).Value
            );

            thrown.Should().BeOfType<TimeoutException>();

            string second = await cache.Read(DerivativeKey(), () => Task.FromResult("recovered")).Value;
            second.Should().Be("recovered");
        }

        /// <summary>
        /// A producer that throws before returning a task must behave like one that returns a faulted
        /// task: faulting the awaited task, evicting, and preserving the original exception. Letting it
        /// escape Lazy.Value would cache the exception inside the Lazy, where no eviction can reach it.
        /// </summary>
        [Test]
        public async Task It_treats_a_synchronous_throw_as_a_faulted_task()
        {
            ControlledTimeProvider time = new(Start);
            var cache = CacheOf(time);
            InvalidOperationException expected = new("thrown before returning a task");

            ValidationCacheRead<string> read = cache.Read(DerivativeKey(), () => throw expected);

            Exception? thrown = await CatchAsync(read.Value);

            thrown.Should().BeSameAs(expected);

            string second = await cache.Read(DerivativeKey(), () => Task.FromResult("recovered")).Value;
            second.Should().Be("recovered");
        }

        [Test]
        public async Task It_rethrows_the_original_exception_unchanged()
        {
            ControlledTimeProvider time = new(Start);
            var cache = CacheOf(time);
            TimeoutException expected = new("the original");

            Exception? thrown = await CatchAsync(
                cache.Read(DerivativeKey(), () => Task.FromException<string>(expected)).Value
            );

            thrown.Should().BeSameAs(expected);
        }

        [Test]
        public async Task It_retains_a_fault_the_policy_says_to_keep()
        {
            ControlledTimeProvider time = new(Start);
            var cache = CacheOf(time, shouldRetainFault: (_, exception) => exception is TimeoutException);

            await CatchAsync(
                cache.Read(PrimaryKey(), () => Task.FromException<string>(new TimeoutException())).Value
            );

            Exception? second = await CatchAsync(
                cache.Read(PrimaryKey(), () => Task.FromResult("would have recovered")).Value
            );

            second.Should().BeOfType<TimeoutException>();
        }

        /// <summary>
        /// The other half of the exact-version rule: a retained faulted entry that is later superseded
        /// must not remove its replacement, and a late fault from a superseded entry must not either.
        /// </summary>
        [Test]
        public async Task It_does_not_let_a_late_fault_remove_the_replacement()
        {
            ControlledTimeProvider time = new(Start);
            var cache = CacheOf(time);

            TaskCompletionSource<string> slowProduction = new();
            int productions = 0;

            Task<string> Replacement()
            {
                productions++;
                return Task.FromResult("replacement");
            }

            ValidationCacheRead<string> slowRead = cache.Read(DerivativeKey(), () => slowProduction.Task);

            // The slow entry expires and is replaced while its production is still in flight.
            time.Advance(_expiration);
            await cache.Read(DerivativeKey(), Replacement).Value;

            // Only now does the superseded production fail. Its eviction names the entry that faulted,
            // which is no longer the current one, so it must remove nothing.
            slowProduction.SetException(new TimeoutException());
            await CatchAsync(slowRead.Value);

            string current = await cache.Read(DerivativeKey(), Replacement).Value;

            current.Should().Be("replacement");
            productions.Should().Be(1, "the replacement must survive the superseded entry's eviction");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Concurrent_First_Readers : ValidationEntryCacheTests
    {
        /// <summary>
        /// One production for all of them: the cost this cache exists to avoid is the database round
        /// trip, and a burst of first requests for the same derivative must pay it once.
        /// </summary>
        [Test]
        public async Task It_produces_exactly_once()
        {
            ControlledTimeProvider time = new(Start);
            var cache = CacheOf(time);
            TaskCompletionSource<string> gate = new();
            int productions = 0;

            Task<string> Produce()
            {
                Interlocked.Increment(ref productions);
                return gate.Task;
            }

            // Two stages on purpose: every reader must reach Read before the production completes, so
            // the assertion is about concurrent first readers rather than about later cache hits.
            Task<Task<string>>[] reads =
            [
                .. Enumerable
                    .Range(0, 16)
                    .Select(_ => Task.Run<Task<string>>(() => cache.Read(DerivativeKey(), Produce).Value)),
            ];

            Task<string>[] values = await Task.WhenAll(reads);
            gate.SetResult("value");
            string[] results = await Task.WhenAll(values);

            productions.Should().Be(1);
            results.Should().AllBe("value");
        }
    }

    /// <summary>
    /// The two interleavings where a removal could reach past the entry that issued it. Both are
    /// driven by TaskCompletionSource rather than by timing, so what they assert is the ordering and
    /// not the scheduler.
    /// </summary>
    [TestFixture]
    [Parallelizable]
    public class Given_An_Entry_Superseded_While_Still_In_Flight : ValidationEntryCacheTests
    {
        /// <summary>
        /// A reader takes a derivative entry, decides the database is unusable and invalidates it while
        /// its own production is still running; a second reader installs a replacement that completes;
        /// only then does the first production fail. The first reader must still see its own exception,
        /// and its late fault must not remove the replacement it never observed.
        /// </summary>
        [Test]
        public async Task It_keeps_the_replacement_when_the_superseded_production_faults_late()
        {
            ControlledTimeProvider time = new(Start);
            var cache = CacheOf(time);

            TaskCompletionSource<string> inFlight = new();
            int replacementProductions = 0;

            Task<string> Replacement()
            {
                replacementProductions++;
                return Task.FromResult("B");
            }

            // A is taken and its production is still running.
            ValidationCacheRead<string> readA = cache.Read(DerivativeKey(), () => inFlight.Task);

            // A's reader concludes the database is unusable and drops A before A has produced.
            readA.Token.Invalidate();

            // B is installed and completes while A is still in flight.
            string b = await cache.Read(DerivativeKey(), Replacement).Value;
            b.Should().Be("B");

            // Only now does A fail.
            InvalidOperationException expected = new("A failed after being superseded");
            inFlight.SetException(expected);
            Exception? seenByA = await CatchAsync(readA.Value);

            // A's own awaiter sees A's own exception, unchanged.
            seenByA.Should().BeSameAs(expected);

            // B is still current, and reading again neither produces C nor re-produces B.
            string current = await cache.Read(DerivativeKey(), Replacement).Value;
            current.Should().Be("B");
            replacementProductions.Should().Be(1, "A's late fault must not have removed B");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Two_Readers_Racing_Through_The_Expiry_Boundary : ValidationEntryCacheTests
    {
        /// <summary>
        /// A clock that pauses one caller inside a chosen reading, so a test can hold a reader between
        /// the moment it decides an entry has expired and the moment it removes it. Nothing in
        /// production is instrumented: this is the injected clock, blocking on the way out.
        /// </summary>
        private sealed class GatedTimeProvider(DateTimeOffset start, int gateOnReading) : TimeProvider
        {
            private readonly ManualResetEventSlim _release = new(initialState: false);
            private readonly ManualResetEventSlim _gateEntered = new(initialState: false);
            private DateTimeOffset _now = start;
            private int _readings;

            public void Advance(TimeSpan amount) => _now += amount;

            public void WaitUntilGateEntered() => _gateEntered.Wait(TimeSpan.FromSeconds(10));

            public void Release() => _release.Set();

            public override DateTimeOffset GetUtcNow()
            {
                if (Interlocked.Increment(ref _readings) == gateOnReading)
                {
                    _gateEntered.Set();
                    _release.Wait(TimeSpan.FromSeconds(10));
                }

                return _now;
            }
        }

        /// <summary>
        /// Both readers observe the same expired entry and both try to remove it. Exactly one
        /// replacement must exist afterwards, produced once: a removal that named the key rather than
        /// the expired entry it observed would let the paused reader delete the other reader's
        /// replacement and install a third.
        /// </summary>
        /// <remarks>
        /// The gate fires on the fourth clock reading, which is the expiry check of the paused
        /// reader's first pass - two readings populate the original entry, and the paused reader's own
        /// pass takes one for the entry timestamp before the one that decides expiry. That holds it
        /// between deciding and removing, which is the only window where the two removals can collide.
        /// </remarks>
        [Test]
        public async Task It_produces_exactly_one_replacement()
        {
            GatedTimeProvider time = new(Start, gateOnReading: 4);
            ValidationEntryCache<string> cache = new(time, _expiration, (_, _) => false);

            await cache.Read(DerivativeKey(), () => Task.FromResult("expired")).Value;
            time.Advance(_expiration);

            int productions = 0;

            Task<string> Produce()
            {
                int ordinal = Interlocked.Increment(ref productions);
                return Task.FromResult($"replacement {ordinal}");
            }

            // The paused reader stops inside its expiry check, having observed the expired entry.
            Task<string> paused = Task.Run(async () => await cache.Read(DerivativeKey(), Produce).Value);
            time.WaitUntilGateEntered();

            // The other reader runs to completion while the first is held, replacing the expired entry.
            string byOther = await cache.Read(DerivativeKey(), Produce).Value;

            // Now the paused reader resumes and performs its own removal.
            time.Release();
            string byPaused = await paused;

            byPaused.Should().Be(byOther, "both readers must end up with the same replacement");

            string current = await cache.Read(DerivativeKey(), Produce).Value;
            current.Should().Be(byOther, "the replacement must still be the cached entry");
            productions.Should().Be(1, "the expired entry must be replaced exactly once");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Token : ValidationEntryCacheTests
    {
        [Test]
        public async Task It_is_a_no_op_for_a_primary_key()
        {
            ControlledTimeProvider time = new(Start);
            var cache = CacheOf(time);

            ValidationCacheRead<string> read = cache.Read(PrimaryKey(), () => Task.FromResult("first"));
            await read.Value;

            read.Token.Invalidate();

            string second = await cache.Read(PrimaryKey(), () => Task.FromResult("second")).Value;
            second.Should().Be("first");
        }

        [Test]
        public async Task It_removes_the_observed_entry_for_a_derivative_key()
        {
            ControlledTimeProvider time = new(Start);
            var cache = CacheOf(time);

            ValidationCacheRead<string> read = cache.Read(DerivativeKey(), () => Task.FromResult("first"));
            await read.Value;

            read.Token.Invalidate();

            string second = await cache.Read(DerivativeKey(), () => Task.FromResult("second")).Value;
            second.Should().Be("second");
        }

        /// <summary>
        /// Invalidating twice must not reach past the entry it was issued for.
        /// </summary>
        [Test]
        public async Task It_is_superseded_once_the_entry_it_names_is_gone()
        {
            ControlledTimeProvider time = new(Start);
            var cache = CacheOf(time);

            ValidationCacheRead<string> read = cache.Read(DerivativeKey(), () => Task.FromResult("first"));
            await read.Value;

            read.Token.Invalidate();
            await cache.Read(DerivativeKey(), () => Task.FromResult("second")).Value;
            read.Token.Invalidate();

            string third = await cache.Read(DerivativeKey(), () => Task.FromResult("third")).Value;
            third.Should().Be("second");
        }
    }
}
