// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace EdFi.DmsConfigurationService.Backend.Mssql.Tests.Integration;

public abstract class ApplicationLockManagerTestBase : DatabaseTestBase
{
    private protected static MssqlApplicationLockManager CreateManager(
        TimeSpan? acquireTimeout = null,
        Func<SqlConnection, string, Task>? unlockAsync = null
    )
    {
        IOptions<ApplicationLockOptions> lockOptions = Options.Create(
            new ApplicationLockOptions { AcquireTimeout = acquireTimeout ?? TimeSpan.FromSeconds(5) }
        );
        return unlockAsync is null
            ? new MssqlApplicationLockManager(
                MssqlTestConfiguration.DatabaseOptions,
                lockOptions,
                NullLogger<MssqlApplicationLockManager>.Instance
            )
            : new MssqlApplicationLockManager(
                MssqlTestConfiguration.DatabaseOptions,
                lockOptions,
                NullLogger<MssqlApplicationLockManager>.Instance,
                unlockAsync
            );
    }

    private protected static async Task<IAsyncDisposable> AcquireOrFailAsync(
        MssqlApplicationLockManager manager,
        int applicationId
    )
    {
        ApplicationLockResult result = await manager.AcquireAsync(applicationId, CancellationToken.None);
        result.Should().BeOfType<ApplicationLockResult.Acquired>();
        return ((ApplicationLockResult.Acquired)result).Handle;
    }

    private protected static async Task<bool> TryApplockAsync(SqlConnection connection, string resource)
    {
        using var command = new SqlCommand("sp_getapplock", connection)
        {
            CommandType = System.Data.CommandType.StoredProcedure,
        };
        command.Parameters.AddWithValue("@Resource", resource);
        command.Parameters.AddWithValue("@LockMode", "Exclusive");
        command.Parameters.AddWithValue("@LockOwner", "Session");
        command.Parameters.AddWithValue("@LockTimeout", 0);
        SqlParameter returnValue = command.Parameters.Add("@ReturnValue", System.Data.SqlDbType.Int);
        returnValue.Direction = System.Data.ParameterDirection.ReturnValue;
        await command.ExecuteNonQueryAsync();
        return (int)returnValue.Value >= 0;
    }

    /// <summary>
    /// Whether an independent session can take the resource right now; a probe that succeeds
    /// gives the resource straight back so the probe itself never holds anything.
    /// </summary>
    private protected static async Task<bool> IsFreeAsync(SqlConnection connection, string resource)
    {
        if (!await TryApplockAsync(connection, resource))
        {
            return false;
        }

        await MssqlApplicationLockManager.UnlockAsync(connection, resource);
        return true;
    }

    /// <summary>
    /// A manager whose release seam records the session and resource of every unlock before
    /// performing it, so a fixture can see which resources a lock set held and on how many
    /// sessions.
    /// </summary>
    private protected static MssqlApplicationLockManager CreateRecordingManager(
        List<(int SessionId, string Resource)> releases,
        TimeSpan? acquireTimeout = null
    ) =>
        CreateManager(
            acquireTimeout,
            async (connection, resource) =>
            {
                lock (releases)
                {
                    releases.Add((connection.ServerProcessId, resource));
                }

                await MssqlApplicationLockManager.UnlockAsync(connection, resource);
            }
        );

