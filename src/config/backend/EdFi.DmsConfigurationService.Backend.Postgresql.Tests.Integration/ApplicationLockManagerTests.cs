// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.Tests.Integration;

public abstract class ApplicationLockManagerTestBase : DatabaseTestBase
{
    private protected static PostgresqlApplicationLockManager CreateManager(
        TimeSpan? acquireTimeout = null,
        Func<NpgsqlConnection, long, Task>? unlockAsync = null
    )
    {
        IOptions<ApplicationLockOptions> lockOptions = Options.Create(
            new ApplicationLockOptions { AcquireTimeout = acquireTimeout ?? TimeSpan.FromSeconds(5) }
        );
        return unlockAsync is null
            ? new PostgresqlApplicationLockManager(
                Configuration.DatabaseOptions,
                lockOptions,
                NullLogger<PostgresqlApplicationLockManager>.Instance
            )
            : new PostgresqlApplicationLockManager(
                Configuration.DatabaseOptions,
                lockOptions,
                NullLogger<PostgresqlApplicationLockManager>.Instance,
                unlockAsync
            );
    }

    private protected static async Task<IAsyncDisposable> AcquireOrFailAsync(
        PostgresqlApplicationLockManager manager,
        int applicationId
    )
    {
        ApplicationLockResult result = await manager.AcquireAsync(applicationId, CancellationToken.None);
        result.Should().BeOfType<ApplicationLockResult.Acquired>();
        return ((ApplicationLockResult.Acquired)result).Handle;
    }

    private protected static async Task<bool> TryAdvisoryLockAsync(NpgsqlConnection connection, long key)
    {
        await using var command = new NpgsqlCommand("SELECT pg_try_advisory_lock(@key);", connection);
        command.Parameters.AddWithValue("key", key);
        return (bool)(await command.ExecuteScalarAsync())!;
    }

    /// <summary>
    /// Whether an independent session can take the key right now; a probe that succeeds gives
    /// the key straight back so the probe itself never holds anything.
    /// </summary>
    private protected static async Task<bool> IsFreeAsync(NpgsqlConnection connection, long key)
    {
        if (!await TryAdvisoryLockAsync(connection, key))
        {
            return false;
        }

        await PostgresqlApplicationLockManager.UnlockAsync(connection, key);
        return true;
    }

    /// <summary>
    /// A manager whose release seam records the session and key of every unlock before
    /// performing it, so a fixture can see which keys a lock set held and on how many sessions.
    /// </summary>
    private protected static PostgresqlApplicationLockManager CreateRecordingManager(
        List<(int ProcessId, long Key)> releases,
        TimeSpan? acquireTimeout = null
    ) =>
        CreateManager(
            acquireTimeout,
            async (connection, key) =>
            {
                lock (releases)
                {
                    releases.Add((connection.ProcessID, key));
                }

                await PostgresqlApplicationLockManager.UnlockAsync(connection, key);
            }
        );

    private protected static async Task<NpgsqlConnection> OpenIndependentSessionAsync()
    {
        var session = new NpgsqlConnection(Configuration.DatabaseOptions.Value.DatabaseConnection);
        await session.OpenAsync();
        return session;
    }
}

[TestFixture]
public class Given_an_application_lock_held_by_another_session : ApplicationLockManagerTestBase
{
    private ApplicationLockResult _contendedResult = null!;
    private bool _independentSessionAcquiredBeforeRelease;
    private bool _independentSessionAcquiredAfterRelease;

    [SetUp]
    public async Task Act()
    {
        PostgresqlApplicationLockManager holder = CreateManager();
        PostgresqlApplicationLockManager contender = CreateManager(TimeSpan.FromSeconds(1));
        long key = PostgresqlApplicationLockManager.ComputeLockKey(9101);

        IAsyncDisposable held = await AcquireOrFailAsync(holder, 9101);
        _contendedResult = await contender.AcquireAsync(9101, CancellationToken.None);

        // An independent session kept open across the holder's disposal can only acquire if the
        // explicit unlock actually released the lock; pooled-session reentrancy cannot mask a
        // missing release here.
        await using var independentSession = new NpgsqlConnection(
            Configuration.DatabaseOptions.Value.DatabaseConnection
        );
        await independentSession.OpenAsync();
        _independentSessionAcquiredBeforeRelease = await TryAdvisoryLockAsync(independentSession, key);

        await held.DisposeAsync();

        _independentSessionAcquiredAfterRelease = await TryAdvisoryLockAsync(independentSession, key);
        if (_independentSessionAcquiredAfterRelease)
        {
            await PostgresqlApplicationLockManager.UnlockAsync(independentSession, key);
        }
    }

