// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Tests.Integration.Common;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Mssql.Tests.Integration;

/// <summary>
/// MSSQL counterpart to the PG auth EdOrg hierarchy trigger tests. MSSQL triggers are
/// statement-level (<c>AFTER INSERT/UPDATE</c> processing <c>inserted</c>/<c>deleted</c>
/// pseudo-tables), which is a different code path from PG's <c>FOR EACH ROW</c>; this
/// fixture mirrors the ten PG scenarios and adds bulk-DML coverage (multi-row INSERT and
/// multi-row UPDATE) that the row-level PG triggers do not require.
///
/// Covers DMS-1096 acceptance criterion: "Test(s) ensure that inserts/updates made to
/// concrete Education Organizations update the
/// auth.EducationOrganizationIdToEducationOrganizationId table accordingly."
/// </summary>
[TestFixture]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
[Category(MssqlCiShards.Shard1)]
public class Given_A_Provisioned_Mssql_Database_With_Auth_EdOrg_Hierarchy_Triggers
{
    private const string FixtureRelativePath = "src/dms/backend/Fixtures/authoritative/ds-5.2";

    private IMssqlGeneratedDdlBaselineLease _databaseLease = null!;
    private MssqlGeneratedDdlTestDatabase _database = null!;
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
        if (!MssqlTestDatabaseHelper.IsConfigured())
        {
            Assert.Ignore(
                "SQL Server integration tests require a MssqlAdmin connection string in appsettings.Test.json"
            );
        }

        var fixture = MssqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(
            FixtureRelativePath,
            strict: true
        );
        _databaseLease = await MssqlBackendBaselineCache.AcquireLeaseAsync(
            FixtureRelativePath,
            strict: true,
            fixture.GeneratedDdl
        );
        _database = _databaseLease.Database;

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
        // fresh per test so the row is in scope after ResetAsync() clears user tables.
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
        if (_databaseLease is not null)
        {
            await _databaseLease.DisposeAsync();
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
            UPDATE [edfi].[LocalEducationAgency]
            SET [StateEducationAgency_DocumentId] = @newSeaDocumentId,
                [StateEducationAgency_StateEducationAgencyId] = @newSeaId
            WHERE [LocalEducationAgencyId] = @leaId;
            """,
            new SqlParameter("@newSeaDocumentId", sea2DocumentId),
            new SqlParameter("@newSeaId", 200L),
            new SqlParameter("@leaId", 500L)
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
            UPDATE [edfi].[LocalEducationAgency]
            SET [StateEducationAgency_DocumentId] = @newSeaDocumentId,
                [StateEducationAgency_StateEducationAgencyId] = @newSeaId
            WHERE [LocalEducationAgencyId] = @leaId;
            """,
            new SqlParameter("@newSeaDocumentId", sea2DocumentId),
            new SqlParameter("@newSeaId", 200L),
            new SqlParameter("@leaId", 500L)
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
            UPDATE [edfi].[LocalEducationAgency]
            SET [StateEducationAgency_DocumentId] = @seaDocumentId,
                [StateEducationAgency_StateEducationAgencyId] = @seaId
            WHERE [LocalEducationAgencyId] = @leaId;
            """,
            new SqlParameter("@seaDocumentId", seaDocumentId),
            new SqlParameter("@seaId", 100L),
            new SqlParameter("@leaId", 500L)
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
            UPDATE [edfi].[LocalEducationAgency]
            SET [StateEducationAgency_DocumentId] = NULL,
                [StateEducationAgency_StateEducationAgencyId] = NULL
            WHERE [LocalEducationAgencyId] = @leaId;
            """,
            new SqlParameter("@leaId", 500L)
        );

        var tuples = await GetAuthTuplesAsync();

