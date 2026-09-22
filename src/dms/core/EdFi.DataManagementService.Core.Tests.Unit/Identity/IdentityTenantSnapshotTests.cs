// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.Identity;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Identity;

/// <summary>
/// Pins <see cref="IdentityTenantSnapshot.CheckAsync" /> (design.md D4, design.md:565-583, story
/// acceptance A4-A7 and A9): the 60-second freshness window, the single shared refresh for cold or
/// expired callers, the 5-second failure cooldown, the 30-second refresh budget linked to
/// <see cref="TimeProvider.CreateTimer" /> and <see cref="IHostApplicationLifetime.ApplicationStopping" />,
/// and the datastore independence of the tenant lookup. Every scenario runs on a
/// <see cref="FakeTimeProvider" /> so no test sleeps for the windows themselves; the handful of
/// interleaving waits between two concurrent callers use a short real-time delay, matching sibling
/// concurrency fixtures such as <c>CachedApplicationContextProviderTests</c>.
/// </summary>
[TestFixture]
public class IdentityTenantSnapshotTests
{
    private static IHostApplicationLifetime CreateLifetime(CancellationToken stoppingToken = default)
    {
        var lifetime = A.Fake<IHostApplicationLifetime>();
        A.CallTo(() => lifetime.ApplicationStopping).Returns(stoppingToken);
        return lifetime;
    }

    private static IdentityTenantSnapshot CreateSnapshot(
        IDataStoreProvider dataStoreProvider,
        TimeProvider timeProvider,
        IHostApplicationLifetime? lifetime = null
    ) =>
        new(
            dataStoreProvider,
            timeProvider,
            lifetime ?? CreateLifetime(),
            NullLogger<IdentityTenantSnapshot>.Instance
        );

    private static IDataStoreProvider CreateDataStoreProvider() => A.Fake<IDataStoreProvider>();

    [TestFixture]
    public class Given_A_Fresh_Snapshot : IdentityTenantSnapshotTests
    {
        [Test]
        public async Task Different_unknown_names_make_zero_further_calls()
        {
            var timeProvider = new FakeTimeProvider();
            IDataStoreProvider dataStoreProvider = CreateDataStoreProvider();
            A.CallTo(() => dataStoreProvider.LoadTenants(A<CancellationToken>._))
                .Returns(Task.FromResult<IList<string>>(["North"]));
            var snapshot = CreateSnapshot(dataStoreProvider, timeProvider);

            (await snapshot.CheckAsync("North", CancellationToken.None))
                .Should()
                .Be(TenantExistenceOutcome.Exists);

            (await snapshot.CheckAsync("South", CancellationToken.None))
                .Should()
                .Be(TenantExistenceOutcome.Absent);
            (await snapshot.CheckAsync("East", CancellationToken.None))
                .Should()
                .Be(TenantExistenceOutcome.Absent);

            A.CallTo(() => dataStoreProvider.LoadTenants(A<CancellationToken>._))
                .MustHaveHappenedOnceExactly();
        }

        [Test]
        public async Task An_empty_list_is_a_fresh_snapshot_where_every_name_is_absent()
        {
            var timeProvider = new FakeTimeProvider();
            IDataStoreProvider dataStoreProvider = CreateDataStoreProvider();
            A.CallTo(() => dataStoreProvider.LoadTenants(A<CancellationToken>._))
                .Returns(Task.FromResult<IList<string>>([]));
            var snapshot = CreateSnapshot(dataStoreProvider, timeProvider);

            (await snapshot.CheckAsync("North", CancellationToken.None))
                .Should()
                .Be(TenantExistenceOutcome.Absent);
            (await snapshot.CheckAsync("South", CancellationToken.None))
                .Should()
                .Be(TenantExistenceOutcome.Absent);

            A.CallTo(() => dataStoreProvider.LoadTenants(A<CancellationToken>._))
                .MustHaveHappenedOnceExactly();
        }
    }

    [TestFixture]
    public class Given_A_Cold_Or_Expired_Snapshot : IdentityTenantSnapshotTests
    {
        [Test]
        public async Task Two_concurrent_cold_callers_make_exactly_one_LoadTenants_call()
        {
            var timeProvider = new FakeTimeProvider();
            var gate = new TaskCompletionSource<IList<string>>();
            IDataStoreProvider dataStoreProvider = CreateDataStoreProvider();
            A.CallTo(() => dataStoreProvider.LoadTenants(A<CancellationToken>._))
                .ReturnsLazily(_ => gate.Task);
            var snapshot = CreateSnapshot(dataStoreProvider, timeProvider);

            Task<TenantExistenceOutcome> first = snapshot.CheckAsync("North", CancellationToken.None);
            Task<TenantExistenceOutcome> second = snapshot.CheckAsync("South", CancellationToken.None);
            await Task.Delay(50);

            gate.SetResult(["North"]);

            (await first).Should().Be(TenantExistenceOutcome.Exists);
            (await second).Should().Be(TenantExistenceOutcome.Absent);
            A.CallTo(() => dataStoreProvider.LoadTenants(A<CancellationToken>._))
                .MustHaveHappenedOnceExactly();
        }