    [Test]
    public void It_times_out_while_the_lock_is_held() =>
        _contendedResult.Should().BeOfType<ApplicationLockResult.FailureTimeout>();

    [Test]
    public void It_blocks_an_independent_open_session_before_release() =>
        _independentSessionAcquiredBeforeRelease.Should().BeFalse();

    [Test]
    public void It_grants_the_same_independent_session_after_release() =>
        _independentSessionAcquiredAfterRelease.Should().BeTrue();
}

[TestFixture]
public class Given_a_holder_releasing_after_the_contention_deadline : ApplicationLockManagerTestBase
{
    private ApplicationLockResult _contendedResult = null!;

    [SetUp]
    public async Task Act()
    {
        PostgresqlApplicationLockManager holder = CreateManager();
        PostgresqlApplicationLockManager contender = CreateManager(TimeSpan.FromMilliseconds(50));

        // Warm the pool so the timed attempt below is not skewed by connection setup.
        IAsyncDisposable warmup = await AcquireOrFailAsync(contender, 9601);
        await warmup.DisposeAsync();

        IAsyncDisposable held = await AcquireOrFailAsync(holder, 9602);
        Task<ApplicationLockResult> contending = contender.AcquireAsync(9602, CancellationToken.None);

        // The holder releases after the 50 ms deadline but before the fixed 200 ms poll would
        // retry; an expired wait must not be granted the lock.
        await Task.Delay(120);
        await held.DisposeAsync();

        _contendedResult = await contending;
    }

    [Test]
    public void It_still_times_out_when_the_holder_releases_after_the_deadline() =>
        _contendedResult.Should().BeOfType<ApplicationLockResult.FailureTimeout>();
}

[TestFixture]
public class Given_an_unlock_of_a_lock_that_is_not_held : ApplicationLockManagerTestBase
{
    private Exception? _caught;

    [SetUp]
    public async Task Act()
    {
        await using var session = new NpgsqlConnection(
            Configuration.DatabaseOptions.Value.DatabaseConnection
        );
        await session.OpenAsync();
        _caught = Assert.CatchAsync(async () =>
            await PostgresqlApplicationLockManager.UnlockAsync(
                session,
                PostgresqlApplicationLockManager.ComputeLockKey(9801)
            )
        );
    }

    [Test]
    public void It_throws_the_fixed_release_failure() =>
        _caught.Should().BeOfType<InvalidOperationException>();
}

[TestFixture]
public class Given_a_cancelled_acquisition_while_contending : ApplicationLockManagerTestBase
{
    private Exception? _caught;
    private ApplicationLockResult _postReleaseResult = null!;

    [SetUp]
    public async Task Act()
    {
        PostgresqlApplicationLockManager manager = CreateManager(TimeSpan.FromSeconds(30));

        IAsyncDisposable held = await AcquireOrFailAsync(manager, 9701);
        using var cancellationSource = new CancellationTokenSource();
        Task<ApplicationLockResult> contending = manager.AcquireAsync(9701, cancellationSource.Token);
        await Task.Delay(300);
        await cancellationSource.CancelAsync();
        _caught = Assert.CatchAsync(async () => await contending);

        await held.DisposeAsync();
        _postReleaseResult = await manager.AcquireAsync(9701, CancellationToken.None);
        if (_postReleaseResult is ApplicationLockResult.Acquired acquired)
        {
            await acquired.Handle.DisposeAsync();
        }
    }

    [Test]
    public void It_propagates_the_cancellation() =>
        _caught.Should().BeAssignableTo<OperationCanceledException>();

    [Test]
    public void It_remains_usable_after_the_cancelled_wait() =>
        _postReleaseResult.Should().BeOfType<ApplicationLockResult.Acquired>();
}