    private protected static async Task<SqlConnection> OpenIndependentSessionAsync()
    {
        var session = new SqlConnection(MssqlTestConfiguration.DatabaseConnectionString);
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
        MssqlApplicationLockManager holder = CreateManager();
        MssqlApplicationLockManager contender = CreateManager(TimeSpan.FromSeconds(1));
        string resource = MssqlApplicationLockManager.ComputeLockResource(9101);

        IAsyncDisposable held = await AcquireOrFailAsync(holder, 9101);
        _contendedResult = await contender.AcquireAsync(9101, CancellationToken.None);

        // An independent session kept open across the holder's disposal can only acquire if the
        // explicit unlock actually released the lock; pooled-session reentrancy cannot mask a
        // missing release here.
        await using var independentSession = new SqlConnection(
            MssqlTestConfiguration.DatabaseConnectionString
        );
        await independentSession.OpenAsync();
        _independentSessionAcquiredBeforeRelease = await TryApplockAsync(independentSession, resource);

        await held.DisposeAsync();

        _independentSessionAcquiredAfterRelease = await TryApplockAsync(independentSession, resource);
        if (_independentSessionAcquiredAfterRelease)
        {
            await MssqlApplicationLockManager.UnlockAsync(independentSession, resource);
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
public class Given_an_unlock_of_a_lock_that_is_not_held : ApplicationLockManagerTestBase
{
    private Exception? _caught;

    [SetUp]
    public async Task Act()
    {
        await using var session = new SqlConnection(MssqlTestConfiguration.DatabaseConnectionString);
        await session.OpenAsync();
        _caught = Assert.CatchAsync(async () =>
            await MssqlApplicationLockManager.UnlockAsync(
                session,
                MssqlApplicationLockManager.ComputeLockResource(9801)
            )
        );
    }

    [Test]
    public void It_reports_the_failed_release() => _caught.Should().NotBeNull();
}

[TestFixture]
public class Given_a_failed_release_status : ApplicationLockManagerTestBase
{
    private Exception? _caught;

    [SetUp]
    public void Act() => _caught = Assert.Catch(() => MssqlApplicationLockManager.ThrowIfReleaseFailed(-999));

    [Test]
    public void It_throws_the_fixed_release_failure() =>
        _caught.Should().BeOfType<InvalidOperationException>();
}

[TestFixture]
public class Given_a_cancelled_lock_status_from_the_server : ApplicationLockManagerTestBase
{
    private Exception? _caught;

    [SetUp]
    public async Task Act()
    {
        using var cancellationSource = new CancellationTokenSource();
        await cancellationSource.CancelAsync();
        _caught = Assert.Catch(() =>
            MssqlApplicationLockManager.ClassifyFailedLockStatus(-2, cancellationSource.Token)
        );
    }

    [Test]
    public void It_propagates_the_cancellation() =>
        _caught.Should().BeAssignableTo<OperationCanceledException>();
}

[TestFixture]
public class Given_a_cancelled_lock_status_without_caller_cancellation : ApplicationLockManagerTestBase
{
    private ApplicationLockResult _result = null!;

    [SetUp]
    public void Act() =>
        _result = MssqlApplicationLockManager.ClassifyFailedLockStatus(-2, CancellationToken.None);

    [Test]
    public void It_returns_failure_unknown() =>
        _result.Should().BeOfType<ApplicationLockResult.FailureUnknown>();
}

[TestFixture]
public class Given_a_timed_out_lock_status : ApplicationLockManagerTestBase
{
    private ApplicationLockResult _result = null!;

    [SetUp]
    public void Act() =>
        _result = MssqlApplicationLockManager.ClassifyFailedLockStatus(-1, CancellationToken.None);

    [Test]
    public void It_returns_failure_timeout() =>
        _result.Should().BeOfType<ApplicationLockResult.FailureTimeout>();
}

[TestFixture]
public class Given_a_cancelled_acquisition_while_contending : ApplicationLockManagerTestBase
{
    private Exception? _caught;
    private ApplicationLockResult _postReleaseResult = null!;

    [SetUp]
    public async Task Act()
    {
        MssqlApplicationLockManager manager = CreateManager(TimeSpan.FromSeconds(30));

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
        MssqlApplicationLockManager manager = CreateManager(TimeSpan.FromSeconds(1));

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
        MssqlApplicationLockManager manager = CreateManager(TimeSpan.FromSeconds(1));

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
    private (int SessionId, DateTime LoginTime)? _failedSession;
    private (int SessionId, DateTime LoginTime)? _successorSession;
    private int? _independentSessionCanAcquire;
    private ApplicationLockResult _reacquiredResult = null!;

    private static async Task<(int SessionId, DateTime LoginTime)> ReadSessionIdentityAsync(
        SqlConnection connection
    )
    {
        using var command = new SqlCommand(
            "SELECT login_time FROM sys.dm_exec_sessions WHERE session_id = @@SPID;",
            connection
        );
        var loginTime = (DateTime)(await command.ExecuteScalarAsync())!;
        return (connection.ServerProcessId, loginTime);
    }

    [SetUp]
    public async Task Act()
    {
        _failedSession = null;
        _successorSession = null;
        _independentSessionCanAcquire = null;

        MssqlApplicationLockManager failingManager = CreateManager(
            unlockAsync: async (connection, _) =>
            {
                _failedSession = await ReadSessionIdentityAsync(connection);
                throw new InvalidOperationException("forced unlock failure");
            }
        );

        IAsyncDisposable handle = await AcquireOrFailAsync(failingManager, 9401);
        await handle.DisposeAsync();

        // SQL Server session ids are recycled immediately, so a same-session leak is proven
        // absent from a deliberately independent (non-pooled) session: APPLOCK_TEST returns 1
        // only when no other session still holds the resource.
        var probeConnectionString = new SqlConnectionStringBuilder(
            MssqlTestConfiguration.DatabaseConnectionString
        )
        {
            Pooling = false,
        }.ConnectionString;
        await using (var probeConnection = new SqlConnection(probeConnectionString))
        {
            await probeConnection.OpenAsync();
            using var probeCommand = new SqlCommand(
                "SELECT APPLOCK_TEST('public', @Resource, 'Exclusive', 'Session');",
                probeConnection
            );
            probeCommand.Parameters.AddWithValue(
                "@Resource",
                MssqlApplicationLockManager.ComputeLockResource(9401)
            );
            _independentSessionCanAcquire = (int)(await probeCommand.ExecuteScalarAsync())!;
        }

        MssqlApplicationLockManager successorManager = CreateManager(
            unlockAsync: async (connection, resource) =>
            {
                _successorSession = await ReadSessionIdentityAsync(connection);
                await MssqlApplicationLockManager.UnlockAsync(connection, resource);
            }
        );

        _reacquiredResult = await successorManager.AcquireAsync(9401, CancellationToken.None);
        if (_reacquiredResult is ApplicationLockResult.Acquired acquired)
        {
            await acquired.Handle.DisposeAsync();
        }
    }

    [Test]
    public void It_releases_the_leaked_lock_for_independent_sessions() =>
        _independentSessionCanAcquire.Should().Be(1);

    [Test]
    public void It_reacquires_the_lock_after_eviction() =>
        _reacquiredResult.Should().BeOfType<ApplicationLockResult.Acquired>();

    [Test]
    public void It_does_not_reuse_the_evicted_session()
    {
        _failedSession.Should().NotBeNull();
        _successorSession.Should().NotBeNull();
        _successorSession.Should().NotBe(_failedSession);
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
public class Given_the_lock_resource_derivation : ApplicationLockManagerTestBase
{
    private string _resourceForApplication1 = null!;

    [SetUp]
    public void Act()
    {
        _resourceForApplication1 = MssqlApplicationLockManager.ComputeLockResource(1);
    }

    [Test]
    public void It_derives_the_expected_resource_name() =>
        _resourceForApplication1.Should().Be("dmscs:application:1");
}

/// <summary>
/// A lock set over two applications, requested out of order and with a duplicate. Both locks
/// are held for the caller on one session, in ascending resource order, and released together.
/// </summary>
[TestFixture]
public class Given_a_lock_set_acquired_on_one_session : ApplicationLockManagerTestBase
{
    private const int LowerApplicationId = 9911;
    private const int HigherApplicationId = 9912;

    private ApplicationLockResult _result = null!;
    private bool _lowerHeldWhileSetHeld;
    private bool _higherHeldWhileSetHeld;
    private (int SessionId, string Resource)[] _releases = [];
    private bool _lowerFreeAfterRelease;
    private bool _higherFreeAfterRelease;

    [SetUp]
    public async Task Act()
    {
        List<(int SessionId, string Resource)> releases = [];
        MssqlApplicationLockManager manager = CreateRecordingManager(releases, TimeSpan.FromSeconds(1));
        string lowerResource = MssqlApplicationLockManager.ComputeLockResource(LowerApplicationId);
        string higherResource = MssqlApplicationLockManager.ComputeLockResource(HigherApplicationId);

        _result = await manager.AcquireAllAsync(
            [HigherApplicationId, LowerApplicationId, HigherApplicationId],
            CancellationToken.None
        );

        await using SqlConnection independentSession = await OpenIndependentSessionAsync();
        _lowerHeldWhileSetHeld = !await IsFreeAsync(independentSession, lowerResource);
        _higherHeldWhileSetHeld = !await IsFreeAsync(independentSession, higherResource);

        if (_result is ApplicationLockResult.Acquired acquired)
        {
            await acquired.Handle.DisposeAsync();
        }

        _releases = [.. releases];
        _lowerFreeAfterRelease = await IsFreeAsync(independentSession, lowerResource);
        _higherFreeAfterRelease = await IsFreeAsync(independentSession, higherResource);
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
    public void It_holds_the_distinct_resources_in_ascending_order_on_one_session()
    {
        _releases
            .Select(release => release.Resource)
            .Should()
            .Equal(
                MssqlApplicationLockManager.ComputeLockResource(LowerApplicationId),
                MssqlApplicationLockManager.ComputeLockResource(HigherApplicationId)
            );
        _releases.Select(release => release.SessionId).Distinct().Should().HaveCount(1);
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
    private (int SessionId, string Resource)[] _releases = [];
    private bool _lowerFreeAfterTimeout;

    [SetUp]
    public async Task Act()
    {
        string lowerResource = MssqlApplicationLockManager.ComputeLockResource(LowerApplicationId);
        string higherResource = MssqlApplicationLockManager.ComputeLockResource(HigherApplicationId);

        await using SqlConnection holder = await OpenIndependentSessionAsync();
        (await TryApplockAsync(holder, higherResource)).Should().BeTrue();

        List<(int SessionId, string Resource)> releases = [];
        MssqlApplicationLockManager manager = CreateRecordingManager(releases, TimeSpan.FromSeconds(1));
        _result = await manager.AcquireAllAsync(
            [HigherApplicationId, LowerApplicationId],
            CancellationToken.None
        );

        _releases = [.. releases];
        _lowerFreeAfterTimeout = await IsFreeAsync(holder, lowerResource);
        await MssqlApplicationLockManager.UnlockAsync(holder, higherResource);
    }

    [Test]
    public void It_times_out() => _result.Should().BeOfType<ApplicationLockResult.FailureTimeout>();

    [Test]
    public void It_took_the_lower_lock_first_and_released_only_that_one() =>
        _releases
            .Select(release => release.Resource)
            .Should()
            .Equal(MssqlApplicationLockManager.ComputeLockResource(LowerApplicationId));

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
        MssqlApplicationLockManager manager = CreateManager(TimeSpan.FromSeconds(1));

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
    private (int SessionId, string Resource)[] _releases = [];
    private bool _lowerFreeAfterCancellation;

    [SetUp]
    public async Task Act()
    {
        string lowerResource = MssqlApplicationLockManager.ComputeLockResource(LowerApplicationId);
        string higherResource = MssqlApplicationLockManager.ComputeLockResource(HigherApplicationId);

        await using SqlConnection holder = await OpenIndependentSessionAsync();
        (await TryApplockAsync(holder, higherResource)).Should().BeTrue();

        List<(int SessionId, string Resource)> releases = [];
        MssqlApplicationLockManager manager = CreateRecordingManager(releases, TimeSpan.FromSeconds(30));
        using var cancellationSource = new CancellationTokenSource();
        Task<ApplicationLockResult> contending = manager.AcquireAllAsync(
            [LowerApplicationId, HigherApplicationId],
            cancellationSource.Token
        );
        await Task.Delay(300);
        await cancellationSource.CancelAsync();
        _caught = Assert.CatchAsync(async () => await contending);

        _releases = [.. releases];
        _lowerFreeAfterCancellation = await IsFreeAsync(holder, lowerResource);
        await MssqlApplicationLockManager.UnlockAsync(holder, higherResource);
    }

    [Test]
    public void It_propagates_the_cancellation() =>
        _caught.Should().BeAssignableTo<OperationCanceledException>();

    [Test]
    public void It_releases_the_lock_it_already_held() =>
        _releases
            .Select(release => release.Resource)
            .Should()
            .Equal(MssqlApplicationLockManager.ComputeLockResource(LowerApplicationId));

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
    private SqlConnectionStringBuilder _lockConnection = null!;
    private SqlConnectionStringBuilder _databaseConnection = null!;

    [SetUp]
    public void Act()
    {
        _databaseConnection = new SqlConnectionStringBuilder(MssqlTestConfiguration.DatabaseConnectionString);
        _lockConnection = new SqlConnectionStringBuilder(
            MssqlApplicationLockManager.BuildLockConnectionString(
                MssqlTestConfiguration.DatabaseConnectionString
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
        _lockConnection.DataSource.Should().Be(_databaseConnection.DataSource);
        _lockConnection.InitialCatalog.Should().Be(_databaseConnection.InitialCatalog);
        _lockConnection.UserID.Should().Be(_databaseConnection.UserID);
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
    private int _lockHoldingSessionsInDedicatedPool;
    private ApplicationLockResult _beyondBound = null!;

    [SetUp]
    public async Task Act()
    {
        // The repository connection string of this fixture: bounded to exactly the lock pool's
        // size and with a short connect timeout, so a lock manager that drew its sessions from
        // this pool would leave it empty and the repository query below would fail.
        string repositoryConnectionString = new SqlConnectionStringBuilder(
            MssqlTestConfiguration.DatabaseConnectionString
        )
        {
            ApplicationName = "EdFi.DmsConfigurationService.PoolSeparationProbe",
            MinPoolSize = 0,
            MaxPoolSize = ApplicationLockConnectionPool.MaxPoolSize,
            ConnectTimeout = 3,
        }.ConnectionString;

        var manager = new MssqlApplicationLockManager(
            Options.Create(
                new DatabaseOptions
                {
                    DatabaseConnection = repositoryConnectionString,
                    EncryptionKey = MssqlTestConfiguration.DatabaseOptions.Value.EncryptionKey,
                }
            ),
            Options.Create(new ApplicationLockOptions { AcquireTimeout = TimeSpan.FromSeconds(1) }),
            NullLogger<MssqlApplicationLockManager>.Instance
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
                await using var repositoryConnection = new SqlConnection(repositoryConnectionString);
                await repositoryConnection.OpenAsync();
                using var probe = new SqlCommand(
                    """
                    SELECT COUNT(DISTINCT l.request_session_id)
                    FROM sys.dm_tran_locks l
                    JOIN sys.dm_exec_sessions s ON s.session_id = l.request_session_id
                    WHERE l.resource_type = 'APPLICATION' AND s.program_name = @name;
                    """,
                    repositoryConnection
                );
                probe.Parameters.AddWithValue("@name", ApplicationLockConnectionPool.ApplicationName);
                _lockHoldingSessionsInDedicatedPool = (int)(await probe.ExecuteScalarAsync())!;
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
            SqlConnection.ClearPool(
                new SqlConnection(
                    MssqlApplicationLockManager.BuildLockConnectionString(repositoryConnectionString)
                )
            );
            SqlConnection.ClearPool(new SqlConnection(repositoryConnectionString));
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
/// late and has only the remainder left to offer <c>@LockTimeout</c> for the higher lock, which
/// is held for the whole fixture. Passing the full timeout per lock — the defect this pins —
/// would let the set wait another full <c>AcquireTimeout</c> on the higher lock while it kept
/// the lower one held, which is what the upper bound below rejects. A fixture whose second lock
/// is simply held from the outset cannot tell the two behaviors apart.
/// </summary>
[TestFixture]
public class Given_a_lock_set_whose_earlier_lock_consumes_most_of_the_budget : ApplicationLockManagerTestBase
{
    private const int LowerApplicationId = 9961;
    private const int HigherApplicationId = 9962;

    private static readonly TimeSpan _budget = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan _lowerHeldFor = TimeSpan.FromMilliseconds(1500);

    // Generous enough for scheduling, still far below the two budgets a per-lock window would
    // spend (about 3.5 s).
    private static readonly TimeSpan _oneBudgetUpperBound = _budget + TimeSpan.FromMilliseconds(800);

    private ApplicationLockResult _result = null!;
    private TimeSpan _elapsed;
    private (int SessionId, string Resource)[] _releases = [];
    private bool _lowerFreeAfterTimeout;

    [SetUp]
    public async Task Act()
    {
        string lowerResource = MssqlApplicationLockManager.ComputeLockResource(LowerApplicationId);
        string higherResource = MssqlApplicationLockManager.ComputeLockResource(HigherApplicationId);

        await using SqlConnection lowerHolder = await OpenIndependentSessionAsync();
        await using SqlConnection higherHolder = await OpenIndependentSessionAsync();
        (await TryApplockAsync(lowerHolder, lowerResource)).Should().BeTrue();
        (await TryApplockAsync(higherHolder, higherResource)).Should().BeTrue();

        List<(int SessionId, string Resource)> releases = [];
        MssqlApplicationLockManager manager = CreateRecordingManager(releases, _budget);

        Task releaseLower = Task.Run(async () =>
        {
            await Task.Delay(_lowerHeldFor);
            await MssqlApplicationLockManager.UnlockAsync(lowerHolder, lowerResource);
        });

        var elapsed = Stopwatch.StartNew();
        _result = await manager.AcquireAllAsync(
            [LowerApplicationId, HigherApplicationId],
            CancellationToken.None
        );
        _elapsed = elapsed.Elapsed;
        await releaseLower;

        _releases = [.. releases];
        _lowerFreeAfterTimeout = await IsFreeAsync(higherHolder, lowerResource);
        await MssqlApplicationLockManager.UnlockAsync(higherHolder, higherResource);
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
            .Select(release => release.Resource)
            .Should()
            .Equal(MssqlApplicationLockManager.ComputeLockResource(LowerApplicationId));

    [Test]
    public void It_leaves_that_lock_free_after_the_timeout() => _lowerFreeAfterTimeout.Should().BeTrue();
}
