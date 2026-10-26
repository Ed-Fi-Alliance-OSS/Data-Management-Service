// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Dapper;
using EdFi.DmsConfigurationService.Backend.Postgresql.Repositories;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.Backend.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.Tests.Integration;

/// <summary>
/// The PostgreSQL state read is one statement, so it sees one snapshot: an uncommitted scope change
/// is invisible and its commit becomes visible all at once.
/// </summary>
[TestFixture]
public class Given_a_scope_change_not_yet_committed : ApiClientEducationOrganizationRepositoryTests
{
    private ApiClientEducationOrganizationState _whileUncommitted = null!;
    private ApiClientEducationOrganizationState _afterCommit = null!;

    [SetUp]
    public async Task Setup()
    {
        await SeedAsync(new TenantContextProvider());

        await using NpgsqlConnection writer = new(Configuration.DatabaseOptions.Value.DatabaseConnection);
        await writer.OpenAsync();
        await using NpgsqlTransaction transaction = await writer.BeginTransactionAsync();

        // What a narrowing to {1002} writes, held open
        await writer.ExecuteAsync(
            """
            UPDATE "dmscs"."ApiClient" SET "InheritsApplicationEducationOrganizations" = false WHERE "Id" = @Id;
            DELETE FROM "dmscs"."ApiClientEducationOrganization"
            WHERE "ApiClientId" = @Id AND "EducationOrganizationId" <> 1002;
            """,
            new { Id = InheritingClientId },
            transaction
        );

        _whileUncommitted = await StateAsync(InheritingClientId);
        await transaction.CommitAsync();
        _afterCommit = await StateAsync(InheritingClientId);
    }

    [Test]
    public void It_reads_the_committed_inheriting_state_while_the_change_is_open() =>
        ShouldMatch(
            _whileUncommitted,
            State(ScopedApplicationId, true, [1001, 1002, 1003], [1001, 1002, 1003])
        );

    [Test]
    public void It_reads_the_whole_narrowed_state_once_the_change_commits() =>
        ShouldMatch(_afterCommit, State(ScopedApplicationId, false, [1002], [1002]));
}

/// <summary>
/// 05:30 and 06:30 UTC on 2030-11-03 are both 01:30 in New York, where clocks fall back that
/// morning, so a sync-pending time stored as a session wall clock could not keep them apart.
/// </summary>
[TestFixture]
public class Given_sync_pending_times_written_in_a_session_outside_UTC
    : ApiClientEducationOrganizationRepositoryTests
{
    private static readonly DateTime BeforeFallBack = new(2030, 11, 3, 5, 30, 0, DateTimeKind.Utc);
    private static readonly DateTime AfterFallBack = new(2030, 11, 3, 6, 30, 0, DateTimeKind.Utc);

    private ApiClientEducationOrganizationState _beforeFallBackState = null!;
    private ApiClientEducationOrganizationState _afterFallBackState = null!;
    private ApiClientEducationOrganizationState _afterFallBackStateInUtcSession = null!;

    [SetUp]
    public async Task Setup()
    {
        await SeedAsync(new TenantContextProvider());

        NpgsqlConnectionStringBuilder newYorkSession = new(
            Configuration.DatabaseOptions.Value.DatabaseConnection
        )
        {
            Timezone = "America/New_York",
        };
        ApiClientRepository newYorkRepository = new(
            Options.Create(
                new DatabaseOptions
                {
                    DatabaseConnection = newYorkSession.ConnectionString,
                    EncryptionKey = Configuration.DatabaseOptions.Value.EncryptionKey,
                }
            ),
            NullLogger<ApiClientRepository>.Instance,
            new TestAuditContext(),
            new TenantContextProvider()
        );

        await newYorkRepository.SetApiClientIdentityProviderSyncPending(NarrowedClientId, BeforeFallBack);
        await newYorkRepository.SetApiClientIdentityProviderSyncPending(
            SecondNarrowedClientId,
            AfterFallBack
        );

        _beforeFallBackState = (
            (ApiClientEducationOrganizationStateResult.Success)
                await newYorkRepository.GetApiClientEducationOrganizationState(NarrowedClientId)
        ).State;
        _afterFallBackState = (
            (ApiClientEducationOrganizationStateResult.Success)
                await newYorkRepository.GetApiClientEducationOrganizationState(SecondNarrowedClientId)
        ).State;
        _afterFallBackStateInUtcSession = await StateAsync(SecondNarrowedClientId);
    }

    [Test]
    public void It_reads_back_the_instant_before_the_fall_back() =>
        _beforeFallBackState.IdentityProviderSyncPendingSince.Should().Be(BeforeFallBack);

    [Test]
    public void It_reads_back_the_instant_after_the_fall_back() =>
        _afterFallBackState.IdentityProviderSyncPendingSince.Should().Be(AfterFallBack);

    [Test]
    public void It_reads_the_times_as_UTC() =>
        new[] { _beforeFallBackState, _afterFallBackState }
            .Should()
            .OnlyContain(state => state.IdentityProviderSyncPendingSince!.Value.Kind == DateTimeKind.Utc);

    [Test]
    public void It_reads_the_same_instant_in_a_UTC_session() =>
        _afterFallBackStateInUtcSession.IdentityProviderSyncPendingSince.Should().Be(AfterFallBack);
}
