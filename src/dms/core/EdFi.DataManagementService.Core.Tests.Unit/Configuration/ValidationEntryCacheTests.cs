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
