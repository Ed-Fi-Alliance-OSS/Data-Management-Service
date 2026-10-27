// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Dapper;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.Backend.Services;
using EdFi.DmsConfigurationService.DataModel.Model.ApiClient;
using FluentAssertions;
using Microsoft.Data.SqlClient;

namespace EdFi.DmsConfigurationService.Backend.Mssql.Tests.Integration;

/// <summary>
/// The SQL Server state read holds shared locks to commit, Application first, then the client and
/// its rows. A blocker holding the inheriting client's 1003 row pauses the read partway through its
/// stored list. An Application save removing 1001 touches neither that row nor anything the paused
/// read still needs, so without the read's Application lock it would commit mid-read and the read
/// would return the old stored list with the new Application list, a state never committed. With
/// the lock, the save must wait for the read. Each step waits on a blocking chain reported by
/// sys.dm_exec_requests, not on a delay; the wait ceiling only bounds a failure.
/// </summary>
[TestFixture]
public class Given_an_Application_save_while_a_state_read_is_paused
    : ApiClientEducationOrganizationRepositoryTests
{
    private static readonly TimeSpan BlockingWaitCeiling = TimeSpan.FromSeconds(30);

    private ApiClientEducationOrganizationStateResult _pausedRead = null!;
    private ApplicationSaveResult _saveResult = null!;
    private ApiClientEducationOrganizationState _afterBoth = null!;

    [SetUp]
    public async Task Setup()
    {
        await SeedAsync(new TenantContextProvider());

        await using SqlConnection blocker = new(MssqlTestConfiguration.DatabaseConnectionString);
        await blocker.OpenAsync();
        await using SqlTransaction transaction = (SqlTransaction)await blocker.BeginTransactionAsync();
        short blockerSession = await blocker.ExecuteScalarAsync<short>(
            "SELECT @@SPID;",
            transaction: transaction
        );

        await blocker.ExecuteAsync(
            """
            UPDATE dmscs.ApiClientEducationOrganization SET ModifiedBy = 'blocker'
            WHERE ApiClientId = @ApiClientId AND EducationOrganizationId = 1003;
            """,
            new { ApiClientId = InheritingClientId },
            transaction
        );

        Task<ApiClientEducationOrganizationStateResult> read =
            ApiClientRepository.GetApiClientEducationOrganizationState(InheritingClientId);
        short readSession = await SessionBlockedByAsync(blocker, transaction, blockerSession);

        Task<ApplicationSaveResult> save = SaveRenamedScopedApplicationAsync([1002, 1003]);
        await SessionBlockedByAsync(blocker, transaction, readSession);

        await transaction.RollbackAsync();
        _pausedRead = await read;
        _saveResult = await save;
        _afterBoth = await StateAsync(InheritingClientId);
    }

    private static async Task<short> SessionBlockedByAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        short blockingSession
    )
    {
        DateTime ceiling = DateTime.UtcNow + BlockingWaitCeiling;
        while (DateTime.UtcNow < ceiling)
        {
            if (
                await connection.ExecuteScalarAsync<short?>(
                    "SELECT TOP (1) session_id FROM sys.dm_exec_requests WHERE blocking_session_id = @BlockingSession;",
                    new { BlockingSession = blockingSession },
                    transaction
                )
                is short blockedSession
            )
            {
                return blockedSession;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50));
        }

        Assert.Fail($"No session became blocked by session {blockingSession}.");
        return 0;
    }

    [Test]
    public void It_reads_the_whole_state_from_before_the_Application_save() =>
        ShouldMatch(
            _pausedRead.Should().BeOfType<ApiClientEducationOrganizationStateResult.Success>().Subject.State,
            State(ScopedApplicationId, true, [1001, 1002, 1003], [1001, 1002, 1003])
        );

    [Test]
    public void It_completes_the_Application_save_after_the_read() =>
        _saveResult.Should().BeOfType<ApplicationSaveResult.Success>();

    [Test]
    public void It_reads_the_whole_state_after_the_save_afterwards() =>
        ShouldMatch(
            _afterBoth,
            State(
                ScopedApplicationId,
                true,
                [1002, 1003],
                [1002, 1003],
                // The removal marks the inheriting client at the time the save returned
                _saveResult
                    .Should()
                    .BeOfType<ApplicationSaveResult.Success>()
                    .Subject.State.PendingClients.Single(client => client.Id == InheritingClientId)
                    .PendingSince
            )
        );
}

