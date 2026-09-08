// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

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