[TestFixture]
public class Given_locks_for_two_different_applications : ApplicationLockManagerTestBase
{
    private ApplicationLockResult _firstResult = null!;
    private ApplicationLockResult _secondResult = null!;

    [SetUp]
    public async Task Act()
    {
        PostgresqlApplicationLockManager manager = CreateManager(TimeSpan.FromSeconds(1));

        _firstResult = await manager.AcquireAsync(9201, CancellationToken.None);
        _secondResult = await manager.AcquireAsync(9202, CancellationToken.None);

        if (_secondResult is ApplicationLockResult.Acquired second)
        {
            await second.Handle.DisposeAsync();
        }

        if (_firstResult is ApplicationLockResult.Acquired first)
        {
            await first.Handle.DisposeAsync();
        }
    }

    [Test]
    public void It_acquires_the_first_application_lock() =>
        _firstResult.Should().BeOfType<ApplicationLockResult.Acquired>();

    [Test]
    public void It_acquires_the_second_application_lock_concurrently() =>
        _secondResult.Should().BeOfType<ApplicationLockResult.Acquired>();
}

[TestFixture]
public class Given_a_lock_released_after_a_workflow_exception : ApplicationLockManagerTestBase
{
    private ApplicationLockResult _reacquiredResult = null!;

    [SetUp]
    public async Task Act()
    {
        PostgresqlApplicationLockManager manager = CreateManager(TimeSpan.FromSeconds(1));

        IAsyncDisposable handle = await AcquireOrFailAsync(manager, 9301);
        try
        {
            throw new InvalidOperationException("workflow failure");
        }
        catch (InvalidOperationException)
        {
            // The workflow failure is observed; the handle must still be released below.
        }
        finally
        {
            await handle.DisposeAsync();
        }

        _reacquiredResult = await manager.AcquireAsync(9301, CancellationToken.None);
        if (_reacquiredResult is ApplicationLockResult.Acquired acquired)
        {
            await acquired.Handle.DisposeAsync();
        }
    }

    [Test]
    public void It_reacquires_the_lock() =>
        _reacquiredResult.Should().BeOfType<ApplicationLockResult.Acquired>();
}

[TestFixture]
public class Given_an_unlock_failure_on_release : ApplicationLockManagerTestBase
{
    private int? _failedSessionPid;
    private int? _successorSessionPid;
    private ApplicationLockResult _reacquiredResult = null!;

    [SetUp]
    public async Task Act()
    {
        _failedSessionPid = null;
        _successorSessionPid = null;

        PostgresqlApplicationLockManager failingManager = CreateManager(
            unlockAsync: (connection, _) =>
            {
                _failedSessionPid = connection.ProcessID;
                throw new InvalidOperationException("forced unlock failure");
            }
        );

        IAsyncDisposable handle = await AcquireOrFailAsync(failingManager, 9401);
        await handle.DisposeAsync();

        PostgresqlApplicationLockManager successorManager = CreateManager(
            unlockAsync: async (connection, key) =>
            {
                _successorSessionPid = connection.ProcessID;
                await PostgresqlApplicationLockManager.UnlockAsync(connection, key);
            }
        );

        _reacquiredResult = await successorManager.AcquireAsync(9401, CancellationToken.None);
        if (_reacquiredResult is ApplicationLockResult.Acquired acquired)
        {
            await acquired.Handle.DisposeAsync();
        }
    }

    [Test]
    public void It_reacquires_the_lock_after_eviction() =>
        _reacquiredResult.Should().BeOfType<ApplicationLockResult.Acquired>();

    [Test]
    public void It_does_not_reuse_the_evicted_session()
    {
        _failedSessionPid.Should().NotBeNull();
        _successorSessionPid.Should().NotBeNull();
        _successorSessionPid.Should().NotBe(_failedSessionPid);
    }
}

[TestFixture]
public class Given_a_cancelled_lock_acquisition : ApplicationLockManagerTestBase
{
    private Exception? _caught;