        [Test]
        public async Task Expiry_at_60_seconds_triggers_one_refresh()
        {
            var timeProvider = new FakeTimeProvider();
            IDataStoreProvider dataStoreProvider = CreateDataStoreProvider();
            A.CallTo(() => dataStoreProvider.LoadTenants(A<CancellationToken>._))
                .Returns(Task.FromResult<IList<string>>(["North"]));
            var snapshot = CreateSnapshot(dataStoreProvider, timeProvider);

            await snapshot.CheckAsync("North", CancellationToken.None);
            timeProvider.Advance(TimeSpan.FromSeconds(60));
            await snapshot.CheckAsync("North", CancellationToken.None);

            A.CallTo(() => dataStoreProvider.LoadTenants(A<CancellationToken>._))
                .MustHaveHappenedTwiceExactly();
        }
    }

    [TestFixture]
    public class Given_A_Failed_Refresh : IdentityTenantSnapshotTests
    {
        [Test]
        public async Task An_InvalidOperationException_from_LoadTenants_is_Unavailable()
        {
            var timeProvider = new FakeTimeProvider();
            IDataStoreProvider dataStoreProvider = CreateDataStoreProvider();
            A.CallTo(() => dataStoreProvider.LoadTenants(A<CancellationToken>._))
                .ThrowsAsync(new InvalidOperationException("Configuration Service unavailable"));
            var snapshot = CreateSnapshot(dataStoreProvider, timeProvider);

            TenantExistenceOutcome outcome = await snapshot.CheckAsync("North", CancellationToken.None);

            outcome.Should().Be(TenantExistenceOutcome.Unavailable);
        }

        [Test]
        public async Task A_failure_yields_Unavailable_to_every_waiter_and_no_second_call_inside_the_cooldown()
        {
            var timeProvider = new FakeTimeProvider();
            IDataStoreProvider dataStoreProvider = CreateDataStoreProvider();
            A.CallTo(() => dataStoreProvider.LoadTenants(A<CancellationToken>._))
                .ThrowsAsync(new InvalidOperationException("boom"));
            var snapshot = CreateSnapshot(dataStoreProvider, timeProvider);

            Task<TenantExistenceOutcome> first = snapshot.CheckAsync("North", CancellationToken.None);
            Task<TenantExistenceOutcome> second = snapshot.CheckAsync("South", CancellationToken.None);

            (await first).Should().Be(TenantExistenceOutcome.Unavailable);
            (await second).Should().Be(TenantExistenceOutcome.Unavailable);

            (await snapshot.CheckAsync("East", CancellationToken.None))
                .Should()
                .Be(TenantExistenceOutcome.Unavailable);

            A.CallTo(() => dataStoreProvider.LoadTenants(A<CancellationToken>._))
                .MustHaveHappenedOnceExactly();
        }

        [Test]
        public async Task A_call_after_the_cooldown_elapses_starts_a_new_refresh()
        {
            var timeProvider = new FakeTimeProvider();
            IDataStoreProvider dataStoreProvider = CreateDataStoreProvider();
            A.CallTo(() => dataStoreProvider.LoadTenants(A<CancellationToken>._))
                .ThrowsAsync(new InvalidOperationException("boom"))
                .Once()
                .Then.Returns(Task.FromResult<IList<string>>(["North"]));
            var snapshot = CreateSnapshot(dataStoreProvider, timeProvider);

            (await snapshot.CheckAsync("North", CancellationToken.None))
                .Should()
                .Be(TenantExistenceOutcome.Unavailable);

            timeProvider.Advance(TimeSpan.FromSeconds(5));

            (await snapshot.CheckAsync("North", CancellationToken.None))
                .Should()
                .Be(TenantExistenceOutcome.Exists);

            A.CallTo(() => dataStoreProvider.LoadTenants(A<CancellationToken>._))
                .MustHaveHappenedTwiceExactly();
        }
    }

    [TestFixture]
    public class Given_Cancellation : IdentityTenantSnapshotTests
    {
        [Test]
        public async Task A_pre_cancelled_request_token_throws_OperationCanceledException()
        {
            var timeProvider = new FakeTimeProvider();
            IDataStoreProvider dataStoreProvider = CreateDataStoreProvider();
            var snapshot = CreateSnapshot(dataStoreProvider, timeProvider);
            using var cancellationTokenSource = new CancellationTokenSource();
            await cancellationTokenSource.CancelAsync();

            Func<Task> act = () => snapshot.CheckAsync("North", cancellationTokenSource.Token);

            await act.Should().ThrowAsync<OperationCanceledException>();
            A.CallTo(() => dataStoreProvider.LoadTenants(A<CancellationToken>._)).MustNotHaveHappened();
        }