/// <summary>
/// A client writer must contend with a state read on the same lock: the client's clustered
/// PK_ApiClient key, which the read share-locks. If the writer's exclusive lock lands on another
/// index, such as the covering UX_ApiClient_Id_ApplicationId, it does not conflict with the read,
/// and the writer goes on to lock the client's education organization rows while the read waits for
/// them; once the writer needs the client row, the two deadlock. Here a blocker holding one of the
/// Application's education organization rows pauses the read after it has locked the client and its
/// rows. The writer must then be waiting on PK_ApiClient for that read. Each step waits on a
/// blocking chain reported by sys.dm_exec_requests, not on a delay; the wait ceiling only bounds a
/// failure.
/// </summary>
public abstract class ClientWriterWhileAStateReadIsPausedFixture
    : ApiClientEducationOrganizationRepositoryTests
{
    private static readonly TimeSpan BlockingWaitCeiling = TimeSpan.FromSeconds(30);

    protected sealed record LockWait(string TableName, string IndexName);

    protected ApiClientEducationOrganizationStateResult PausedRead { get; private set; } = null!;
    protected LockWait[] WriterWaits { get; private set; } = [];

    protected abstract Task StartAndAwaitWriterAsync(Func<Task> whileWriterIsBlocked);

    [SetUp]
    public async Task Setup()
    {
        await SeedAsync(new TenantContextProvider());

        await using SqlConnection blocker = new(MssqlTestConfiguration.DatabaseConnectionString);
        await blocker.OpenAsync();
        await using SqlTransaction transaction = (SqlTransaction)await blocker.BeginTransactionAsync();
        short blockerSession = await blocker.ExecuteScalarAsync<short>(
            "SELECT @@SPID;",
            transaction: transaction
        );

        await blocker.ExecuteAsync(
            """
            UPDATE dmscs.ApplicationEducationOrganization SET ModifiedBy = 'blocker'
            WHERE ApplicationId = @ApplicationId AND EducationOrganizationId = 1001;
            """,
            new { ApplicationId = ScopedApplicationId },
            transaction
        );

        Task<ApiClientEducationOrganizationStateResult> read =
            ApiClientRepository.GetApiClientEducationOrganizationState(InheritingClientId);
        short readSession = await SessionBlockedByAsync(blocker, transaction, blockerSession);

        await StartAndAwaitWriterAsync(async () =>
        {
            short writerSession = await SessionBlockedByAsync(blocker, transaction, readSession);
            WriterWaits = (
                await blocker.QueryAsync<LockWait>(
                    """
                    SELECT OBJECT_NAME(partition_info.object_id) AS TableName, index_info.name AS IndexName
                    FROM sys.dm_tran_locks lock_info
                    JOIN sys.partitions partition_info
                        ON partition_info.hobt_id = lock_info.resource_associated_entity_id
                    JOIN sys.indexes index_info
                        ON index_info.object_id = partition_info.object_id
                       AND index_info.index_id = partition_info.index_id
                    WHERE lock_info.request_session_id = @WriterSession
                      AND lock_info.request_status = 'WAIT';
                    """,
                    new { WriterSession = writerSession },
                    transaction
                )
            ).ToArray();

            await transaction.RollbackAsync();
            PausedRead = await read;
        });
    }

    private static async Task<short> SessionBlockedByAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        short blockingSession
    )
    {
        DateTime ceiling = DateTime.UtcNow + BlockingWaitCeiling;
        while (DateTime.UtcNow < ceiling)
        {
            if (
                await connection.ExecuteScalarAsync<short?>(
                    "SELECT TOP (1) session_id FROM sys.dm_exec_requests WHERE blocking_session_id = @BlockingSession;",
                    new { BlockingSession = blockingSession },
                    transaction
                )
                is short blockedSession
            )
            {
                return blockedSession;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50));
        }

        Assert.Fail($"No session became blocked by session {blockingSession}.");
        return 0;
    }

    [Test]
    public void It_makes_the_writer_wait_for_the_read_on_the_client_clustered_key() =>
        WriterWaits.Should().Equal(new LockWait("ApiClient", "PK_ApiClient"));

    [Test]
    public void It_reads_the_whole_state_from_before_the_writer() =>
        ShouldMatch(
            PausedRead.Should().BeOfType<ApiClientEducationOrganizationStateResult.Success>().Subject.State,
            State(ScopedApplicationId, true, [1001, 1002, 1003], [1001, 1002, 1003])
        );
}