    [SetUp]
    public async Task Act()
    {
        using var cancellationSource = new CancellationTokenSource();
        await cancellationSource.CancelAsync();

        _caught = Assert.CatchAsync(async () =>
            await CreateManager().AcquireAsync(9501, cancellationSource.Token)
        );
    }

    [Test]
    public void It_propagates_the_cancellation() =>
        _caught.Should().BeAssignableTo<OperationCanceledException>();
}

[TestFixture]
public class Given_the_lock_key_derivation : ApplicationLockManagerTestBase
{
    private long _keyForApplication1;
    private long _keyForApplication42;

    [SetUp]
    public void Act()
    {
        _keyForApplication1 = PostgresqlApplicationLockManager.ComputeLockKey(1);
        _keyForApplication42 = PostgresqlApplicationLockManager.ComputeLockKey(42);
    }

    [Test]
    public void It_derives_the_expected_key_for_application_1() =>
        _keyForApplication1.Should().Be(-8823528662823346350L);

    [Test]
    public void It_derives_the_expected_key_for_application_42() =>
        _keyForApplication42.Should().Be(4405348987498648439L);
}

/// <summary>
/// A lock set over two applications, requested out of order and with a duplicate. Both locks
/// are held for the caller on one session, in ascending key order, and released together.
/// </summary>
[TestFixture]
public class Given_a_lock_set_acquired_on_one_session : ApplicationLockManagerTestBase
{
    private const int LowerApplicationId = 9911;
    private const int HigherApplicationId = 9912;

    private ApplicationLockResult _result = null!;
    private bool _lowerHeldWhileSetHeld;
    private bool _higherHeldWhileSetHeld;
    private (int ProcessId, long Key)[] _releases = [];
    private bool _lowerFreeAfterRelease;
    private bool _higherFreeAfterRelease;

    [SetUp]
    public async Task Act()
    {
        List<(int ProcessId, long Key)> releases = [];
        PostgresqlApplicationLockManager manager = CreateRecordingManager(releases, TimeSpan.FromSeconds(1));
        long lowerKey = PostgresqlApplicationLockManager.ComputeLockKey(LowerApplicationId);
        long higherKey = PostgresqlApplicationLockManager.ComputeLockKey(HigherApplicationId);

        _result = await manager.AcquireAllAsync(
            [HigherApplicationId, LowerApplicationId, HigherApplicationId],
            CancellationToken.None
        );

        await using NpgsqlConnection independentSession = await OpenIndependentSessionAsync();
        _lowerHeldWhileSetHeld = !await IsFreeAsync(independentSession, lowerKey);
        _higherHeldWhileSetHeld = !await IsFreeAsync(independentSession, higherKey);

        if (_result is ApplicationLockResult.Acquired acquired)
        {
            await acquired.Handle.DisposeAsync();
        }

        _releases = [.. releases];
        _lowerFreeAfterRelease = await IsFreeAsync(independentSession, lowerKey);
        _higherFreeAfterRelease = await IsFreeAsync(independentSession, higherKey);
    }

    [Test]
    public void It_acquires_the_set() => _result.Should().BeOfType<ApplicationLockResult.Acquired>();

    [Test]
    public void It_holds_both_locks_against_an_independent_session()
    {
        _lowerHeldWhileSetHeld.Should().BeTrue();
        _higherHeldWhileSetHeld.Should().BeTrue();
    }

    [Test]
    public void It_holds_the_distinct_keys_in_ascending_order_on_one_session()
    {
        _releases
            .Select(release => release.Key)
            .Should()
            .Equal(
                PostgresqlApplicationLockManager.ComputeLockKey(LowerApplicationId),
                PostgresqlApplicationLockManager.ComputeLockKey(HigherApplicationId)
            );
        _releases.Select(release => release.ProcessId).Distinct().Should().HaveCount(1);
    }

    [Test]
    public void It_frees_both_locks_on_release()
    {
        _lowerFreeAfterRelease.Should().BeTrue();
        _higherFreeAfterRelease.Should().BeTrue();
    }
}

