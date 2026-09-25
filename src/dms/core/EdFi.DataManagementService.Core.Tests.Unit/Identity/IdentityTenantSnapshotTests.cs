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
    public class Given_A_Fresh_Snapshot_Answering_Multiple_Names : IdentityTenantSnapshotTests
    {
        private IDataStoreProvider _dataStoreProvider = null!;
        private TenantExistenceOutcome _northOutcome;
        private TenantExistenceOutcome _southOutcome;
        private TenantExistenceOutcome _eastOutcome;

        [SetUp]
        public async Task Setup()
        {
            var timeProvider = new FakeTimeProvider();
            _dataStoreProvider = CreateDataStoreProvider();
            A.CallTo(() => _dataStoreProvider.LoadTenants(A<CancellationToken>._))
                .Returns(Task.FromResult<IList<string>>(["North"]));
            var snapshot = CreateSnapshot(_dataStoreProvider, timeProvider);

            _northOutcome = await snapshot.CheckAsync("North", CancellationToken.None);
            _southOutcome = await snapshot.CheckAsync("South", CancellationToken.None);
            _eastOutcome = await snapshot.CheckAsync("East", CancellationToken.None);
        }

        [Test]
        public void It_answers_Exists_for_the_known_tenant()
        {
            _northOutcome.Should().Be(TenantExistenceOutcome.Exists);
        }

        [Test]
        public void It_answers_Absent_for_the_first_unknown_tenant()
        {
            _southOutcome.Should().Be(TenantExistenceOutcome.Absent);
        }

        [Test]
        public void It_answers_Absent_for_the_second_unknown_tenant()
        {
            _eastOutcome.Should().Be(TenantExistenceOutcome.Absent);
        }

        [Test]
        public void It_calls_LoadTenants_exactly_once()
        {
            A.CallTo(() => _dataStoreProvider.LoadTenants(A<CancellationToken>._))
                .MustHaveHappenedOnceExactly();
        }
    }

    [TestFixture]
    public class Given_A_Fresh_Snapshot_With_An_Empty_Tenant_List : IdentityTenantSnapshotTests
    {
        private IDataStoreProvider _dataStoreProvider = null!;
        private TenantExistenceOutcome _northOutcome;
        private TenantExistenceOutcome _southOutcome;

        [SetUp]
        public async Task Setup()
        {
            var timeProvider = new FakeTimeProvider();
            _dataStoreProvider = CreateDataStoreProvider();
            A.CallTo(() => _dataStoreProvider.LoadTenants(A<CancellationToken>._))
                .Returns(Task.FromResult<IList<string>>([]));
            var snapshot = CreateSnapshot(_dataStoreProvider, timeProvider);

            _northOutcome = await snapshot.CheckAsync("North", CancellationToken.None);
            _southOutcome = await snapshot.CheckAsync("South", CancellationToken.None);
        }

        [Test]
        public void It_answers_Absent_for_the_first_name()
        {
            _northOutcome.Should().Be(TenantExistenceOutcome.Absent);
        }

        [Test]
        public void It_answers_Absent_for_the_second_name()
        {
            _southOutcome.Should().Be(TenantExistenceOutcome.Absent);
        }

        [Test]
        public void It_calls_LoadTenants_exactly_once()
        {
            A.CallTo(() => _dataStoreProvider.LoadTenants(A<CancellationToken>._))
                .MustHaveHappenedOnceExactly();
        }
    }

    [TestFixture]
    public class Given_Two_Concurrent_Cold_Callers : IdentityTenantSnapshotTests
    {
        private IDataStoreProvider _dataStoreProvider = null!;
        private TenantExistenceOutcome _firstOutcome;
        private TenantExistenceOutcome _secondOutcome;

        [SetUp]
        public async Task Setup()
        {
            var timeProvider = new FakeTimeProvider();
            var gate = new TaskCompletionSource<IList<string>>();
            _dataStoreProvider = CreateDataStoreProvider();
            A.CallTo(() => _dataStoreProvider.LoadTenants(A<CancellationToken>._))
                .ReturnsLazily(_ => gate.Task);
            var snapshot = CreateSnapshot(_dataStoreProvider, timeProvider);

            Task<TenantExistenceOutcome> first = snapshot.CheckAsync("North", CancellationToken.None);
            Task<TenantExistenceOutcome> second = snapshot.CheckAsync("South", CancellationToken.None);
            await Task.Delay(50);

            gate.SetResult(["North"]);

            _firstOutcome = await first;
            _secondOutcome = await second;
        }

        [Test]
        public void It_answers_Exists_for_the_first_caller()
        {
            _firstOutcome.Should().Be(TenantExistenceOutcome.Exists);
        }

        [Test]
        public void It_answers_Absent_for_the_second_caller()
        {
            _secondOutcome.Should().Be(TenantExistenceOutcome.Absent);
        }

        [Test]
        public void It_calls_LoadTenants_exactly_once()
        {
            A.CallTo(() => _dataStoreProvider.LoadTenants(A<CancellationToken>._))
                .MustHaveHappenedOnceExactly();
        }
    }

    [TestFixture]
    public class Given_A_Snapshot_That_Has_Expired : IdentityTenantSnapshotTests
    {
        private IDataStoreProvider _dataStoreProvider = null!;

        [SetUp]
        public async Task Setup()
        {
            var timeProvider = new FakeTimeProvider();
            _dataStoreProvider = CreateDataStoreProvider();
            A.CallTo(() => _dataStoreProvider.LoadTenants(A<CancellationToken>._))
                .Returns(Task.FromResult<IList<string>>(["North"]));
            var snapshot = CreateSnapshot(_dataStoreProvider, timeProvider);

            await snapshot.CheckAsync("North", CancellationToken.None);
            timeProvider.Advance(TimeSpan.FromSeconds(60));
            await snapshot.CheckAsync("North", CancellationToken.None);
        }

        [Test]
        public void It_triggers_a_second_LoadTenants_call_after_expiry()
        {
            A.CallTo(() => _dataStoreProvider.LoadTenants(A<CancellationToken>._))
                .MustHaveHappenedTwiceExactly();
        }
    }

    [TestFixture]
    public class Given_A_Refresh_That_Fails : IdentityTenantSnapshotTests
    {
        private TenantExistenceOutcome _outcome;

        [SetUp]
        public async Task Setup()
        {
            var timeProvider = new FakeTimeProvider();
            IDataStoreProvider dataStoreProvider = CreateDataStoreProvider();
            A.CallTo(() => dataStoreProvider.LoadTenants(A<CancellationToken>._))
                .ThrowsAsync(new InvalidOperationException("Configuration Service unavailable"));
            var snapshot = CreateSnapshot(dataStoreProvider, timeProvider);

            _outcome = await snapshot.CheckAsync("North", CancellationToken.None);
        }

        [Test]
        public void It_returns_Unavailable()
        {
            _outcome.Should().Be(TenantExistenceOutcome.Unavailable);
        }
    }

    [TestFixture]
    public class Given_A_Failure_Within_The_Cooldown_Window : IdentityTenantSnapshotTests
    {
        private IDataStoreProvider _dataStoreProvider = null!;
        private TenantExistenceOutcome _firstOutcome;
        private TenantExistenceOutcome _secondOutcome;
        private TenantExistenceOutcome _thirdOutcome;

        [SetUp]
        public async Task Setup()
        {
            var timeProvider = new FakeTimeProvider();
            _dataStoreProvider = CreateDataStoreProvider();
            A.CallTo(() => _dataStoreProvider.LoadTenants(A<CancellationToken>._))
                .ThrowsAsync(new InvalidOperationException("boom"));
            var snapshot = CreateSnapshot(_dataStoreProvider, timeProvider);

            Task<TenantExistenceOutcome> first = snapshot.CheckAsync("North", CancellationToken.None);
            Task<TenantExistenceOutcome> second = snapshot.CheckAsync("South", CancellationToken.None);

            _firstOutcome = await first;
            _secondOutcome = await second;
            _thirdOutcome = await snapshot.CheckAsync("East", CancellationToken.None);
        }

        [Test]
        public void It_answers_Unavailable_to_the_first_waiter()
        {
            _firstOutcome.Should().Be(TenantExistenceOutcome.Unavailable);
        }

        [Test]
        public void It_answers_Unavailable_to_the_second_waiter()
        {
            _secondOutcome.Should().Be(TenantExistenceOutcome.Unavailable);
        }

        [Test]
        public void It_answers_Unavailable_within_the_cooldown_window()
        {
            _thirdOutcome.Should().Be(TenantExistenceOutcome.Unavailable);
        }

        [Test]
        public void It_calls_LoadTenants_exactly_once()
        {
            A.CallTo(() => _dataStoreProvider.LoadTenants(A<CancellationToken>._))
                .MustHaveHappenedOnceExactly();
        }
    }

    [TestFixture]
    public class Given_A_Call_After_The_Cooldown_Elapses : IdentityTenantSnapshotTests
    {
        private IDataStoreProvider _dataStoreProvider = null!;
        private TenantExistenceOutcome _firstOutcome;
        private TenantExistenceOutcome _secondOutcome;

        [SetUp]
        public async Task Setup()
        {
            var timeProvider = new FakeTimeProvider();
            _dataStoreProvider = CreateDataStoreProvider();
            A.CallTo(() => _dataStoreProvider.LoadTenants(A<CancellationToken>._))
                .ThrowsAsync(new InvalidOperationException("boom"))
                .Once()
                .Then.Returns(Task.FromResult<IList<string>>(["North"]));
            var snapshot = CreateSnapshot(_dataStoreProvider, timeProvider);

            _firstOutcome = await snapshot.CheckAsync("North", CancellationToken.None);
            timeProvider.Advance(TimeSpan.FromSeconds(5));
            _secondOutcome = await snapshot.CheckAsync("North", CancellationToken.None);
        }

        [Test]
        public void It_answers_Unavailable_before_the_cooldown_elapses()
        {
            _firstOutcome.Should().Be(TenantExistenceOutcome.Unavailable);
        }

        [Test]
        public void It_answers_Exists_after_the_cooldown_elapses()
        {
            _secondOutcome.Should().Be(TenantExistenceOutcome.Exists);
        }

        [Test]
        public void It_calls_LoadTenants_twice()
        {
            A.CallTo(() => _dataStoreProvider.LoadTenants(A<CancellationToken>._))
                .MustHaveHappenedTwiceExactly();
        }
    }

    [TestFixture]
    public class Given_A_Pre_Cancelled_Request_Token : IdentityTenantSnapshotTests
    {
        private IDataStoreProvider _dataStoreProvider = null!;
        private CancellationTokenSource _cancellationTokenSource = null!;
        private Func<Task> _act = null!;

        [SetUp]
        public void Setup()
        {
            var timeProvider = new FakeTimeProvider();
            _dataStoreProvider = CreateDataStoreProvider();
            var snapshot = CreateSnapshot(_dataStoreProvider, timeProvider);
            _cancellationTokenSource = new CancellationTokenSource();
            _cancellationTokenSource.Cancel();

            _act = () => snapshot.CheckAsync("North", _cancellationTokenSource.Token);
        }

        [TearDown]
        public void TearDown()
        {
            _cancellationTokenSource.Dispose();
        }

        [Test]
        public async Task It_throws_OperationCanceledException()
        {
            await _act.Should().ThrowAsync<OperationCanceledException>();
        }

        [Test]
        public async Task It_never_calls_LoadTenants()
        {
            await _act.Should().ThrowAsync<OperationCanceledException>();

            A.CallTo(() => _dataStoreProvider.LoadTenants(A<CancellationToken>._)).MustNotHaveHappened();
        }
    }

    [TestFixture]
    public class Given_One_Waiter_Cancelled_While_Another_Still_Waits : IdentityTenantSnapshotTests
    {
        /// <summary>
        /// Mid-flight orchestration: the cancelled waiter must be observed completing before the gate
        /// releases, and the live waiter must be observed still pending at that same moment, so the
        /// gated TaskCompletionSource cannot be moved into SetUp.
        /// </summary>
        [Test]
        public async Task It_completes_the_cancelled_waiter_promptly_while_the_live_waiter_still_receives_Exists()
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
    }

    [TestFixture]
    public class Given_ApplicationStopping_During_An_In_Flight_Refresh : IdentityTenantSnapshotTests
    {
        private TenantExistenceOutcome _outcome;

        [SetUp]
        public async Task Setup()
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

            _outcome = await checkTask.WaitAsync(TimeSpan.FromSeconds(2));
        }

        [Test]
        public void It_returns_Unavailable()
        {
            _outcome.Should().Be(TenantExistenceOutcome.Unavailable);
        }
    }

    [TestFixture]
    public class Given_A_Refresh_That_Exceeds_Its_Budget : IdentityTenantSnapshotTests
    {
        private TenantExistenceOutcome _outcome;

        [SetUp]
        public async Task Setup()
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

            _outcome = await checkTask.WaitAsync(TimeSpan.FromSeconds(2));
        }

        [Test]
        public void It_returns_Unavailable()
        {
            _outcome.Should().Be(TenantExistenceOutcome.Unavailable);
        }
    }

    [TestFixture]
    public class Given_The_Datastore_Independence_Requirement : IdentityTenantSnapshotTests
    {
        private TenantExistenceOutcome _outcome;
        private IDataStoreProvider _dataStoreProvider = null!;
        private IConnectionStringDecryptionService _decryptionService = null!;

        [SetUp]
        public async Task Setup()
        {
            var timeProvider = new FakeTimeProvider();
            _dataStoreProvider = CreateDataStoreProvider();
            A.CallTo(() => _dataStoreProvider.LoadTenants(A<CancellationToken>._))
                .Returns(Task.FromResult<IList<string>>(["North"]));
            A.CallTo(() => _dataStoreProvider.LoadDataStores(A<string?>._, A<CancellationToken>._))
                .Throws(new InvalidOperationException("LoadDataStores must not be called"));
            _decryptionService = A.Fake<IConnectionStringDecryptionService>();
            A.CallTo(_decryptionService).Throws(new InvalidOperationException("must not be called"));
            var snapshot = CreateSnapshot(_dataStoreProvider, timeProvider);

            _outcome = await snapshot.CheckAsync("North", CancellationToken.None);
        }

        [Test]
        public void It_returns_Exists_for_the_known_tenant()
        {
            _outcome.Should().Be(TenantExistenceOutcome.Exists);
        }

        [Test]
        public void It_never_calls_LoadDataStores()
        {
            A.CallTo(() => _dataStoreProvider.LoadDataStores(A<string?>._, A<CancellationToken>._))
                .MustNotHaveHappened();
        }

        [Test]
        public void It_never_touches_the_connection_string_decryption_service()
        {
            A.CallTo(_decryptionService).MustNotHaveHappened();
        }
    }
}