[TestFixture]
public class Given_a_client_delete_while_a_state_read_is_paused : ClientWriterWhileAStateReadIsPausedFixture
{
    private ApiClientDeleteResult _deleteResult = null!;

    protected override async Task StartAndAwaitWriterAsync(Func<Task> whileWriterIsBlocked)
    {
        Task<ApiClientDeleteResult> delete = ApiClientRepository.DeleteApiClient(InheritingClientId);
        await whileWriterIsBlocked();
        _deleteResult = await delete;
    }

    [Test]
    public void It_deletes_the_client_after_the_read() =>
        _deleteResult.Should().BeOfType<ApiClientDeleteResult.Success>();
}

[TestFixture]
public class Given_a_scope_write_while_a_state_read_is_paused : ClientWriterWhileAStateReadIsPausedFixture
{
    private ApiClientUpdateResult _scopeResult = null!;

    protected override async Task StartAndAwaitWriterAsync(Func<Task> whileWriterIsBlocked)
    {
        Task<ApiClientUpdateResult> write = UpdateApiClientAsync(
            InheritingClientId,
            ScopedApplicationId,
            "Narrowed client",
            new ApiClientEducationOrganizationScope.Narrow([1002])
        );
        await whileWriterIsBlocked();
        _scopeResult = await write;
    }

    [Test]
    public void It_writes_the_scope_after_the_read() =>
        _scopeResult.Should().BeOfType<ApiClientUpdateResult.Success>();
}

/// <summary>
/// DATETIME2 has no session time zone, so two UTC instants an hour apart round-trip as stored,
/// down to DATETIME2's 100-nanosecond precision. Legacy datetime would round .1234567 seconds to
/// .123, so a value bound as datetime would read back different.
/// </summary>
[TestFixture]
public class Given_sync_pending_times_an_hour_apart_with_fractional_seconds
    : ApiClientEducationOrganizationRepositoryTests
{
    private static readonly DateTime First = new DateTime(2030, 11, 3, 5, 30, 0, DateTimeKind.Utc).AddTicks(
        1_234_567
    );
    private static readonly DateTime Second = new DateTime(2030, 11, 3, 6, 30, 0, DateTimeKind.Utc).AddTicks(
        1_234_567
    );

    private ApiClientEducationOrganizationState _firstState = null!;
    private ApiClientEducationOrganizationState _secondState = null!;

    [SetUp]
    public async Task Setup()
    {
        await SeedAsync(new TenantContextProvider());
        await ApiClientRepository.SetApiClientIdentityProviderSyncPending(NarrowedClientId, First);
        await ApiClientRepository.SetApiClientIdentityProviderSyncPending(SecondNarrowedClientId, Second);
        _firstState = await StateAsync(NarrowedClientId);
        _secondState = await StateAsync(SecondNarrowedClientId);
    }

    [Test]
    public void It_reads_back_each_instant_as_UTC()
    {
        _firstState.IdentityProviderSyncPendingSince.Should().Be(First);
        _secondState.IdentityProviderSyncPendingSince.Should().Be(Second);
        _secondState.IdentityProviderSyncPendingSince!.Value.Kind.Should().Be(DateTimeKind.Utc);
    }
}