/// <summary>
/// The higher application of a lock set is held elsewhere. Requested with the higher id first,
/// the set must still take the lower lock first (ascending order) and, when the higher one
/// times out, give the lower one back before reporting the timeout.
/// </summary>
[TestFixture]
public class Given_a_lock_set_whose_higher_lock_is_held_elsewhere : ApplicationLockManagerTestBase
{
    private const int LowerApplicationId = 9921;
    private const int HigherApplicationId = 9922;

    private ApplicationLockResult _result = null!;
    private (int ProcessId, long Key)[] _releases = [];
    private bool _lowerFreeAfterTimeout;

    [SetUp]
    public async Task Act()
    {
        long lowerKey = PostgresqlApplicationLockManager.ComputeLockKey(LowerApplicationId);
        long higherKey = PostgresqlApplicationLockManager.ComputeLockKey(HigherApplicationId);

        await using NpgsqlConnection holder = await OpenIndependentSessionAsync();
        (await TryAdvisoryLockAsync(holder, higherKey)).Should().BeTrue();

        List<(int ProcessId, long Key)> releases = [];
        PostgresqlApplicationLockManager manager = CreateRecordingManager(releases, TimeSpan.FromSeconds(1));
        _result = await manager.AcquireAllAsync(
            [HigherApplicationId, LowerApplicationId],
            CancellationToken.None
        );

        _releases = [.. releases];
        _lowerFreeAfterTimeout = await IsFreeAsync(holder, lowerKey);
        await PostgresqlApplicationLockManager.UnlockAsync(holder, higherKey);
    }

    [Test]
    public void It_times_out() => _result.Should().BeOfType<ApplicationLockResult.FailureTimeout>();

    [Test]
    public void It_took_the_lower_lock_first_and_released_only_that_one() =>
        _releases
            .Select(release => release.Key)
            .Should()
            .Equal(PostgresqlApplicationLockManager.ComputeLockKey(LowerApplicationId));

    [Test]
    public void It_leaves_the_lower_lock_free_after_the_timeout() => _lowerFreeAfterTimeout.Should().BeTrue();
}

/// <summary>
/// A lock set and a single-application lock over the same application share the lock resource,
/// so each blocks the other for as long as it is held.
/// </summary>
[TestFixture]
public class Given_a_lock_set_contending_with_a_single_application_lock : ApplicationLockManagerTestBase
{
    private const int SharedApplicationId = 9931;
    private const int OtherApplicationId = 9932;

    private ApplicationLockResult _setWhileSingleHeld = null!;
    private ApplicationLockResult _setAfterSingleReleased = null!;
    private ApplicationLockResult _singleWhileSetHeld = null!;
    private ApplicationLockResult _singleAfterSetReleased = null!;

    [SetUp]
    public async Task Act()
    {
        PostgresqlApplicationLockManager manager = CreateManager(TimeSpan.FromSeconds(1));

        IAsyncDisposable single = await AcquireOrFailAsync(manager, SharedApplicationId);
        _setWhileSingleHeld = await manager.AcquireAllAsync(
            [SharedApplicationId, OtherApplicationId],
            CancellationToken.None
        );
        await single.DisposeAsync();

        _setAfterSingleReleased = await manager.AcquireAllAsync(
            [SharedApplicationId, OtherApplicationId],
            CancellationToken.None
        );
        _singleWhileSetHeld = await manager.AcquireAsync(OtherApplicationId, CancellationToken.None);
        if (_setAfterSingleReleased is ApplicationLockResult.Acquired set)
        {
            await set.Handle.DisposeAsync();
        }

        _singleAfterSetReleased = await manager.AcquireAsync(OtherApplicationId, CancellationToken.None);
        if (_singleAfterSetReleased is ApplicationLockResult.Acquired reacquired)
        {
            await reacquired.Handle.DisposeAsync();
        }
    }

    [Test]
    public void It_blocks_the_set_while_a_single_lock_holds_one_of_its_applications() =>
        _setWhileSingleHeld.Should().BeOfType<ApplicationLockResult.FailureTimeout>();

    [Test]
    public void It_grants_the_set_once_the_single_lock_is_released() =>
        _setAfterSingleReleased.Should().BeOfType<ApplicationLockResult.Acquired>();

    [Test]
    public void It_blocks_a_single_lock_while_the_set_holds_its_application() =>
        _singleWhileSetHeld.Should().BeOfType<ApplicationLockResult.FailureTimeout>();

