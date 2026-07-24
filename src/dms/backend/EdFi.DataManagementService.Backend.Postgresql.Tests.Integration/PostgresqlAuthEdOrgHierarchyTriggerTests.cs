// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Tests.Integration.Common;
using FluentAssertions;
using Npgsql;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Postgresql.Tests.Integration;

/// <summary>
/// Verifies that the auth EdOrg hierarchy triggers emitted onto concrete EducationOrganization
/// tables correctly maintain <c>auth.EducationOrganizationIdToEducationOrganizationId</c> in
/// response to inserts and updates (and the leaf-delete regression path).
///
/// Covers DMS-1096 acceptance criterion: "Test(s) ensure that inserts/updates made to concrete
/// Education Organizations update the auth.EducationOrganizationIdToEducationOrganizationId
/// table accordingly."
/// </summary>
[TestFixture]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
public class Given_A_Provisioned_Postgresql_Database_With_Auth_EdOrg_Hierarchy_Triggers
{
    private const string FixtureRelativePath = "src/dms/backend/Fixtures/authoritative/ds-5.2";

    private PostgresqlGeneratedDdlTestDatabase _database = null!;
    private short _stateEducationAgencyResourceKeyId;
    private short _educationServiceCenterResourceKeyId;
    private short _localEducationAgencyResourceKeyId;
    private short _schoolResourceKeyId;
    private short _organizationDepartmentResourceKeyId;
    private short _localEducationAgencyCategoryDescriptorResourceKeyId;
    private long _localEducationAgencyCategoryDescriptorDocumentId;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        var fixture = PostgresqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(
            FixtureRelativePath,
            strict: true
        );
        _database = await PostgresqlGeneratedDdlTestDatabase.CreateProvisionedAsync(fixture.GeneratedDdl);

