// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

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
        long applicationId
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