    [Test]
    public void It_grants_the_single_lock_once_the_set_is_released() =>
        _singleAfterSetReleased.Should().BeOfType<ApplicationLockResult.Acquired>();
}

/// <summary>
/// A lock set cancelled while waiting for its second lock propagates the cancellation and gives
/// back the first lock it already held.
/// </summary>
[TestFixture]
public class Given_a_cancelled_lock_set_acquisition_while_contending : ApplicationLockManagerTestBase
{
    private const int LowerApplicationId = 9941;
    private const int HigherApplicationId = 9942;

    private Exception? _caught;
    private (int ProcessId, long Key)[] _releases = [];
    private bool _lowerFreeAfterCancellation;

    [SetUp]
    public async Task Act()
    {
        long lowerKey = PostgresqlApplicationLockManager.ComputeLockKey(LowerApplicationId);
        long higherKey = PostgresqlApplicationLockManager.ComputeLockKey(HigherApplicationId);

        await using NpgsqlConnection holder = await OpenIndependentSessionAsync();
        (await TryAdvisoryLockAsync(holder, higherKey)).Should().BeTrue();

        List<(int ProcessId, long Key)> releases = [];
        PostgresqlApplicationLockManager manager = CreateRecordingManager(releases, TimeSpan.FromSeconds(30));
        using var cancellationSource = new CancellationTokenSource();
        Task<ApplicationLockResult> contending = manager.AcquireAllAsync(
            [LowerApplicationId, HigherApplicationId],
            cancellationSource.Token
        );
        await Task.Delay(300);
        await cancellationSource.CancelAsync();
        _caught = Assert.CatchAsync(async () => await contending);

        _releases = [.. releases];
        _lowerFreeAfterCancellation = await IsFreeAsync(holder, lowerKey);
        await PostgresqlApplicationLockManager.UnlockAsync(holder, higherKey);
    }

    [Test]
    public void It_propagates_the_cancellation() =>
        _caught.Should().BeAssignableTo<OperationCanceledException>();

    [Test]
    public void It_releases_the_lock_it_already_held() =>
        _releases
            .Select(release => release.Key)
            .Should()
            .Equal(PostgresqlApplicationLockManager.ComputeLockKey(LowerApplicationId));

    [Test]
    public void It_leaves_that_lock_free() => _lowerFreeAfterCancellation.Should().BeTrue();
}

/// <summary>
/// The lock managers build their sessions' connection string from the configured database
/// connection by renaming and bounding the pool, so lock sessions are pooled apart from
/// repository connections.
/// </summary>
[TestFixture]
public class Given_the_lock_connection_string : ApplicationLockManagerTestBase
{
    private NpgsqlConnectionStringBuilder _lockConnection = null!;
    private NpgsqlConnectionStringBuilder _databaseConnection = null!;

    [SetUp]
    public void Act()
    {
        _databaseConnection = new NpgsqlConnectionStringBuilder(
            Configuration.DatabaseOptions.Value.DatabaseConnection
        );
        _lockConnection = new NpgsqlConnectionStringBuilder(
            PostgresqlApplicationLockManager.BuildLockConnectionString(
                Configuration.DatabaseOptions.Value.DatabaseConnection
            )
        );
    }

    [Test]
    public void It_names_the_dedicated_pool() =>
        _lockConnection.ApplicationName.Should().Be(ApplicationLockConnectionPool.ApplicationName);

    [Test]
    public void It_bounds_the_dedicated_pool()
    {
        _lockConnection.Pooling.Should().BeTrue();
        _lockConnection.MinPoolSize.Should().Be(0);
        _lockConnection.MaxPoolSize.Should().Be(ApplicationLockConnectionPool.MaxPoolSize);
    }

    [Test]
    public void It_targets_the_same_database()
    {
        _lockConnection.Host.Should().Be(_databaseConnection.Host);
        _lockConnection.Port.Should().Be(_databaseConnection.Port);
        _lockConnection.Database.Should().Be(_databaseConnection.Database);
        _lockConnection.Username.Should().Be(_databaseConnection.Username);
    }
}

