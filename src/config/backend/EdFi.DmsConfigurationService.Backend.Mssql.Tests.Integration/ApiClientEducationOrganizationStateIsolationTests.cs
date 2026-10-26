// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Dapper;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.Backend.Services;
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
    private ApplicationUpdateResult _saveResult = null!;
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

        Task<ApplicationUpdateResult> save = UpdateScopedApplicationAsync([1002, 1003]);
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
            short? blockedSession = await connection.ExecuteScalarAsync<short?>(
                "SELECT TOP (1) session_id FROM sys.dm_exec_requests WHERE blocking_session_id = @BlockingSession;",
                new { BlockingSession = blockingSession },
                transaction
            );
            if (blockedSession is not null)
            {
                return blockedSession.Value;
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
        _saveResult.Should().BeOfType<ApplicationUpdateResult.Success>();

    [Test]
    public void It_reads_the_whole_state_after_the_save_afterwards() =>
        ShouldMatch(_afterBoth, State(ScopedApplicationId, true, [1002, 1003], [1002, 1003]));
}

/// <summary>
/// DATETIME2 has no session time zone, so two UTC instants an hour apart round-trip as stored.
/// </summary>
[TestFixture]
public class Given_sync_pending_times_an_hour_apart : ApiClientEducationOrganizationRepositoryTests
{
    private static readonly DateTime First = new(2030, 11, 3, 5, 30, 0, DateTimeKind.Utc);
    private static readonly DateTime Second = new(2030, 11, 3, 6, 30, 0, DateTimeKind.Utc);

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