        _stateEducationAgencyResourceKeyId = await GetResourceKeyIdAsync("Ed-Fi", "StateEducationAgency");
        _educationServiceCenterResourceKeyId = await GetResourceKeyIdAsync("Ed-Fi", "EducationServiceCenter");
        _localEducationAgencyResourceKeyId = await GetResourceKeyIdAsync("Ed-Fi", "LocalEducationAgency");
        _schoolResourceKeyId = await GetResourceKeyIdAsync("Ed-Fi", "School");
        _organizationDepartmentResourceKeyId = await GetResourceKeyIdAsync("Ed-Fi", "OrganizationDepartment");
        _localEducationAgencyCategoryDescriptorResourceKeyId = await GetResourceKeyIdAsync(
            "Ed-Fi",
            "LocalEducationAgencyCategoryDescriptor"
        );
    }

    [SetUp]
    public async Task SetUp()
    {
        await _database.ResetAsync();

        // Every LEA insert requires a LocalEducationAgencyCategoryDescriptor_DescriptorId; seed one
        // fresh per test so the row is in scope after ResetAsync() truncates user tables.
        _localEducationAgencyCategoryDescriptorDocumentId = await InsertDescriptorAsync(
            documentUuid: Guid.Parse("aaaaaaaa-1111-1111-1111-aaaaaaaaaaaa"),
            resourceKeyId: _localEducationAgencyCategoryDescriptorResourceKeyId,
            discriminator: "Ed-Fi:LocalEducationAgencyCategoryDescriptor",
            uri: "uri://ed-fi.org/LocalEducationAgencyCategoryDescriptor#Independent",
            @namespace: "uri://ed-fi.org/LocalEducationAgencyCategoryDescriptor",
            codeValue: "Independent",
            shortDescription: "Independent"
        );
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_database is not null)
        {
            await _database.DisposeAsync();
        }
    }

    // ── Insert scenarios ───────────────────────────────────────────────

    [Test]
    public async Task It_creates_self_tuple_when_a_leaf_StateEducationAgency_is_inserted()
    {
        await InsertStateEducationAgencyAsync(
            documentUuid: Guid.Parse("c0000000-0000-0000-0000-000000000100"),
            stateEducationAgencyId: 100,
            nameOfInstitution: "Test SEA"
        );

        var tuples = await GetAuthTuplesAsync();

        tuples.Should().BeEquivalentTo(new[] { (Source: 100L, Target: 100L) });
    }

    [Test]
    public async Task It_creates_self_and_ancestor_tuples_when_a_hierarchical_LocalEducationAgency_is_inserted()
    {
        var seaDocumentId = await InsertStateEducationAgencyAsync(
            Guid.Parse("c0000000-0000-0000-0000-000000000100"),
            100,
            "Test SEA"
        );
        await InsertLocalEducationAgencyAsync(
            documentUuid: Guid.Parse("c0000000-0000-0000-0000-000000000500"),
            localEducationAgencyId: 500,
            localEducationAgencyCategoryDescriptorDocumentId: _localEducationAgencyCategoryDescriptorDocumentId,
            nameOfInstitution: "Test LEA",
            parentStateEducationAgencyDocumentId: seaDocumentId,
            parentStateEducationAgencyId: 100
        );

        var tuples = await GetAuthTuplesAsync();

        tuples
            .Should()
            .BeEquivalentTo(
                new[]
                {
                    (Source: 100L, Target: 100L),
                    (Source: 100L, Target: 500L),
                    (Source: 500L, Target: 500L),
                }
            );
    }

    [Test]
    public async Task It_creates_full_ancestor_chain_when_a_School_is_inserted_under_an_LEA()
    {
        var seaDocumentId = await InsertStateEducationAgencyAsync(
            Guid.Parse("c0000000-0000-0000-0000-000000000100"),
            100,
            "Test SEA"
        );
        var leaDocumentId = await InsertLocalEducationAgencyAsync(
            documentUuid: Guid.Parse("c0000000-0000-0000-0000-000000000500"),
            localEducationAgencyId: 500,
            localEducationAgencyCategoryDescriptorDocumentId: _localEducationAgencyCategoryDescriptorDocumentId,
            nameOfInstitution: "Test LEA",
            parentStateEducationAgencyDocumentId: seaDocumentId,
            parentStateEducationAgencyId: 100
        );
        await InsertSchoolAsync(
            documentUuid: Guid.Parse("c0000000-0000-0000-0000-000000000700"),
            schoolId: 700,
            nameOfInstitution: "Test School",
            parentLocalEducationAgencyDocumentId: leaDocumentId,
            parentLocalEducationAgencyId: 500
        );

        var tuples = await GetAuthTuplesAsync();

        tuples
            .Should()
            .BeEquivalentTo(
                new[]
                {
                    (Source: 100L, Target: 100L),
                    (Source: 100L, Target: 500L),
                    (Source: 100L, Target: 700L),
                    (Source: 500L, Target: 500L),
                    (Source: 500L, Target: 700L),
                    (Source: 700L, Target: 700L),
                }
            );
    }

    [Test]
    public async Task It_creates_self_tuple_only_when_an_LEA_is_inserted_without_a_parent_FK()
    {
        await InsertLocalEducationAgencyAsync(
            documentUuid: Guid.Parse("c0000000-0000-0000-0000-000000000500"),
            localEducationAgencyId: 500,
            localEducationAgencyCategoryDescriptorDocumentId: _localEducationAgencyCategoryDescriptorDocumentId,
            nameOfInstitution: "Orphan LEA"
        );

        var tuples = await GetAuthTuplesAsync();

        tuples.Should().BeEquivalentTo(new[] { (Source: 500L, Target: 500L) });
    }

    [Test]
    public async Task It_creates_ancestor_tuples_when_an_LEA_is_inserted_under_only_an_ESC()
    {
        // Exercises the ESC branch of the trigger's UNION-of-parents in isolation. Scenarios 2 and
        // 5 pin the SEA branch and the all-three case; a defect that only broke the ESC branch
        // would slip past both.
        var escDocumentId = await InsertEducationServiceCenterAsync(
            documentUuid: Guid.Parse("c0000000-0000-0000-0000-000000000300"),
            educationServiceCenterId: 300,
            nameOfInstitution: "Standalone ESC"
        );
        await InsertLocalEducationAgencyAsync(
            documentUuid: Guid.Parse("c0000000-0000-0000-0000-000000000500"),
            localEducationAgencyId: 500,
            localEducationAgencyCategoryDescriptorDocumentId: _localEducationAgencyCategoryDescriptorDocumentId,
            nameOfInstitution: "ESC-only LEA",
            parentEducationServiceCenterDocumentId: escDocumentId,
            parentEducationServiceCenterId: 300
        );

        var tuples = await GetAuthTuplesAsync();

        tuples
            .Should()
            .BeEquivalentTo(
                new[]
                {
                    (Source: 300L, Target: 300L),
                    (Source: 300L, Target: 500L),
                    (Source: 500L, Target: 500L),
                }
            );
    }

    [Test]
    public async Task It_creates_ancestor_tuples_when_an_LEA_is_inserted_under_only_a_parent_LEA()
    {
        // Exercises the parent-LEA branch of the trigger's UNION-of-parents in isolation.
        var parentLeaDocumentId = await InsertLocalEducationAgencyAsync(
            documentUuid: Guid.Parse("c0000000-0000-0000-0000-000000000400"),
            localEducationAgencyId: 400,
            localEducationAgencyCategoryDescriptorDocumentId: _localEducationAgencyCategoryDescriptorDocumentId,
            nameOfInstitution: "Parent LEA"
        );
        await InsertLocalEducationAgencyAsync(
            documentUuid: Guid.Parse("c0000000-0000-0000-0000-000000000500"),
            localEducationAgencyId: 500,
            localEducationAgencyCategoryDescriptorDocumentId: _localEducationAgencyCategoryDescriptorDocumentId,
            nameOfInstitution: "Parent-LEA-only Child LEA",
            parentLocalEducationAgencyDocumentId: parentLeaDocumentId,
            parentLocalEducationAgencyId: 400
        );

        var tuples = await GetAuthTuplesAsync();

        tuples
            .Should()
            .BeEquivalentTo(
                new[]
                {
                    (Source: 400L, Target: 400L),
                    (Source: 400L, Target: 500L),
                    (Source: 500L, Target: 500L),
                }
            );
    }

    [Test]
    public async Task It_creates_ancestor_tuples_for_each_present_parent_FK_when_an_LEA_has_multiple_parents()
    {
        var seaDocumentId = await InsertStateEducationAgencyAsync(
            Guid.Parse("c0000000-0000-0000-0000-000000000100"),
            100,
            "Test SEA"
        );
        var escDocumentId = await InsertEducationServiceCenterAsync(
            documentUuid: Guid.Parse("c0000000-0000-0000-0000-000000000300"),
            educationServiceCenterId: 300,
            nameOfInstitution: "Test ESC",
            parentStateEducationAgencyDocumentId: seaDocumentId,
            parentStateEducationAgencyId: 100
        );
        var parentLeaDocumentId = await InsertLocalEducationAgencyAsync(
            documentUuid: Guid.Parse("c0000000-0000-0000-0000-000000000400"),
            localEducationAgencyId: 400,
            localEducationAgencyCategoryDescriptorDocumentId: _localEducationAgencyCategoryDescriptorDocumentId,
            nameOfInstitution: "Parent LEA",
            parentStateEducationAgencyDocumentId: seaDocumentId,
            parentStateEducationAgencyId: 100
        );
        await InsertLocalEducationAgencyAsync(
            documentUuid: Guid.Parse("c0000000-0000-0000-0000-000000000500"),
            localEducationAgencyId: 500,
            localEducationAgencyCategoryDescriptorDocumentId: _localEducationAgencyCategoryDescriptorDocumentId,
            nameOfInstitution: "Child LEA",
            parentStateEducationAgencyDocumentId: seaDocumentId,
            parentStateEducationAgencyId: 100,
            parentEducationServiceCenterDocumentId: escDocumentId,
            parentEducationServiceCenterId: 300,
            parentLocalEducationAgencyDocumentId: parentLeaDocumentId,
            parentLocalEducationAgencyId: 400
        );

        var tuples = await GetAuthTuplesAsync();

        // Child LEA inherits ancestry from each of its three populated parent FK pairs (SEA, ESC,
        // parent-LEA), plus its own self-tuple.
        tuples
            .Should()
            .BeEquivalentTo(
                new[]
                {
                    (Source: 100L, Target: 100L),
                    (Source: 100L, Target: 300L),
                    (Source: 100L, Target: 400L),
                    (Source: 100L, Target: 500L),
                    (Source: 300L, Target: 300L),
                    (Source: 300L, Target: 500L),
                    (Source: 400L, Target: 400L),
                    (Source: 400L, Target: 500L),
                    (Source: 500L, Target: 500L),
                }
            );
    }

    [Test]
    public async Task It_creates_ancestor_tuples_when_an_OrganizationDepartment_is_inserted_under_an_SEA()
    {
        // OrganizationDepartment is the only concrete EdOrg in DS 5.2 whose parent FK uses the
        // abstract `ParentEducationOrganization_EducationOrganizationId` column shape. SEA/ESC/
        // LEA/School all use subtype-scoped parent FKs (`<Subtype>_<Subtype>Id`); the trigger
        // generator selects between the two shapes based on entity metadata, so this is the only
        // test that exercises the abstract-parent path on insert.
        var seaDocumentId = await InsertStateEducationAgencyAsync(
            Guid.Parse("c0000000-0000-0000-0000-000000000100"),
            100,
            "Test SEA"
        );
        await InsertOrganizationDepartmentAsync(
            documentUuid: Guid.Parse("c0000000-0000-0000-0000-000000000800"),
            organizationDepartmentId: 800,
            nameOfInstitution: "Test OrgDept",
            parentEducationOrganizationDocumentId: seaDocumentId,
            parentEducationOrganizationEducationOrganizationId: 100
        );

        var tuples = await GetAuthTuplesAsync();

        tuples
            .Should()
            .BeEquivalentTo(
                new[]
                {
                    (Source: 100L, Target: 100L),
                    (Source: 100L, Target: 800L),
                    (Source: 800L, Target: 800L),
                }
            );
    }

    // ── Update scenarios ───────────────────────────────────────────────

    [Test]
    public async Task It_rewrites_ancestor_tuples_when_an_LEA_parent_FK_is_changed_to_another_SEA()
    {
        var sea1DocumentId = await InsertStateEducationAgencyAsync(
            Guid.Parse("c0000000-0000-0000-0000-000000000100"),
            100,
            "SEA 1"
        );
        var sea2DocumentId = await InsertStateEducationAgencyAsync(
            Guid.Parse("c0000000-0000-0000-0000-000000000200"),
            200,
            "SEA 2"
        );
        await InsertLocalEducationAgencyAsync(
            documentUuid: Guid.Parse("c0000000-0000-0000-0000-000000000500"),
            localEducationAgencyId: 500,
            localEducationAgencyCategoryDescriptorDocumentId: _localEducationAgencyCategoryDescriptorDocumentId,
            nameOfInstitution: "Test LEA",
            parentStateEducationAgencyDocumentId: sea1DocumentId,
            parentStateEducationAgencyId: 100
        );

        await _database.ExecuteNonQueryAsync(
            """
            UPDATE "edfi"."LocalEducationAgency"
            SET "StateEducationAgency_DocumentId" = @newSeaDocumentId,
                "StateEducationAgency_StateEducationAgencyId" = @newSeaId
            WHERE "LocalEducationAgencyId" = @leaId;
            """,
            new NpgsqlParameter("newSeaDocumentId", sea2DocumentId),
            new NpgsqlParameter("newSeaId", 200L),
            new NpgsqlParameter("leaId", 500L)
        );

        var tuples = await GetAuthTuplesAsync();

        tuples
            .Should()
            .BeEquivalentTo(
                new[]
                {
                    (Source: 100L, Target: 100L),
                    (Source: 200L, Target: 200L),
                    (Source: 200L, Target: 500L),
                    (Source: 500L, Target: 500L),
                }
            );
    }

    [Test]
    public async Task It_rewrites_descendant_tuples_when_an_intermediate_LEA_parent_FK_changes()
    {
        var sea1DocumentId = await InsertStateEducationAgencyAsync(
            Guid.Parse("c0000000-0000-0000-0000-000000000100"),
            100,
            "SEA 1"
        );
        var sea2DocumentId = await InsertStateEducationAgencyAsync(
            Guid.Parse("c0000000-0000-0000-0000-000000000200"),
            200,
            "SEA 2"
        );
        var leaDocumentId = await InsertLocalEducationAgencyAsync(
            documentUuid: Guid.Parse("c0000000-0000-0000-0000-000000000500"),
            localEducationAgencyId: 500,
            localEducationAgencyCategoryDescriptorDocumentId: _localEducationAgencyCategoryDescriptorDocumentId,
            nameOfInstitution: "Test LEA",
            parentStateEducationAgencyDocumentId: sea1DocumentId,
            parentStateEducationAgencyId: 100
        );
        await InsertSchoolAsync(
            documentUuid: Guid.Parse("c0000000-0000-0000-0000-000000000700"),
            schoolId: 700,
            nameOfInstitution: "Test School",
            parentLocalEducationAgencyDocumentId: leaDocumentId,
            parentLocalEducationAgencyId: 500
        );

        await _database.ExecuteNonQueryAsync(
            """
            UPDATE "edfi"."LocalEducationAgency"
            SET "StateEducationAgency_DocumentId" = @newSeaDocumentId,
                "StateEducationAgency_StateEducationAgencyId" = @newSeaId
            WHERE "LocalEducationAgencyId" = @leaId;
            """,
            new NpgsqlParameter("newSeaDocumentId", sea2DocumentId),
            new NpgsqlParameter("newSeaId", 200L),
            new NpgsqlParameter("leaId", 500L)
        );

        var tuples = await GetAuthTuplesAsync();

        // SEA1 ancestry to both LEA and School is gone; SEA2 ancestry replaces it.
        tuples
            .Should()
            .BeEquivalentTo(
                new[]
                {
                    (Source: 100L, Target: 100L),
                    (Source: 200L, Target: 200L),
                    (Source: 200L, Target: 500L),
                    (Source: 200L, Target: 700L),
                    (Source: 500L, Target: 500L),
                    (Source: 500L, Target: 700L),
                    (Source: 700L, Target: 700L),
                }
            );
    }

    [Test]
    public async Task It_adds_ancestor_tuples_when_an_LEA_parent_FK_transitions_from_null_to_a_value()
    {
        var seaDocumentId = await InsertStateEducationAgencyAsync(
            Guid.Parse("c0000000-0000-0000-0000-000000000100"),
            100,
            "Test SEA"
        );
        await InsertLocalEducationAgencyAsync(
            documentUuid: Guid.Parse("c0000000-0000-0000-0000-000000000500"),
            localEducationAgencyId: 500,
            localEducationAgencyCategoryDescriptorDocumentId: _localEducationAgencyCategoryDescriptorDocumentId,
            nameOfInstitution: "Orphan LEA"
        );

        await _database.ExecuteNonQueryAsync(
            """
            UPDATE "edfi"."LocalEducationAgency"
            SET "StateEducationAgency_DocumentId" = @seaDocumentId,
                "StateEducationAgency_StateEducationAgencyId" = @seaId
            WHERE "LocalEducationAgencyId" = @leaId;
            """,
            new NpgsqlParameter("seaDocumentId", seaDocumentId),
            new NpgsqlParameter("seaId", 100L),
            new NpgsqlParameter("leaId", 500L)
        );

        var tuples = await GetAuthTuplesAsync();

        tuples
            .Should()
            .BeEquivalentTo(
                new[]
                {
                    (Source: 100L, Target: 100L),
                    (Source: 100L, Target: 500L),
                    (Source: 500L, Target: 500L),
                }
            );
    }

    [Test]
    public async Task It_removes_ancestor_tuples_when_an_LEA_parent_FK_transitions_from_a_value_to_null()
    {
        var seaDocumentId = await InsertStateEducationAgencyAsync(
            Guid.Parse("c0000000-0000-0000-0000-000000000100"),
            100,
            "Test SEA"
        );
        await InsertLocalEducationAgencyAsync(
            documentUuid: Guid.Parse("c0000000-0000-0000-0000-000000000500"),
            localEducationAgencyId: 500,
            localEducationAgencyCategoryDescriptorDocumentId: _localEducationAgencyCategoryDescriptorDocumentId,
            nameOfInstitution: "Test LEA",
            parentStateEducationAgencyDocumentId: seaDocumentId,
            parentStateEducationAgencyId: 100
        );

        await _database.ExecuteNonQueryAsync(
            """
            UPDATE "edfi"."LocalEducationAgency"
            SET "StateEducationAgency_DocumentId" = NULL,
                "StateEducationAgency_StateEducationAgencyId" = NULL
            WHERE "LocalEducationAgencyId" = @leaId;
            """,
            new NpgsqlParameter("leaId", 500L)
        );

        var tuples = await GetAuthTuplesAsync();

        tuples.Should().BeEquivalentTo(new[] { (Source: 100L, Target: 100L), (Source: 500L, Target: 500L) });
    }

    [Test]
    public async Task It_does_not_mutate_auth_tuples_when_an_LEA_parent_FK_is_updated_to_the_same_value()
    {
        // Pins the trigger's per-branch `(NEW.X IS NULL OR OLD.X <> NEW.X)` predicate: a no-op
        // UPDATE (OLD = NEW, both non-null) must leave auth.EducationOrganizationIdToEducationOrganizationId
        // untouched. A predicate inversion (`<>` → `=`) would mutate auth on every no-op write and
        // skip every real re-parent.
        var seaDocumentId = await InsertStateEducationAgencyAsync(
            Guid.Parse("c0000000-0000-0000-0000-000000000100"),
            100,
            "Test SEA"
        );
        await InsertLocalEducationAgencyAsync(
            documentUuid: Guid.Parse("c0000000-0000-0000-0000-000000000500"),
            localEducationAgencyId: 500,
            localEducationAgencyCategoryDescriptorDocumentId: _localEducationAgencyCategoryDescriptorDocumentId,
            nameOfInstitution: "Test LEA",
            parentStateEducationAgencyDocumentId: seaDocumentId,
            parentStateEducationAgencyId: 100
        );

        await _database.ExecuteNonQueryAsync(
            """
            UPDATE "edfi"."LocalEducationAgency"
            SET "StateEducationAgency_DocumentId" = @seaDocumentId,
                "StateEducationAgency_StateEducationAgencyId" = @seaId
            WHERE "LocalEducationAgencyId" = @leaId;
            """,
            new NpgsqlParameter("seaDocumentId", seaDocumentId),
            new NpgsqlParameter("seaId", 100L),
            new NpgsqlParameter("leaId", 500L)
        );

        var tuples = await GetAuthTuplesAsync();

        tuples
            .Should()
            .BeEquivalentTo(
                new[]
                {
                    (Source: 100L, Target: 100L),
                    (Source: 100L, Target: 500L),
                    (Source: 500L, Target: 500L),
                }
            );
    }

    [Test]
    public async Task It_adds_ancestor_tuples_when_an_LEA_parent_LEA_FK_transitions_from_null_to_a_value()
    {
        // Mirrors the SEA-branch NULL→value transition on the ParentLocalEducationAgency_* FK pair.
        // The trigger emits one structurally parallel `((OLD.X IS NULL AND NEW.X IS NOT NULL) OR
        // OLD.X <> NEW.X)` predicate per parent FK; SEA-only coverage leaves a defect in the
        // parent-LEA branch's predicate undetected.
        var seaDocumentId = await InsertStateEducationAgencyAsync(
            Guid.Parse("c0000000-0000-0000-0000-000000000100"),
            100,
            "Test SEA"
        );
        var parentLeaDocumentId = await InsertLocalEducationAgencyAsync(
            documentUuid: Guid.Parse("c0000000-0000-0000-0000-000000000400"),
            localEducationAgencyId: 400,
            localEducationAgencyCategoryDescriptorDocumentId: _localEducationAgencyCategoryDescriptorDocumentId,
            nameOfInstitution: "Parent LEA",
            parentStateEducationAgencyDocumentId: seaDocumentId,
            parentStateEducationAgencyId: 100
        );
        await InsertLocalEducationAgencyAsync(
            documentUuid: Guid.Parse("c0000000-0000-0000-0000-000000000500"),
            localEducationAgencyId: 500,
            localEducationAgencyCategoryDescriptorDocumentId: _localEducationAgencyCategoryDescriptorDocumentId,
            nameOfInstitution: "Orphan Child LEA"
        );

        await _database.ExecuteNonQueryAsync(
            """
            UPDATE "edfi"."LocalEducationAgency"
            SET "ParentLocalEducationAgency_DocumentId" = @parentLeaDocumentId,
                "ParentLocalEducationAgency_LocalEducationAgencyId" = @parentLeaId
            WHERE "LocalEducationAgencyId" = @leaId;
            """,
            new NpgsqlParameter("parentLeaDocumentId", parentLeaDocumentId),
            new NpgsqlParameter("parentLeaId", 400L),
            new NpgsqlParameter("leaId", 500L)
        );

        var tuples = await GetAuthTuplesAsync();

        // Both the direct parent-LEA ancestry (400 → 500) and the transitive SEA ancestry through
        // the parent LEA (100 → 500) are added.
        tuples
            .Should()
            .BeEquivalentTo(
                new[]
                {
                    (Source: 100L, Target: 100L),
                    (Source: 100L, Target: 400L),
                    (Source: 100L, Target: 500L),
                    (Source: 400L, Target: 400L),
                    (Source: 400L, Target: 500L),
                    (Source: 500L, Target: 500L),
                }
            );
    }

    [Test]
    public async Task It_removes_ancestor_tuples_when_an_LEA_parent_LEA_FK_transitions_from_a_value_to_null()
    {
        // Mirrors the SEA-branch value→NULL transition on the ParentLocalEducationAgency_* FK pair
        // to pin the per-branch `(NEW.X IS NULL OR OLD.X <> NEW.X)` predicate in the DELETE half
        // of the trigger.
        var seaDocumentId = await InsertStateEducationAgencyAsync(
            Guid.Parse("c0000000-0000-0000-0000-000000000100"),
            100,
            "Test SEA"
        );
        var parentLeaDocumentId = await InsertLocalEducationAgencyAsync(
            documentUuid: Guid.Parse("c0000000-0000-0000-0000-000000000400"),
            localEducationAgencyId: 400,
            localEducationAgencyCategoryDescriptorDocumentId: _localEducationAgencyCategoryDescriptorDocumentId,
            nameOfInstitution: "Parent LEA",
            parentStateEducationAgencyDocumentId: seaDocumentId,
            parentStateEducationAgencyId: 100
        );
        await InsertLocalEducationAgencyAsync(
            documentUuid: Guid.Parse("c0000000-0000-0000-0000-000000000500"),
            localEducationAgencyId: 500,
            localEducationAgencyCategoryDescriptorDocumentId: _localEducationAgencyCategoryDescriptorDocumentId,
            nameOfInstitution: "Child LEA",
            parentLocalEducationAgencyDocumentId: parentLeaDocumentId,
            parentLocalEducationAgencyId: 400
        );

        await _database.ExecuteNonQueryAsync(
            """
            UPDATE "edfi"."LocalEducationAgency"
            SET "ParentLocalEducationAgency_DocumentId" = NULL,
                "ParentLocalEducationAgency_LocalEducationAgencyId" = NULL
            WHERE "LocalEducationAgencyId" = @leaId;
            """,
            new NpgsqlParameter("leaId", 500L)
        );

        var tuples = await GetAuthTuplesAsync();

        // Both the direct parent-LEA → child (400, 500) and the transitive SEA → child (100, 500)
        // are removed; SEA/parent-LEA self/ancestor tuples and child LEA self-tuple remain.
        tuples
            .Should()
            .BeEquivalentTo(
                new[]
                {
                    (Source: 100L, Target: 100L),
                    (Source: 100L, Target: 400L),
                    (Source: 400L, Target: 400L),
                    (Source: 500L, Target: 500L),
                }
            );
    }

    [Test]
    public async Task It_rewrites_ancestor_tuples_when_an_ESC_parent_FK_is_changed_to_another_SEA()
    {
        // Pins that the EducationServiceCenter AuthHierarchy_Update trigger fires and correlates
        // its UNION-of-parents (SEA branch) on update. The LEA update scenarios don't cover this
        // codepath — ESC has its own emitted trigger function with a SEA-specific source predicate.
        var sea1DocumentId = await InsertStateEducationAgencyAsync(
            Guid.Parse("c0000000-0000-0000-0000-000000000100"),
            100,
            "SEA 1"
        );
        var sea2DocumentId = await InsertStateEducationAgencyAsync(
            Guid.Parse("c0000000-0000-0000-0000-000000000200"),
            200,
            "SEA 2"
        );
        await InsertEducationServiceCenterAsync(
            documentUuid: Guid.Parse("c0000000-0000-0000-0000-000000000300"),
            educationServiceCenterId: 300,
            nameOfInstitution: "Test ESC",
            parentStateEducationAgencyDocumentId: sea1DocumentId,
            parentStateEducationAgencyId: 100
        );

        await _database.ExecuteNonQueryAsync(
            """
            UPDATE "edfi"."EducationServiceCenter"
            SET "StateEducationAgency_DocumentId" = @newSeaDocumentId,
                "StateEducationAgency_StateEducationAgencyId" = @newSeaId
            WHERE "EducationServiceCenterId" = @escId;
            """,
            new NpgsqlParameter("newSeaDocumentId", sea2DocumentId),
            new NpgsqlParameter("newSeaId", 200L),
            new NpgsqlParameter("escId", 300L)
        );

        var tuples = await GetAuthTuplesAsync();

        tuples
            .Should()
            .BeEquivalentTo(
                new[]
                {
                    (Source: 100L, Target: 100L),
                    (Source: 200L, Target: 200L),
                    (Source: 200L, Target: 300L),
                    (Source: 300L, Target: 300L),
                }
            );
    }

    [Test]
    public async Task It_rewrites_ancestor_tuples_when_a_School_parent_FK_is_changed_to_another_LEA()
    {
        // Pins that the School AuthHierarchy_Update trigger fires on update. School has only one
        // parent FK (parent LEA) so this is the single branch of its UNION-of-parents.
        var seaDocumentId = await InsertStateEducationAgencyAsync(
            Guid.Parse("c0000000-0000-0000-0000-000000000100"),
            100,
            "Test SEA"
        );
        var lea1DocumentId = await InsertLocalEducationAgencyAsync(
            documentUuid: Guid.Parse("c0000000-0000-0000-0000-000000000400"),
            localEducationAgencyId: 400,
            localEducationAgencyCategoryDescriptorDocumentId: _localEducationAgencyCategoryDescriptorDocumentId,
            nameOfInstitution: "LEA 1",
            parentStateEducationAgencyDocumentId: seaDocumentId,
            parentStateEducationAgencyId: 100
        );
        var lea2DocumentId = await InsertLocalEducationAgencyAsync(
            documentUuid: Guid.Parse("c0000000-0000-0000-0000-000000000500"),
            localEducationAgencyId: 500,
            localEducationAgencyCategoryDescriptorDocumentId: _localEducationAgencyCategoryDescriptorDocumentId,
            nameOfInstitution: "LEA 2",
            parentStateEducationAgencyDocumentId: seaDocumentId,
            parentStateEducationAgencyId: 100
        );
        await InsertSchoolAsync(
            documentUuid: Guid.Parse("c0000000-0000-0000-0000-000000000700"),
            schoolId: 700,
            nameOfInstitution: "Test School",
            parentLocalEducationAgencyDocumentId: lea1DocumentId,
            parentLocalEducationAgencyId: 400
        );

        await _database.ExecuteNonQueryAsync(
            """
            UPDATE "edfi"."School"
            SET "LocalEducationAgency_DocumentId" = @newLeaDocumentId,
                "LocalEducationAgency_LocalEducationAgencyId" = @newLeaId
            WHERE "SchoolId" = @schoolId;
            """,
            new NpgsqlParameter("newLeaDocumentId", lea2DocumentId),
            new NpgsqlParameter("newLeaId", 500L),
            new NpgsqlParameter("schoolId", 700L)
        );

        var tuples = await GetAuthTuplesAsync();

        // The shared SEA ancestor (100) remains the school's ancestor through the new LEA path,
        // so (100, 700) stays. LEA1's ancestry of the school is removed; LEA2's is added.
        tuples
            .Should()
            .BeEquivalentTo(
                new[]
                {
                    (Source: 100L, Target: 100L),
                    (Source: 100L, Target: 400L),
                    (Source: 100L, Target: 500L),
                    (Source: 100L, Target: 700L),
                    (Source: 400L, Target: 400L),
                    (Source: 500L, Target: 500L),
                    (Source: 500L, Target: 700L),
                    (Source: 700L, Target: 700L),
                }
            );
    }

    [Test]
    public async Task It_rewrites_ancestor_tuples_when_an_OrganizationDepartment_parent_FK_is_changed_to_another_SEA()
    {
        // Pins the OrganizationDepartment AuthHierarchy_Update trigger and — uniquely among the
        // covered EdOrgs — the abstract-parent FK update path
        // (`ParentEducationOrganization_EducationOrganizationId`). A defect in the generator's
        // abstract-parent column resolution for `_Update` triggers would slip past every other
        // scenario.
        var sea1DocumentId = await InsertStateEducationAgencyAsync(
            Guid.Parse("c0000000-0000-0000-0000-000000000100"),
            100,
            "SEA 1"
        );
        var sea2DocumentId = await InsertStateEducationAgencyAsync(
            Guid.Parse("c0000000-0000-0000-0000-000000000200"),
            200,
            "SEA 2"
        );
        await InsertOrganizationDepartmentAsync(
            documentUuid: Guid.Parse("c0000000-0000-0000-0000-000000000800"),
            organizationDepartmentId: 800,
            nameOfInstitution: "Test OrgDept",
            parentEducationOrganizationDocumentId: sea1DocumentId,
            parentEducationOrganizationEducationOrganizationId: 100
        );

        await _database.ExecuteNonQueryAsync(
            """
            UPDATE "edfi"."OrganizationDepartment"
            SET "ParentEducationOrganization_DocumentId" = @newParentDocumentId,
                "ParentEducationOrganization_EducationOrganizationId" = @newParentId
            WHERE "OrganizationDepartmentId" = @organizationDepartmentId;
            """,
            new NpgsqlParameter("newParentDocumentId", sea2DocumentId),
            new NpgsqlParameter("newParentId", 200L),
            new NpgsqlParameter("organizationDepartmentId", 800L)
        );

        var tuples = await GetAuthTuplesAsync();

        tuples
            .Should()
            .BeEquivalentTo(
                new[]
                {
                    (Source: 100L, Target: 100L),
                    (Source: 200L, Target: 200L),
                    (Source: 200L, Target: 800L),
                    (Source: 800L, Target: 800L),
                }
            );
    }

    [Test]
    public async Task It_adds_ancestor_tuples_when_an_OrganizationDepartment_parent_FK_transitions_from_null_to_a_value()
    {
        // Pins the null→value transition on OrganizationDepartment's abstract-parent column
        // (`ParentEducationOrganization_EducationOrganizationId`). The LEA tests structurally cover
        // the predicate template against subtype-scoped columns; this test exercises it against the
        // unique abstract-column resolution.
        var seaDocumentId = await InsertStateEducationAgencyAsync(
            Guid.Parse("c0000000-0000-0000-0000-000000000100"),
            100,
            "Test SEA"
        );
        await InsertOrganizationDepartmentAsync(
            documentUuid: Guid.Parse("c0000000-0000-0000-0000-000000000800"),
            organizationDepartmentId: 800,
            nameOfInstitution: "Orphan OrgDept"
        );

        await _database.ExecuteNonQueryAsync(
            """
            UPDATE "edfi"."OrganizationDepartment"
            SET "ParentEducationOrganization_DocumentId" = @parentDocumentId,
                "ParentEducationOrganization_EducationOrganizationId" = @parentId
            WHERE "OrganizationDepartmentId" = @organizationDepartmentId;
            """,
            new NpgsqlParameter("parentDocumentId", seaDocumentId),
            new NpgsqlParameter("parentId", 100L),
            new NpgsqlParameter("organizationDepartmentId", 800L)
        );

        var tuples = await GetAuthTuplesAsync();

        tuples
            .Should()
            .BeEquivalentTo(
                new[]
                {
                    (Source: 100L, Target: 100L),
                    (Source: 100L, Target: 800L),
                    (Source: 800L, Target: 800L),
                }
            );
    }

    [Test]
    public async Task It_removes_ancestor_tuples_when_an_OrganizationDepartment_parent_FK_transitions_from_a_value_to_null()
    {
        // Pins the value→null transition on OrganizationDepartment's abstract-parent column to
        // exercise the DELETE-half predicate `(NEW.X IS NULL OR OLD.X <> NEW.X)` against the
        // abstract-column resolution.
        var seaDocumentId = await InsertStateEducationAgencyAsync(
            Guid.Parse("c0000000-0000-0000-0000-000000000100"),
            100,
            "Test SEA"
        );
        await InsertOrganizationDepartmentAsync(
            documentUuid: Guid.Parse("c0000000-0000-0000-0000-000000000800"),
            organizationDepartmentId: 800,
            nameOfInstitution: "Test OrgDept",
            parentEducationOrganizationDocumentId: seaDocumentId,
            parentEducationOrganizationEducationOrganizationId: 100
        );

        await _database.ExecuteNonQueryAsync(
            """
            UPDATE "edfi"."OrganizationDepartment"
            SET "ParentEducationOrganization_DocumentId" = NULL,
                "ParentEducationOrganization_EducationOrganizationId" = NULL
            WHERE "OrganizationDepartmentId" = @organizationDepartmentId;
            """,
            new NpgsqlParameter("organizationDepartmentId", 800L)
        );

        var tuples = await GetAuthTuplesAsync();

        tuples.Should().BeEquivalentTo(new[] { (Source: 100L, Target: 100L), (Source: 800L, Target: 800L) });
    }

    // ── Delete scenarios (regression coverage; not part of canonical AC) ──

    [Test]
    public async Task It_removes_all_tuples_for_a_deleted_leaf_School()
    {
        var seaDocumentId = await InsertStateEducationAgencyAsync(
            Guid.Parse("c0000000-0000-0000-0000-000000000100"),
            100,
            "Test SEA"
        );
        var leaDocumentId = await InsertLocalEducationAgencyAsync(
            documentUuid: Guid.Parse("c0000000-0000-0000-0000-000000000500"),
            localEducationAgencyId: 500,
            localEducationAgencyCategoryDescriptorDocumentId: _localEducationAgencyCategoryDescriptorDocumentId,
            nameOfInstitution: "Test LEA",
            parentStateEducationAgencyDocumentId: seaDocumentId,
            parentStateEducationAgencyId: 100
        );
        await InsertSchoolAsync(
            documentUuid: Guid.Parse("c0000000-0000-0000-0000-000000000700"),
            schoolId: 700,
            nameOfInstitution: "Test School",
            parentLocalEducationAgencyDocumentId: leaDocumentId,
            parentLocalEducationAgencyId: 500
        );

        await _database.ExecuteNonQueryAsync(
            """
            DELETE FROM "edfi"."School" WHERE "SchoolId" = @schoolId;
            """,
            new NpgsqlParameter("schoolId", 700L)
        );

        var tuples = await GetAuthTuplesAsync();

        // All rows with Target = School (700) removed; SEA and LEA self/ancestor tuples intact.
        tuples
            .Should()
            .BeEquivalentTo(
                new[]
                {
                    (Source: 100L, Target: 100L),
                    (Source: 100L, Target: 500L),
                    (Source: 500L, Target: 500L),
                }
            );
    }

    // ── Seed helpers ───────────────────────────────────────────────────

    private async Task<short> GetResourceKeyIdAsync(string projectName, string resourceName)
    {
        return await _database.ExecuteScalarAsync<short>(
            """
            SELECT "ResourceKeyId"
            FROM "dms"."ResourceKey"
            WHERE "ProjectName" = @projectName
              AND "ResourceName" = @resourceName;
            """,
            new NpgsqlParameter("projectName", projectName),
            new NpgsqlParameter("resourceName", resourceName)
        );
    }

    private async Task<long> InsertDocumentAsync(Guid documentUuid, short resourceKeyId)
    {
        return await _database.ExecuteScalarAsync<long>(
            """
            INSERT INTO "dms"."Document" ("DocumentUuid", "ResourceKeyId")
            VALUES (@documentUuid, @resourceKeyId)
            RETURNING "DocumentId";
            """,
            new NpgsqlParameter("documentUuid", documentUuid),
            new NpgsqlParameter("resourceKeyId", resourceKeyId)
        );
    }

    private async Task<long> InsertDescriptorAsync(
        Guid documentUuid,
        short resourceKeyId,
        string discriminator,
        string uri,
        string @namespace,
        string codeValue,
        string shortDescription
    )
    {
        var documentId = await InsertDocumentAsync(documentUuid, resourceKeyId);

        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO "dms"."Descriptor" (
                "DocumentId",
                "ResourceKeyId",
                "Namespace",
                "CodeValue",
                "ShortDescription",
                "Description",
                "Discriminator",
                "Uri"
            )
            VALUES (
                @documentId,
                @resourceKeyId,
                @namespace,
                @codeValue,
                @shortDescription,
                @description,
                @discriminator,
                @uri
            );
            """,
            new NpgsqlParameter("documentId", documentId),
            new NpgsqlParameter("resourceKeyId", resourceKeyId),
            new NpgsqlParameter("namespace", @namespace),
            new NpgsqlParameter("codeValue", codeValue),
            new NpgsqlParameter("shortDescription", shortDescription),
            new NpgsqlParameter("description", shortDescription),
            new NpgsqlParameter("discriminator", discriminator),
            new NpgsqlParameter("uri", uri)
        );

        return documentId;
    }

    /// <summary>
    /// Inserts a row into <c>dms.Document</c> and <c>edfi.StateEducationAgency</c>. The
    /// abstract-identity triggers on the concrete table populate
    /// <c>edfi.EducationOrganizationIdentity</c> automatically; do not pre-seed it.
    /// </summary>
    private async Task<long> InsertStateEducationAgencyAsync(
        Guid documentUuid,
        long stateEducationAgencyId,
        string nameOfInstitution
    )
    {
        var documentId = await InsertDocumentAsync(documentUuid, _stateEducationAgencyResourceKeyId);

        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO "edfi"."StateEducationAgency" (
                "DocumentId",
                "StateEducationAgencyId",
                "NameOfInstitution"
            )
            VALUES (@documentId, @stateEducationAgencyId, @nameOfInstitution);
            """,
            new NpgsqlParameter("documentId", documentId),
            new NpgsqlParameter("stateEducationAgencyId", stateEducationAgencyId),
            new NpgsqlParameter("nameOfInstitution", nameOfInstitution)
        );

        return documentId;
    }

    private async Task<long> InsertEducationServiceCenterAsync(
        Guid documentUuid,
        long educationServiceCenterId,
        string nameOfInstitution,
        long? parentStateEducationAgencyDocumentId = null,
        long? parentStateEducationAgencyId = null
    )
    {
        var documentId = await InsertDocumentAsync(documentUuid, _educationServiceCenterResourceKeyId);

        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO "edfi"."EducationServiceCenter" (
                "DocumentId",
                "EducationServiceCenterId",
                "NameOfInstitution",
                "StateEducationAgency_DocumentId",
                "StateEducationAgency_StateEducationAgencyId"
            )
            VALUES (
                @documentId,
                @educationServiceCenterId,
                @nameOfInstitution,
                @parentSeaDocumentId,
                @parentSeaId
            );
            """,
            new NpgsqlParameter("documentId", documentId),
            new NpgsqlParameter("educationServiceCenterId", educationServiceCenterId),
            new NpgsqlParameter("nameOfInstitution", nameOfInstitution),
            new NpgsqlParameter(
                "parentSeaDocumentId",
                (object?)parentStateEducationAgencyDocumentId ?? DBNull.Value
            ),
            new NpgsqlParameter("parentSeaId", (object?)parentStateEducationAgencyId ?? DBNull.Value)
        );

        return documentId;
    }

    /// <summary>
    /// Inserts a row into <c>dms.Document</c> and <c>edfi.LocalEducationAgency</c>. Every parent
    /// FK is a nullable subtype-specific pair (<c>_DocumentId</c> + scoped natural-key id); a
    /// CHECK constraint on the table requires both members of each pair to be NULL or both
    /// NOT NULL. The category descriptor is required.
    /// </summary>
    private async Task<long> InsertLocalEducationAgencyAsync(
        Guid documentUuid,
        long localEducationAgencyId,
        long localEducationAgencyCategoryDescriptorDocumentId,
        string nameOfInstitution,
        long? parentStateEducationAgencyDocumentId = null,
        long? parentStateEducationAgencyId = null,
        long? parentEducationServiceCenterDocumentId = null,
        long? parentEducationServiceCenterId = null,
        long? parentLocalEducationAgencyDocumentId = null,
        long? parentLocalEducationAgencyId = null
    )
    {
        var documentId = await InsertDocumentAsync(documentUuid, _localEducationAgencyResourceKeyId);

        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO "edfi"."LocalEducationAgency" (
                "DocumentId",
                "LocalEducationAgencyId",
                "LocalEducationAgencyCategoryDescriptor_DescriptorId",
                "NameOfInstitution",
                "StateEducationAgency_DocumentId",
                "StateEducationAgency_StateEducationAgencyId",
                "EducationServiceCenter_DocumentId",
                "EducationServiceCenter_EducationServiceCenterId",
                "ParentLocalEducationAgency_DocumentId",
                "ParentLocalEducationAgency_LocalEducationAgencyId"
            )
            VALUES (
                @documentId,
                @localEducationAgencyId,
                @categoryDescriptorDocumentId,
                @nameOfInstitution,
                @parentSeaDocumentId,
                @parentSeaId,
                @parentEscDocumentId,
                @parentEscId,
                @parentLeaDocumentId,
                @parentLeaId
            );
            """,
            new NpgsqlParameter("documentId", documentId),
            new NpgsqlParameter("localEducationAgencyId", localEducationAgencyId),
            new NpgsqlParameter(
                "categoryDescriptorDocumentId",
                localEducationAgencyCategoryDescriptorDocumentId
            ),
            new NpgsqlParameter("nameOfInstitution", nameOfInstitution),
            new NpgsqlParameter(
                "parentSeaDocumentId",
                (object?)parentStateEducationAgencyDocumentId ?? DBNull.Value
            ),
            new NpgsqlParameter("parentSeaId", (object?)parentStateEducationAgencyId ?? DBNull.Value),
            new NpgsqlParameter(
                "parentEscDocumentId",
                (object?)parentEducationServiceCenterDocumentId ?? DBNull.Value
            ),
            new NpgsqlParameter("parentEscId", (object?)parentEducationServiceCenterId ?? DBNull.Value),
            new NpgsqlParameter(
                "parentLeaDocumentId",
                (object?)parentLocalEducationAgencyDocumentId ?? DBNull.Value
            ),
            new NpgsqlParameter("parentLeaId", (object?)parentLocalEducationAgencyId ?? DBNull.Value)
        );

        return documentId;
    }

    private async Task<long> InsertSchoolAsync(
        Guid documentUuid,
        long schoolId,
        string nameOfInstitution,
        long parentLocalEducationAgencyDocumentId,
        long parentLocalEducationAgencyId
    )
    {
        var documentId = await InsertDocumentAsync(documentUuid, _schoolResourceKeyId);

        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO "edfi"."School" (
                "DocumentId",
                "SchoolId",
                "NameOfInstitution",
                "LocalEducationAgency_DocumentId",
                "LocalEducationAgency_LocalEducationAgencyId"
            )
            VALUES (
                @documentId,
                @schoolId,
                @nameOfInstitution,
                @parentLeaDocumentId,
                @parentLeaId
            );
            """,
            new NpgsqlParameter("documentId", documentId),
            new NpgsqlParameter("schoolId", schoolId),
            new NpgsqlParameter("nameOfInstitution", nameOfInstitution),
            new NpgsqlParameter("parentLeaDocumentId", parentLocalEducationAgencyDocumentId),
            new NpgsqlParameter("parentLeaId", parentLocalEducationAgencyId)
        );

        return documentId;
    }

    /// <summary>
    /// Inserts a row into <c>dms.Document</c> and <c>edfi.OrganizationDepartment</c>. Unlike the
    /// other concrete EdOrgs, OrganizationDepartment's parent FK references the abstract
    /// <c>edfi.EducationOrganizationIdentity</c> via the generic
    /// <c>ParentEducationOrganization_EducationOrganizationId</c> column rather than a
    /// subtype-scoped pair. A CHECK constraint on the table requires both members of the parent
    /// FK pair to be NULL or both NOT NULL.
    /// </summary>
    private async Task<long> InsertOrganizationDepartmentAsync(
        Guid documentUuid,
        long organizationDepartmentId,
        string nameOfInstitution,
        long? parentEducationOrganizationDocumentId = null,
        long? parentEducationOrganizationEducationOrganizationId = null
    )
    {
        var documentId = await InsertDocumentAsync(documentUuid, _organizationDepartmentResourceKeyId);

        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO "edfi"."OrganizationDepartment" (
                "DocumentId",
                "OrganizationDepartmentId",
                "NameOfInstitution",
                "ParentEducationOrganization_DocumentId",
                "ParentEducationOrganization_EducationOrganizationId"
            )
            VALUES (
                @documentId,
                @organizationDepartmentId,
                @nameOfInstitution,
                @parentEdOrgDocumentId,
                @parentEdOrgId
            );
            """,
            new NpgsqlParameter("documentId", documentId),
            new NpgsqlParameter("organizationDepartmentId", organizationDepartmentId),
            new NpgsqlParameter("nameOfInstitution", nameOfInstitution),
            new NpgsqlParameter(
                "parentEdOrgDocumentId",
                (object?)parentEducationOrganizationDocumentId ?? DBNull.Value
            ),
            new NpgsqlParameter(
                "parentEdOrgId",
                (object?)parentEducationOrganizationEducationOrganizationId ?? DBNull.Value
            )
        );

        return documentId;
    }

    /// <summary>
    /// Returns the current contents of <c>auth.EducationOrganizationIdToEducationOrganizationId</c>
    /// as a sorted list of <c>(SourceEducationOrganizationId, TargetEducationOrganizationId)</c>
    /// tuples. The auth columns are <c>bigint</c>, so tuple members are <see cref="long"/>.
    /// Assertions should match by value — never by row index.
    /// </summary>
    private async Task<IReadOnlyList<(long Source, long Target)>> GetAuthTuplesAsync()
    {
        var rows = await _database.QueryRowsAsync(
            """
            SELECT "SourceEducationOrganizationId", "TargetEducationOrganizationId"
            FROM "auth"."EducationOrganizationIdToEducationOrganizationId"
            ORDER BY 1, 2;
            """
        );

        return
        [
            .. rows.Select(row =>
                (
                    Source: Convert.ToInt64(row["SourceEducationOrganizationId"]),
                    Target: Convert.ToInt64(row["TargetEducationOrganizationId"])
                )
            ),
        ];
    }
}