/// <summary>
/// Every session of the lock pool is held by a lock. The repository pool, given the same size
/// so that a shared pool would be exhausted too, still opens a connection at once; each lock is
/// held on a session of the dedicated pool; and a lock beyond the bound is refused rather than
/// taking a repository connection.
/// </summary>
[TestFixture]
public class Given_the_lock_pool_saturated_by_lock_sessions : ApplicationLockManagerTestBase
{
    private const int FirstApplicationId = 9951;

    private readonly List<IAsyncDisposable> _held = [];
    private int _acquiredCount;
    private bool _repositoryQuerySucceeded;
    private long _lockHoldingSessionsInDedicatedPool;
    private ApplicationLockResult _beyondBound = null!;

    [SetUp]
    public async Task Act()
    {
        // The repository connection string of this fixture: bounded to exactly the lock pool's
        // size and with a short connect timeout, so a lock manager that drew its sessions from
        // this pool would leave it empty and the repository query below would fail.
        string repositoryConnectionString = new NpgsqlConnectionStringBuilder(
            Configuration.DatabaseOptions.Value.DatabaseConnection
        )
        {
            ApplicationName = "EdFi.DmsConfigurationService.PoolSeparationProbe",
            MinPoolSize = 0,
            MaxPoolSize = ApplicationLockConnectionPool.MaxPoolSize,
            Timeout = 3,
        }.ConnectionString;

        var manager = new PostgresqlApplicationLockManager(
            Options.Create(
                new DatabaseOptions
                {
                    DatabaseConnection = repositoryConnectionString,
                    EncryptionKey = Configuration.DatabaseOptions.Value.EncryptionKey,
                }
            ),
            Options.Create(new ApplicationLockOptions { AcquireTimeout = TimeSpan.FromSeconds(1) }),
            NullLogger<PostgresqlApplicationLockManager>.Instance
        );

        try
        {
            for (int offset = 0; offset < ApplicationLockConnectionPool.MaxPoolSize; offset++)
            {
                ApplicationLockResult result = await manager.AcquireAsync(
                    FirstApplicationId + offset,
                    CancellationToken.None
                );
                if (result is ApplicationLockResult.Acquired acquired)
                {
                    _held.Add(acquired.Handle);
                }
            }

            _acquiredCount = _held.Count;

            try
            {
                await using var repositoryConnection = new NpgsqlConnection(repositoryConnectionString);
                await repositoryConnection.OpenAsync();
                await using var probe = new NpgsqlCommand(
                    """
                    SELECT count(DISTINCT l.pid)
                    FROM pg_locks l
                    JOIN pg_stat_activity a ON a.pid = l.pid
                    WHERE l.locktype = 'advisory' AND a.application_name = @name;
                    """,
                    repositoryConnection
                );
                probe.Parameters.AddWithValue("name", ApplicationLockConnectionPool.ApplicationName);
                _lockHoldingSessionsInDedicatedPool = (long)(await probe.ExecuteScalarAsync())!;
                _repositoryQuerySucceeded = true;
            }
            catch (Exception)
            {
                _repositoryQuerySucceeded = false;
            }

            _beyondBound = await manager.AcquireAsync(
                FirstApplicationId + ApplicationLockConnectionPool.MaxPoolSize,
                CancellationToken.None
            );
            if (_beyondBound is ApplicationLockResult.Acquired unexpected)
            {
                _held.Add(unexpected.Handle);
            }
        }
        finally
        {
            foreach (IAsyncDisposable handle in _held)
            {
                await handle.DisposeAsync();
            }

            _held.Clear();
            NpgsqlConnection.ClearPool(
                new NpgsqlConnection(
                    PostgresqlApplicationLockManager.BuildLockConnectionString(repositoryConnectionString)
                )
            );
            NpgsqlConnection.ClearPool(new NpgsqlConnection(repositoryConnectionString));
        }
    }

    [Test]
    public void It_holds_a_lock_on_every_session_of_the_bounded_pool() =>
        _acquiredCount.Should().Be(ApplicationLockConnectionPool.MaxPoolSize);

    [Test]
    public void It_leaves_the_repository_pool_available() => _repositoryQuerySucceeded.Should().BeTrue();

