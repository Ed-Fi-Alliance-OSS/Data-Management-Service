// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Tests.Common;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using Npgsql;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Postgresql.Tests.Integration;

/// <summary>
/// Runs <see cref="RootIdentityUpdateShapeScenarios"/> on PostgreSQL against the authoritative DS 5.2
/// fixture through the production repository.
/// </summary>
[TestFixture]
[NonParallelizable]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
public class Given_A_Postgresql_Root_Identity_Update_Shape_With_The_Authoritative_Ds52_Fixture
{
    private PostgresqlRelationalQueryAuthorizationTestContext _context = null!;
    private Harness _harness = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _context = new PostgresqlRelationalQueryAuthorizationTestContext();
        await _context.InitializeAsync(
            RootIdentityUpdateShapeScenarios.FixtureRelativePath,
            strict: true,
            replaceReadTargetLookup: false
        );
        _harness = new Harness(_context);
    }

    [SetUp]
    public async Task SetUp()
    {
        await _context.Database.ResetAsync();
        await RootIdentityUpdateShapeScenarios.SeedAsync(_harness);
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_context is not null)
        {
            await _context.DisposeAsync();
        }
    }

    [Test]
    public Task It_omits_the_session_identity_assignments_when_the_identity_is_unchanged() =>
        RootIdentityUpdateShapeScenarios.AssertUnchangedIdentitySessionPutOmitsIdentityAssignmentsAsync(
            _harness
        );

    [Test]
    public Task It_assigns_the_full_set_list_and_cascades_an_allowed_session_identity_change() =>
        RootIdentityUpdateShapeScenarios.AssertAllowedSessionIdentityChangeUsesFullShapeAndCascadesAsync(
            _harness
        );

    [Test]
    public Task It_rejects_a_disallowed_identity_change_without_mutation() =>
        RootIdentityUpdateShapeScenarios.AssertDisallowedIdentityChangeFailsWithoutMutationAsync(_harness);

    [Test]
    public Task It_omits_the_canonical_stored_column_of_a_unified_key_identity() =>
        RootIdentityUpdateShapeScenarios.AssertUnchangedUnifiedKeyIdentityOmitsCanonicalStoredColumnAsync(
            _harness
        );

    [Test]
    public Task It_issues_no_root_update_for_an_identical_put() =>
        RootIdentityUpdateShapeScenarios.AssertIdenticalSessionPutIssuesNoRootUpdateAsync(_harness);

    [Test]
    public Task It_authorizes_a_related_claim_and_uses_the_reduced_shape() =>
        RootIdentityUpdateShapeScenarios.AssertAuthorizedUnchangedIdentitySessionPutUsesReducedShapeAsync(
            _harness
        );

    [Test]
    public Task It_denies_an_unrelated_claim_without_mutation() =>
        RootIdentityUpdateShapeScenarios.AssertUnauthorizedUnchangedIdentitySessionPutIsDeniedWithoutMutationAsync(
            _harness
        );

    [Test]
    public Task It_denies_moving_the_session_to_a_school_the_claim_is_not_related_to_without_mutation() =>
        RootIdentityUpdateShapeScenarios.AssertUnauthorizedSessionIdentityMoveIsDeniedWithoutMutationAsync(
            _harness
        );

    private sealed class Harness(PostgresqlRelationalQueryAuthorizationTestContext context)
        : IRootIdentityUpdateShapeHarness
    {
        public MappingSet MappingSet => context.MappingSet;

        public IReadOnlyList<string> RecordedWriteCommandTexts =>
            [.. context.RecordedWriteCommands.Select(static command => command.CommandText)];

        public Task SeedDescriptorAsync(
            Guid documentUuid,
            string resourceName,
            string @namespace,
            string codeValue,
            string shortDescription
        ) => context.SeedDescriptorAsync(documentUuid, resourceName, @namespace, codeValue, shortDescription);

        public Task InsertAuthEdgeAsync(
            long sourceEducationOrganizationId,
            long targetEducationOrganizationId
        ) => context.InsertAuthEdgeAsync(sourceEducationOrganizationId, targetEducationOrganizationId);

        public Task<UpsertResult> UpsertAsync(
            string resourceName,
            JsonNode requestBody,
            DocumentUuid documentUuid
        ) => context.UpsertAsync("ed-fi", resourceName, requestBody, documentUuid, $"post-{resourceName}");

        public Task<UpdateResult> UpdateAsync(
            string resourceName,
            JsonNode requestBody,
            DocumentUuid documentUuid,
            IReadOnlyList<long>? claimEducationOrganizationIds = null,
            IReadOnlyList<string>? strategyNames = null
        ) =>
            context.UpdateAsync(
                "ed-fi",
                resourceName,
                requestBody,
                documentUuid,
                $"put-{resourceName}",
                claimEducationOrganizationIds,
                strategyNames
            );

        public void ResetRecorder() => context.ResetRecorder();

        public Task<AuthorizationWriteSideEffectState> ReadSideEffectStateAsync(
            string resourceName,
            DocumentUuid documentUuid
        ) => context.ReadSideEffectStateAsync("ed-fi", resourceName, documentUuid);

        public Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ReadTrackedChangesAsync(
            string resourceName,
            DocumentUuid documentUuid
        ) =>
            context.Database.QueryRowsAsync(
                $"""
                SELECT *
                FROM "tracked_changes_edfi"."{resourceName}"
                WHERE "Id" = @documentUuid
                ORDER BY "ChangeVersion";
                """,
                new NpgsqlParameter("documentUuid", documentUuid.Value)
            );
    }
}