        tuples.Should().BeEquivalentTo(new[] { (Source: 100L, Target: 100L), (Source: 500L, Target: 500L) });
    }

    [Test]
    public async Task It_does_not_mutate_auth_tuples_when_an_LEA_parent_FK_is_updated_to_the_same_value()
    {
        // Pins the trigger's per-branch `(OLD.X IS NULL AND NEW.X IS NOT NULL) OR OLD.X <> NEW.X`
        // predicate: a no-op UPDATE (OLD = NEW, both non-null) must leave
        // auth.EducationOrganizationIdToEducationOrganizationId untouched. A predicate inversion
        // (`<>` → `=`) would mutate auth on every no-op write and skip every real re-parent.
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
            UPDATE [edfi].[LocalEducationAgency]
            SET [StateEducationAgency_DocumentId] = @seaDocumentId,
                [StateEducationAgency_StateEducationAgencyId] = @seaId
            WHERE [LocalEducationAgencyId] = @leaId;
            """,
            new SqlParameter("@seaDocumentId", seaDocumentId),
            new SqlParameter("@seaId", 100L),
            new SqlParameter("@leaId", 500L)
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
        // The MSSQL UPDATE trigger emits one structurally parallel `((OLD.X IS NULL AND NEW.X IS
        // NOT NULL) OR OLD.X <> NEW.X)` predicate per parent FK; SEA-only coverage leaves a defect
        // in the parent-LEA branch's predicate undetected.
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
            UPDATE [edfi].[LocalEducationAgency]
            SET [ParentLocalEducationAgency_DocumentId] = @parentLeaDocumentId,
                [ParentLocalEducationAgency_LocalEducationAgencyId] = @parentLeaId
            WHERE [LocalEducationAgencyId] = @leaId;
            """,
            new SqlParameter("@parentLeaDocumentId", parentLeaDocumentId),
            new SqlParameter("@parentLeaId", 400L),
            new SqlParameter("@leaId", 500L)
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
            UPDATE [edfi].[LocalEducationAgency]
            SET [ParentLocalEducationAgency_DocumentId] = NULL,
                [ParentLocalEducationAgency_LocalEducationAgencyId] = NULL
            WHERE [LocalEducationAgencyId] = @leaId;
            """,
            new SqlParameter("@leaId", 500L)
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
        // codepath — ESC has its own emitted statement-level trigger with a SEA-specific source
        // predicate and its own `inserted`/`deleted` correlation.
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
            UPDATE [edfi].[EducationServiceCenter]
            SET [StateEducationAgency_DocumentId] = @newSeaDocumentId,
                [StateEducationAgency_StateEducationAgencyId] = @newSeaId
            WHERE [EducationServiceCenterId] = @escId;
            """,
            new SqlParameter("@newSeaDocumentId", sea2DocumentId),
            new SqlParameter("@newSeaId", 200L),
            new SqlParameter("@escId", 300L)
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
            UPDATE [edfi].[School]
            SET [LocalEducationAgency_DocumentId] = @newLeaDocumentId,
                [LocalEducationAgency_LocalEducationAgencyId] = @newLeaId
            WHERE [SchoolId] = @schoolId;
            """,
            new SqlParameter("@newLeaDocumentId", lea2DocumentId),
            new SqlParameter("@newLeaId", 500L),
            new SqlParameter("@schoolId", 700L)
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
            UPDATE [edfi].[OrganizationDepartment]
            SET [ParentEducationOrganization_DocumentId] = @newParentDocumentId,
                [ParentEducationOrganization_EducationOrganizationId] = @newParentId
            WHERE [OrganizationDepartmentId] = @organizationDepartmentId;
            """,
            new SqlParameter("@newParentDocumentId", sea2DocumentId),
            new SqlParameter("@newParentId", 200L),
            new SqlParameter("@organizationDepartmentId", 800L)
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
        // unique abstract-column resolution in the MERGE half.
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
            UPDATE [edfi].[OrganizationDepartment]
            SET [ParentEducationOrganization_DocumentId] = @parentDocumentId,
                [ParentEducationOrganization_EducationOrganizationId] = @parentId
            WHERE [OrganizationDepartmentId] = @organizationDepartmentId;
            """,
            new SqlParameter("@parentDocumentId", seaDocumentId),
            new SqlParameter("@parentId", 100L),
            new SqlParameter("@organizationDepartmentId", 800L)
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
        // exercise the DELETE-half predicate `(new.X IS NULL OR old.X <> new.X)` against the
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
            UPDATE [edfi].[OrganizationDepartment]
            SET [ParentEducationOrganization_DocumentId] = NULL,
                [ParentEducationOrganization_EducationOrganizationId] = NULL
            WHERE [OrganizationDepartmentId] = @organizationDepartmentId;
            """,
            new SqlParameter("@organizationDepartmentId", 800L)
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
            DELETE FROM [edfi].[School] WHERE [SchoolId] = @schoolId;
            """,
            new SqlParameter("@schoolId", 700L)
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

    // ── MSSQL-only bulk-DML scenarios ──────────────────────────────────
    // MSSQL triggers are statement-level over the `inserted`/`deleted` pseudo-tables; multi-row
    // DML exercises a different code path from single-row DML. PG `FOR EACH ROW` triggers are
    // structurally covered by the single-row scenarios and need no parallel bulk coverage.

    [Test]
    public async Task It_creates_ancestor_tuples_for_multi_row_insert_in_one_statement()
    {
        // Heterogeneous parents per row are required to pin the trigger's row-correlation
        // predicate (`WHERE sources.[LocalEducationAgencyId] = targets.[LocalEducationAgencyId]`).
        // With identical parents, a regression that produced a Cartesian product of ancestors ×
        // inserted LEAs would emit the same final tuple set; heterogeneous parents force each LEA
        // to inherit only its own ancestor.
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
        var lea1DocumentId = await InsertDocumentAsync(
            Guid.Parse("c0000000-0000-0000-0000-000000000500"),
            _localEducationAgencyResourceKeyId
        );
        var lea2DocumentId = await InsertDocumentAsync(
            Guid.Parse("c0000000-0000-0000-0000-000000000600"),
            _localEducationAgencyResourceKeyId
        );

        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO [edfi].[LocalEducationAgency] (
                [DocumentId],
                [LocalEducationAgencyId],
                [LocalEducationAgencyCategoryDescriptor_DescriptorId],
                [NameOfInstitution],
                [StateEducationAgency_DocumentId],
                [StateEducationAgency_StateEducationAgencyId]
            )
            VALUES
                (@lea1DocumentId, 500, @categoryDocumentId, 'LEA 1', @sea1DocumentId, 100),
                (@lea2DocumentId, 600, @categoryDocumentId, 'LEA 2', @sea2DocumentId, 200);
            """,
            new SqlParameter("@lea1DocumentId", lea1DocumentId),
            new SqlParameter("@lea2DocumentId", lea2DocumentId),
            new SqlParameter("@sea1DocumentId", sea1DocumentId),
            new SqlParameter("@sea2DocumentId", sea2DocumentId),
            new SqlParameter("@categoryDocumentId", _localEducationAgencyCategoryDescriptorDocumentId)
        );

        var tuples = await GetAuthTuplesAsync();

        // LEA1 inherits ancestry from SEA1 only; LEA2 from SEA2 only. The absence of the
        // cross-pairs (100, 600) and (200, 500) is the negative-path check for the trigger's
        // row-correlation predicate.
        tuples
            .Should()
            .BeEquivalentTo(
                new[]
                {
                    (Source: 100L, Target: 100L),
                    (Source: 100L, Target: 500L),
                    (Source: 200L, Target: 200L),
                    (Source: 200L, Target: 600L),
                    (Source: 500L, Target: 500L),
                    (Source: 600L, Target: 600L),
                }
            );
    }

    [Test]
    public async Task It_rewrites_ancestor_tuples_for_multi_row_update_in_one_statement()
    {
        // Heterogeneous new parents per row are required to pin the trigger's row-correlation
        // predicate (`WHERE d1.[LocalEducationAgencyId] = d2.[LocalEducationAgencyId]`). With
        // identical new parents, a regression that cross-joined the `inserted`/`deleted` derived
        // sets would still produce the expected tuples; assigning a different new SEA per LEA in a
        // single statement forces each LEA to inherit only its own new ancestor.
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
        var sea3DocumentId = await InsertStateEducationAgencyAsync(
            Guid.Parse("c0000000-0000-0000-0000-000000000300"),
            300,
            "SEA 3"
        );
        await InsertLocalEducationAgencyAsync(
            documentUuid: Guid.Parse("c0000000-0000-0000-0000-000000000500"),
            localEducationAgencyId: 500,
            localEducationAgencyCategoryDescriptorDocumentId: _localEducationAgencyCategoryDescriptorDocumentId,
            nameOfInstitution: "LEA 1",
            parentStateEducationAgencyDocumentId: sea1DocumentId,
            parentStateEducationAgencyId: 100
        );
        await InsertLocalEducationAgencyAsync(
            documentUuid: Guid.Parse("c0000000-0000-0000-0000-000000000600"),
            localEducationAgencyId: 600,
            localEducationAgencyCategoryDescriptorDocumentId: _localEducationAgencyCategoryDescriptorDocumentId,
            nameOfInstitution: "LEA 2",
            parentStateEducationAgencyDocumentId: sea1DocumentId,
            parentStateEducationAgencyId: 100
        );

        // Single multi-row UPDATE assigns LEA 500 → SEA2 and LEA 600 → SEA3 via a VALUES-derived
        // join so per-row new parents differ.
        await _database.ExecuteNonQueryAsync(
            """
            UPDATE lea
            SET [StateEducationAgency_DocumentId] = s.NewSeaDocumentId,
                [StateEducationAgency_StateEducationAgencyId] = s.NewSeaId
            FROM [edfi].[LocalEducationAgency] AS lea
            INNER JOIN (
                VALUES
                    (@lea1Id, @sea2DocumentId, CAST(200 AS bigint)),
                    (@lea2Id, @sea3DocumentId, CAST(300 AS bigint))
            ) AS s(LeaId, NewSeaDocumentId, NewSeaId)
                ON lea.[LocalEducationAgencyId] = s.LeaId;
            """,
            new SqlParameter("@lea1Id", 500L),
            new SqlParameter("@lea2Id", 600L),
            new SqlParameter("@sea2DocumentId", sea2DocumentId),
            new SqlParameter("@sea3DocumentId", sea3DocumentId)
        );

        var tuples = await GetAuthTuplesAsync();

        // LEA1 (500) inherits ancestry from SEA2 only; LEA2 (600) from SEA3 only. The absence of
        // the cross-pairs (200, 600) and (300, 500) is the negative-path check for the trigger's
        // row-correlation predicate over the multi-row `inserted`/`deleted` pseudo-tables.
        tuples
            .Should()
            .BeEquivalentTo(
                new[]
                {
                    (Source: 100L, Target: 100L),
                    (Source: 200L, Target: 200L),
                    (Source: 200L, Target: 500L),
                    (Source: 300L, Target: 300L),
                    (Source: 300L, Target: 600L),
                    (Source: 500L, Target: 500L),
                    (Source: 600L, Target: 600L),
                }
            );
    }

    [Test]
    public async Task It_creates_per_row_ancestor_tuples_for_multi_row_insert_with_mixed_parent_presence()
    {
        // Pins the statement-level trigger's handling of NULL-parent rows interleaved with
        // parent-bearing rows. The source subqueries inner-join `inserted` to `auth.*` on the
        // parent FK column, so NULL parents are silently filtered by the join predicate. A
        // regression that flipped the INNER JOIN to a LEFT JOIN (or otherwise emitted NULL-target
        // tuples) would produce a spurious cross-pair on the orphan row and slip past the existing
        // all-parented bulk test.
        var seaDocumentId = await InsertStateEducationAgencyAsync(
            Guid.Parse("c0000000-0000-0000-0000-000000000100"),
            100,
            "Test SEA"
        );
        var lea1DocumentId = await InsertDocumentAsync(
            Guid.Parse("c0000000-0000-0000-0000-000000000500"),
            _localEducationAgencyResourceKeyId
        );
        var lea2DocumentId = await InsertDocumentAsync(
            Guid.Parse("c0000000-0000-0000-0000-000000000600"),
            _localEducationAgencyResourceKeyId
        );

        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO [edfi].[LocalEducationAgency] (
                [DocumentId],
                [LocalEducationAgencyId],
                [LocalEducationAgencyCategoryDescriptor_DescriptorId],
                [NameOfInstitution],
                [StateEducationAgency_DocumentId],
                [StateEducationAgency_StateEducationAgencyId]
            )
            VALUES
                (@lea1DocumentId, 500, @categoryDocumentId, 'Parented LEA', @seaDocumentId, 100),
                (@lea2DocumentId, 600, @categoryDocumentId, 'Orphan LEA', NULL, NULL);
            """,
            new SqlParameter("@lea1DocumentId", lea1DocumentId),
            new SqlParameter("@lea2DocumentId", lea2DocumentId),
            new SqlParameter("@seaDocumentId", seaDocumentId),
            new SqlParameter("@categoryDocumentId", _localEducationAgencyCategoryDescriptorDocumentId)
        );

        var tuples = await GetAuthTuplesAsync();

        // LEA 500 inherits ancestry from SEA 100; LEA 600 has only its self-tuple. The absence of
        // (100, 600) is the negative-path check for the trigger's join on the NULL parent column.
        tuples
            .Should()
            .BeEquivalentTo(
                new[]
                {
                    (Source: 100L, Target: 100L),
                    (Source: 100L, Target: 500L),
                    (Source: 500L, Target: 500L),
                    (Source: 600L, Target: 600L),
                }
            );
    }

    [Test]
    public async Task It_rewrites_ancestor_tuples_for_multi_row_update_with_mixed_null_and_value_transitions()
    {
        // Pins the DELETE-half row correlation when `new.X` is NULL on one row of a multi-row
        // UPDATE. The trigger's DELETE predicate `WHERE old.[X] IS NOT NULL AND (new.[X] IS NULL OR
        // old.[X] <> new.[X])` is exercised in single-row form by the value→null transition test,
        // and the multi-row correlation is exercised by the value→value bulk test; this test pins
        // the intersection (multi-row × value→null on some rows).
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
            nameOfInstitution: "LEA 1",
            parentStateEducationAgencyDocumentId: sea1DocumentId,
            parentStateEducationAgencyId: 100
        );
        await InsertLocalEducationAgencyAsync(
            documentUuid: Guid.Parse("c0000000-0000-0000-0000-000000000600"),
            localEducationAgencyId: 600,
            localEducationAgencyCategoryDescriptorDocumentId: _localEducationAgencyCategoryDescriptorDocumentId,
            nameOfInstitution: "LEA 2",
            parentStateEducationAgencyDocumentId: sea1DocumentId,
            parentStateEducationAgencyId: 100
        );

        // Single multi-row UPDATE: LEA 500 → SEA 2, LEA 600 → NULL. The VALUES-derived join feeds
        // a NULL pair to LEA 600 via CAST(NULL AS bigint) so the column types are unambiguous.
        await _database.ExecuteNonQueryAsync(
            """
            UPDATE lea
            SET [StateEducationAgency_DocumentId] = s.NewSeaDocumentId,
                [StateEducationAgency_StateEducationAgencyId] = s.NewSeaId
            FROM [edfi].[LocalEducationAgency] AS lea
            INNER JOIN (
                VALUES
                    (@lea1Id, @sea2DocumentId, CAST(200 AS bigint)),
                    (@lea2Id, CAST(NULL AS bigint), CAST(NULL AS bigint))
            ) AS s(LeaId, NewSeaDocumentId, NewSeaId)
                ON lea.[LocalEducationAgencyId] = s.LeaId;
            """,
            new SqlParameter("@lea1Id", 500L),
            new SqlParameter("@lea2Id", 600L),
            new SqlParameter("@sea2DocumentId", sea2DocumentId)
        );

        var tuples = await GetAuthTuplesAsync();

        // LEA 500 inherits ancestry from SEA 2 only; LEA 600 is orphaned. The old SEA 1 ancestry of
        // both LEAs is gone, and the absence of (200, 600) is the negative-path check for the
        // trigger's join on the NULL new parent column in the MERGE half.
        tuples
            .Should()
            .BeEquivalentTo(
                new[]
                {
                    (Source: 100L, Target: 100L),
                    (Source: 200L, Target: 200L),
                    (Source: 200L, Target: 500L),
                    (Source: 500L, Target: 500L),
                    (Source: 600L, Target: 600L),
                }
            );
    }

    // ── Seed helpers ───────────────────────────────────────────────────

    private async Task<short> GetResourceKeyIdAsync(string projectName, string resourceName)
    {
        return await _database.ExecuteScalarAsync<short>(
            """
            SELECT [ResourceKeyId]
            FROM [dms].[ResourceKey]
            WHERE [ProjectName] = @projectName
              AND [ResourceName] = @resourceName;
            """,
            new SqlParameter("@projectName", projectName),
            new SqlParameter("@resourceName", resourceName)
        );
    }

    private async Task<long> InsertDocumentAsync(Guid documentUuid, short resourceKeyId)
    {
        return await _database.ExecuteScalarAsync<long>(
            """
            DECLARE @Inserted TABLE ([DocumentId] bigint);
            INSERT INTO [dms].[Document] ([DocumentUuid], [ResourceKeyId])
            OUTPUT inserted.[DocumentId] INTO @Inserted ([DocumentId])
            VALUES (@documentUuid, @resourceKeyId);
            SELECT TOP (1) [DocumentId] FROM @Inserted;
            """,
            new SqlParameter("@documentUuid", documentUuid),
            new SqlParameter("@resourceKeyId", resourceKeyId)
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
            INSERT INTO [dms].[Descriptor] (
                [DocumentId],
                [ResourceKeyId],
                [Namespace],
                [CodeValue],
                [ShortDescription],
                [Description],
                [Discriminator],
                [Uri]
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
            new SqlParameter("@documentId", documentId),
            new SqlParameter("@resourceKeyId", resourceKeyId),
            new SqlParameter("@namespace", @namespace),
            new SqlParameter("@codeValue", codeValue),
            new SqlParameter("@shortDescription", shortDescription),
            new SqlParameter("@description", shortDescription),
            new SqlParameter("@discriminator", discriminator),
            new SqlParameter("@uri", uri)
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
            INSERT INTO [edfi].[StateEducationAgency] (
                [DocumentId],
                [StateEducationAgencyId],
                [NameOfInstitution]
            )
            VALUES (@documentId, @stateEducationAgencyId, @nameOfInstitution);
            """,
            new SqlParameter("@documentId", documentId),
            new SqlParameter("@stateEducationAgencyId", stateEducationAgencyId),
            new SqlParameter("@nameOfInstitution", nameOfInstitution)
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
            INSERT INTO [edfi].[EducationServiceCenter] (
                [DocumentId],
                [EducationServiceCenterId],
                [NameOfInstitution],
                [StateEducationAgency_DocumentId],
                [StateEducationAgency_StateEducationAgencyId]
            )
            VALUES (
                @documentId,
                @educationServiceCenterId,
                @nameOfInstitution,
                @parentSeaDocumentId,
                @parentSeaId
            );
            """,
            new SqlParameter("@documentId", documentId),
            new SqlParameter("@educationServiceCenterId", educationServiceCenterId),
            new SqlParameter("@nameOfInstitution", nameOfInstitution),
            new SqlParameter(
                "@parentSeaDocumentId",
                (object?)parentStateEducationAgencyDocumentId ?? DBNull.Value
            ),
            new SqlParameter("@parentSeaId", (object?)parentStateEducationAgencyId ?? DBNull.Value)
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
            INSERT INTO [edfi].[LocalEducationAgency] (
                [DocumentId],
                [LocalEducationAgencyId],
                [LocalEducationAgencyCategoryDescriptor_DescriptorId],
                [NameOfInstitution],
                [StateEducationAgency_DocumentId],
                [StateEducationAgency_StateEducationAgencyId],
                [EducationServiceCenter_DocumentId],
                [EducationServiceCenter_EducationServiceCenterId],
                [ParentLocalEducationAgency_DocumentId],
                [ParentLocalEducationAgency_LocalEducationAgencyId]
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
            new SqlParameter("@documentId", documentId),
            new SqlParameter("@localEducationAgencyId", localEducationAgencyId),
            new SqlParameter(
                "@categoryDescriptorDocumentId",
                localEducationAgencyCategoryDescriptorDocumentId
            ),
            new SqlParameter("@nameOfInstitution", nameOfInstitution),
            new SqlParameter(
                "@parentSeaDocumentId",
                (object?)parentStateEducationAgencyDocumentId ?? DBNull.Value
            ),
            new SqlParameter("@parentSeaId", (object?)parentStateEducationAgencyId ?? DBNull.Value),
            new SqlParameter(
                "@parentEscDocumentId",
                (object?)parentEducationServiceCenterDocumentId ?? DBNull.Value
            ),
            new SqlParameter("@parentEscId", (object?)parentEducationServiceCenterId ?? DBNull.Value),
            new SqlParameter(
                "@parentLeaDocumentId",
                (object?)parentLocalEducationAgencyDocumentId ?? DBNull.Value
            ),
            new SqlParameter("@parentLeaId", (object?)parentLocalEducationAgencyId ?? DBNull.Value)
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
            INSERT INTO [edfi].[School] (
                [DocumentId],
                [SchoolId],
                [NameOfInstitution],
                [LocalEducationAgency_DocumentId],
                [LocalEducationAgency_LocalEducationAgencyId]
            )
            VALUES (
                @documentId,
                @schoolId,
                @nameOfInstitution,
                @parentLeaDocumentId,
                @parentLeaId
            );
            """,
            new SqlParameter("@documentId", documentId),
            new SqlParameter("@schoolId", schoolId),
            new SqlParameter("@nameOfInstitution", nameOfInstitution),
            new SqlParameter("@parentLeaDocumentId", parentLocalEducationAgencyDocumentId),
            new SqlParameter("@parentLeaId", parentLocalEducationAgencyId)
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
            INSERT INTO [edfi].[OrganizationDepartment] (
                [DocumentId],
                [OrganizationDepartmentId],
                [NameOfInstitution],
                [ParentEducationOrganization_DocumentId],
                [ParentEducationOrganization_EducationOrganizationId]
            )
            VALUES (
                @documentId,
                @organizationDepartmentId,
                @nameOfInstitution,
                @parentEdOrgDocumentId,
                @parentEdOrgId
            );
            """,
            new SqlParameter("@documentId", documentId),
            new SqlParameter("@organizationDepartmentId", organizationDepartmentId),
            new SqlParameter("@nameOfInstitution", nameOfInstitution),
            new SqlParameter(
                "@parentEdOrgDocumentId",
                (object?)parentEducationOrganizationDocumentId ?? DBNull.Value
            ),
            new SqlParameter(
                "@parentEdOrgId",
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
            SELECT [SourceEducationOrganizationId], [TargetEducationOrganizationId]
            FROM [auth].[EducationOrganizationIdToEducationOrganizationId]
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