    [Test]
    public void It_holds_every_lock_on_a_session_of_the_dedicated_pool() =>
        _lockHoldingSessionsInDedicatedPool.Should().Be(ApplicationLockConnectionPool.MaxPoolSize);

    [Test]
    public void It_refuses_a_lock_session_beyond_the_bound() =>
        _beyondBound.Should().BeOfType<ApplicationLockResult.FailureUnknown>();
}

/// <summary>
/// One acquisition attempt gets one contention budget, shared by every lock of the set. Here the
/// lower lock is held elsewhere for most of the budget and then given up, so the set takes it
/// late and has only the remainder left to contend for the higher lock, which is held for the
/// whole fixture. Restarting the window per lock — the defect this pins — would let the set wait
/// another full <c>AcquireTimeout</c> on the higher lock while it kept the lower one held, which
/// is what the upper bound below rejects. A fixture whose second lock is simply held from the
/// outset cannot tell the two behaviors apart.
/// </summary>
[TestFixture]
public class Given_a_lock_set_whose_earlier_lock_consumes_most_of_the_budget : ApplicationLockManagerTestBase
{
    private const int LowerApplicationId = 9961;
    private const int HigherApplicationId = 9962;

    private static readonly TimeSpan _budget = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan _lowerHeldFor = TimeSpan.FromMilliseconds(1500);

    // Generous enough for scheduling and poll granularity, still far below the two budgets a
    // per-lock window would spend (about 3.5 s).
    private static readonly TimeSpan _oneBudgetUpperBound = _budget + TimeSpan.FromMilliseconds(800);

    private ApplicationLockResult _result = null!;
    private TimeSpan _elapsed;
    private (int ProcessId, long Key)[] _releases = [];
    private bool _lowerFreeAfterTimeout;

    [SetUp]
    public async Task Act()
    {
        long lowerKey = PostgresqlApplicationLockManager.ComputeLockKey(LowerApplicationId);
        long higherKey = PostgresqlApplicationLockManager.ComputeLockKey(HigherApplicationId);

        await using NpgsqlConnection lowerHolder = await OpenIndependentSessionAsync();
        await using NpgsqlConnection higherHolder = await OpenIndependentSessionAsync();
        (await TryAdvisoryLockAsync(lowerHolder, lowerKey)).Should().BeTrue();
        (await TryAdvisoryLockAsync(higherHolder, higherKey)).Should().BeTrue();

        List<(int ProcessId, long Key)> releases = [];
        PostgresqlApplicationLockManager manager = CreateRecordingManager(releases, _budget);

        Task releaseLower = Task.Run(async () =>
        {
            await Task.Delay(_lowerHeldFor);
            await PostgresqlApplicationLockManager.UnlockAsync(lowerHolder, lowerKey);
        });

        var elapsed = Stopwatch.StartNew();
        _result = await manager.AcquireAllAsync(
            [LowerApplicationId, HigherApplicationId],
            CancellationToken.None
        );
        _elapsed = elapsed.Elapsed;
        await releaseLower;

        _releases = [.. releases];
        _lowerFreeAfterTimeout = await IsFreeAsync(higherHolder, lowerKey);
        await PostgresqlApplicationLockManager.UnlockAsync(higherHolder, higherKey);
    }

    [Test]
    public void It_times_out() => _result.Should().BeOfType<ApplicationLockResult.FailureTimeout>();

    [Test]
    public void It_spends_one_budget_on_the_whole_set() => _elapsed.Should().BeLessThan(_oneBudgetUpperBound);

    [Test]
    public void It_spends_the_budget_rather_than_giving_up_when_the_first_lock_is_taken() =>
        _elapsed.Should().BeGreaterThan(_lowerHeldFor);

    [Test]
    public void It_releases_the_lock_it_took_before_the_budget_ran_out() =>
        _releases
            .Select(release => release.Key)
            .Should()
            .Equal(PostgresqlApplicationLockManager.ComputeLockKey(LowerApplicationId));

    [Test]
    public void It_leaves_that_lock_free_after_the_timeout() => _lowerFreeAfterTimeout.Should().BeTrue();
}