        [Test]
        public async Task Cancelling_one_waiter_completes_promptly_while_the_other_still_receives_Exists()
        {
            var timeProvider = new FakeTimeProvider();
            var gate = new TaskCompletionSource<IList<string>>();
            IDataStoreProvider dataStoreProvider = CreateDataStoreProvider();
            A.CallTo(() => dataStoreProvider.LoadTenants(A<CancellationToken>._))
                .ReturnsLazily(_ => gate.Task);
            var snapshot = CreateSnapshot(dataStoreProvider, timeProvider);

            using var cancelledCallerCts = new CancellationTokenSource();
            Task<TenantExistenceOutcome> cancelledCallerTask = snapshot.CheckAsync(
                "North",
                cancelledCallerCts.Token
            );
            Task<TenantExistenceOutcome> liveCallerTask = snapshot.CheckAsync(
                "North",
                CancellationToken.None
            );
            await Task.Delay(50);

            var stopwatch = Stopwatch.StartNew();
            await cancelledCallerCts.CancelAsync();

            Func<Task> act = async () => await cancelledCallerTask;
            await act.Should().ThrowAsync<OperationCanceledException>();
            stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1));

            // The cancelled caller leaving did not abort the refresh the live caller still needs.
            liveCallerTask.IsCompleted.Should().BeFalse();

            gate.SetResult(["North"]);

            (await liveCallerTask.WaitAsync(TimeSpan.FromSeconds(2)))
                .Should()
                .Be(TenantExistenceOutcome.Exists);
        }

        [Test]
        public async Task ApplicationStopping_ends_an_in_flight_refresh()
        {
            var timeProvider = new FakeTimeProvider();
            var gate = new TaskCompletionSource<IList<string>>();
            IDataStoreProvider dataStoreProvider = CreateDataStoreProvider();
            A.CallTo(() => dataStoreProvider.LoadTenants(A<CancellationToken>._))
                .ReturnsLazily(_ => gate.Task);
            using var stoppingCts = new CancellationTokenSource();
            IHostApplicationLifetime lifetime = CreateLifetime(stoppingCts.Token);
            var snapshot = CreateSnapshot(dataStoreProvider, timeProvider, lifetime);

            Task<TenantExistenceOutcome> checkTask = snapshot.CheckAsync("North", CancellationToken.None);
            await Task.Delay(50);

            await stoppingCts.CancelAsync();

            (await checkTask.WaitAsync(TimeSpan.FromSeconds(2)))
                .Should()
                .Be(TenantExistenceOutcome.Unavailable);
        }
    }

    [TestFixture]
    public class Given_A_Refresh_That_Exceeds_Its_Budget : IdentityTenantSnapshotTests
    {
        [Test]
        public async Task A_refresh_exceeding_its_30_second_budget_yields_Unavailable()
        {
            var timeProvider = new FakeTimeProvider();
            var gate = new TaskCompletionSource<IList<string>>();
            IDataStoreProvider dataStoreProvider = CreateDataStoreProvider();
            // Deliberately ignores the cancellation token DMS passes it, so the assertion proves the
            // snapshot's own linked timeout - scheduled through TimeProvider.CreateTimer - is what
            // bounds the call, not cooperative cancellation inside LoadTenants.
            A.CallTo(() => dataStoreProvider.LoadTenants(A<CancellationToken>._))
                .ReturnsLazily(_ => gate.Task);
            var snapshot = CreateSnapshot(dataStoreProvider, timeProvider);

            Task<TenantExistenceOutcome> checkTask = snapshot.CheckAsync("North", CancellationToken.None);
            await Task.Delay(50);

            timeProvider.Advance(TimeSpan.FromSeconds(30));

            (await checkTask.WaitAsync(TimeSpan.FromSeconds(2)))
                .Should()
                .Be(TenantExistenceOutcome.Unavailable);
        }
    }

    [TestFixture]
    public class Given_The_Datastore_Independence_Requirement : IdentityTenantSnapshotTests
    {
        [Test]
        public async Task LoadDataStores_and_connection_string_decryption_are_never_touched()
        {
            var timeProvider = new FakeTimeProvider();
            IDataStoreProvider dataStoreProvider = CreateDataStoreProvider();
            A.CallTo(() => dataStoreProvider.LoadTenants(A<CancellationToken>._))
                .Returns(Task.FromResult<IList<string>>(["North"]));
            A.CallTo(() => dataStoreProvider.LoadDataStores(A<string?>._, A<CancellationToken>._))
                .Throws(new InvalidOperationException("LoadDataStores must not be called"));
            var decryptionService = A.Fake<IConnectionStringDecryptionService>();
            A.CallTo(decryptionService).Throws(new InvalidOperationException("must not be called"));
            var snapshot = CreateSnapshot(dataStoreProvider, timeProvider);

            (await snapshot.CheckAsync("North", CancellationToken.None))
                .Should()
                .Be(TenantExistenceOutcome.Exists);

            A.CallTo(() => dataStoreProvider.LoadDataStores(A<string?>._, A<CancellationToken>._))
                .MustNotHaveHappened();
            A.CallTo(decryptionService).MustNotHaveHappened();
        }
    }
}
