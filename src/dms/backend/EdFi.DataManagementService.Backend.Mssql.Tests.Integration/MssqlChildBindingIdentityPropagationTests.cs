// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using EdFi.DataManagementService.Backend.Tests.Integration.Common;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Mssql.Tests.Integration;

/// <summary>
/// Runtime proof that a rename of an upstream resource's identity (<c>ClassPeriod</c>)
/// reaches stored child-collection bindings through the native <c>ON UPDATE CASCADE</c>
/// foreign keys and that the child stamp triggers preserve update tracking for the owning
/// root. The <c>BellScheduleClassPeriod</c> → <c>BellSchedule</c> scenario proves the full
/// committed outcome: the binding's own full-composite FK carries the cascade (not a retired
/// propagation trigger); the owning root advances its content stamps and root-table mirror
/// while its identity stamps stay frozen and it emits no key-change row; and the upstream
/// <c>ClassPeriod</c> advances all four stamps and emits exactly one key-change row carrying
/// the full composite identity with the three-way <c>ChangeVersion</c> linkage. The
/// <c>SectionClassPeriod</c> → <c>Section</c> scenario additionally demonstrates that the
/// cascade fan-out is general across multiple child-collection referrers of the same target,
/// not hardcoded to a single binding.
/// </summary>
[TestFixture]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
[Category(MssqlCiShards.Shard3)]
public class MssqlChildBindingIdentityPropagationTests
{
    private const string FixtureRelativePath = "src/dms/backend/Fixtures/authoritative/ds-5.2";

    private MssqlGeneratedDdlFixture _fixture = null!;
    private IMssqlGeneratedDdlBaselineLease _databaseLease = null!;
    private MssqlGeneratedDdlTestDatabase _database = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        if (!MssqlTestDatabaseHelper.IsConfigured())
        {
            Assert.Ignore(
                "SQL Server integration tests require a MssqlAdmin connection string in appsettings.Test.json"
            );
        }

        _fixture = MssqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(
            FixtureRelativePath,
            strict: true
        );
        _databaseLease = await MssqlBackendBaselineCache.AcquireLeaseAsync(
            FixtureRelativePath,
            strict: true,
            _fixture.GeneratedDdl
        );
        _database = _databaseLease.Database;
    }

    [SetUp]
    public async Task SetUp()
    {
        await _database.ResetAsync();
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_databaseLease is not null)
        {
            await _databaseLease.DisposeAsync();
            _database = null!;
        }
    }

    [Test]
    public async Task Updating_ClassPeriod_identity_propagates_to_BellScheduleClassPeriod_and_bumps_BellSchedule_ContentVersion()
    {
        // Arrange — seed the dependency chain
        //   School(id=100, doc=schoolDoc)
        //   ClassPeriod(School=100, Name="Period 1", doc=classPeriodDoc)
        //   BellSchedule(School=100, Name="BS1", doc=bellScheduleDoc)
        //   BellScheduleClassPeriod child binding referencing ClassPeriod and BellSchedule
        const int SchoolId = 100;
        const string OldClassPeriodName = "Period 1";
        const string NewClassPeriodName = "Period 1A";
        const string BellScheduleName = "BS1";

        var schoolDocumentId = await InsertSchoolDocumentAsync(SchoolId);
        var classPeriodDocumentId = await InsertClassPeriodAsync(
            schoolDocumentId,
            SchoolId,
            OldClassPeriodName
        );
        var bellScheduleDocumentId = await InsertBellScheduleAsync(
            schoolDocumentId,
            SchoolId,
            BellScheduleName
        );
        await InsertBellScheduleClassPeriodAsync(
            bellScheduleDocumentId,
            ordinal: 1,
            classPeriodDocumentId: classPeriodDocumentId,
            classPeriodName: OldClassPeriodName,
            classPeriodSchoolId: SchoolId
        );

        var classPeriodDocumentUuid = await QueryDocumentUuidAsync(classPeriodDocumentId);
        var bellScheduleDocumentUuid = await QueryDocumentUuidAsync(bellScheduleDocumentId);

        // Native FK contract — the defining requirement. Identity propagation into this
        // binding is carried by the child FK's OWN native ON UPDATE CASCADE over the full
        // composite reference key, not by any retired propagation trigger. This asserts THIS
        // binding's FK only; the global absence of a %PropagateIdentity% trigger is already
        // proven by
        // MssqlGeneratedDdlAuthoritativeSmokeTests.It_should_not_install_any_retired_identity_propagation_trigger.
        var bindingForeignKeys = await _database.GetForeignKeyMetadataAsync(
            "edfi",
            "BellScheduleClassPeriod"
        );
        var classPeriodReferenceKey = bindingForeignKeys.Single(foreignKey =>
            foreignKey.ConstraintName == "FK_BellScheduleClassPeriod_ClassPeriod_RefKey"
        );
        classPeriodReferenceKey
            .Columns.Should()
            .Equal("ClassPeriod_ClassPeriodName", "ClassPeriod_SchoolId", "ClassPeriod_DocumentId");
        classPeriodReferenceKey.ReferencedSchema.Should().Be("edfi");
        classPeriodReferenceKey.ReferencedTable.Should().Be("ClassPeriod");
        classPeriodReferenceKey
            .ReferencedColumns.Should()
            .Equal("ClassPeriodName", "School_SchoolId", "DocumentId");
        classPeriodReferenceKey.DeleteAction.Should().Be("NO ACTION");
        classPeriodReferenceKey
            .UpdateAction.Should()
            .Be("CASCADE", "identity propagation is carried by the native FK cascade, not a trigger");

        var childRowsBefore = await QueryBellScheduleClassPeriodRowCountAsync(bellScheduleDocumentId);
        var childAnchorBefore = await QueryBellScheduleClassPeriodAnchorAsync(bellScheduleDocumentId);
        childRowsBefore.Should().Be(1);

        var beforeBellSchedule = await GetDocumentStampStateAsync(bellScheduleDocumentId);
        var beforeClassPeriod = await GetDocumentStampStateAsync(classPeriodDocumentId);
        var bellScheduleKeyChangesBefore = await CountTrackedChangeRowsAsync(
            "tracked_changes_edfi",
            "BellSchedule",
            bellScheduleDocumentUuid
        );
        var classPeriodKeyChangesBefore = await CountTrackedChangeRowsAsync(
            "tracked_changes_edfi",
            "ClassPeriod",
            classPeriodDocumentUuid
        );

        // A small delay so ContentLastModifiedAt (sysutcdatetime) advances to a distinct value.
        await _database.ExecuteNonQueryAsync("WAITFOR DELAY '00:00:00.050';");

        // Act — update the upstream ClassPeriod identity column.
        await _database.ExecuteNonQueryAsync(
            """
            UPDATE [edfi].[ClassPeriod]
            SET [ClassPeriodName] = @newName
            WHERE [DocumentId] = @classPeriodDocumentId;
            """,
            new SqlParameter("@newName", NewClassPeriodName),
            new SqlParameter("@classPeriodDocumentId", classPeriodDocumentId)
        );

        // Assert — the cascade updated the projected identity column on the child row.
        var childRow = await QuerySingleBellScheduleClassPeriodAsync(bellScheduleDocumentId);
        Convert
            .ToString(childRow["ClassPeriod_ClassPeriodName"], CultureInfo.InvariantCulture)
            .Should()
            .Be(NewClassPeriodName);

        // Row count must be unchanged — the cascade is an UPDATE, not an INSERT/DELETE.
        var childRowsAfter = await QueryBellScheduleClassPeriodRowCountAsync(bellScheduleDocumentId);
        childRowsAfter
            .Should()
            .Be(
                childRowsBefore,
                "the cascade must update projected identity columns, not insert/delete rows"
            );

        // FK anchor (ClassPeriod_DocumentId) must NOT change — propagation should only
        // touch the projected non-key identity columns, never the reference link itself.
        var childAnchorAfter = await QueryBellScheduleClassPeriodAnchorAsync(bellScheduleDocumentId);
        childAnchorAfter
            .Should()
            .Be(
                childAnchorBefore,
                "ClassPeriod_DocumentId is the reference anchor and must not change during an identity cascade"
            );

        var afterBellSchedule = await GetDocumentStampStateAsync(bellScheduleDocumentId);
        var afterClassPeriod = await GetDocumentStampStateAsync(classPeriodDocumentId);
        var afterBellScheduleMirror = await GetRootMirrorStampStateAsync(
            "edfi",
            "BellSchedule",
            bellScheduleDocumentId
        );
        var afterClassPeriodMirror = await GetRootMirrorStampStateAsync(
            "edfi",
            "ClassPeriod",
            classPeriodDocumentId
        );

        // Owning root BellSchedule: its representation changed (a child binding's projected
        // identity was cascade-updated) but its OWN identity did not. Its content stamps advance
        // and its root-table mirror tracks the document row, while its identity stamps stay frozen
        // (a paired contract) and it emits no key-change row because its identity is unchanged.
        afterBellSchedule
            .ContentVersion.Should()
            .BeGreaterThan(
                beforeBellSchedule.ContentVersion,
                "child stamp trigger must fire from the cascade UPDATE and bump the owning root ContentVersion"
            );
        afterBellSchedule.ContentLastModifiedAt.Should().BeAfter(beforeBellSchedule.ContentLastModifiedAt);
        afterBellSchedule
            .IdentityVersion.Should()
            .Be(
                beforeBellSchedule.IdentityVersion,
                "BellSchedule's own identity did not change, so IdentityVersion must be frozen"
            );
        afterBellSchedule
            .IdentityLastModifiedAt.Should()
            .Be(
                beforeBellSchedule.IdentityLastModifiedAt,
                "identity stamps are a paired contract and must both be frozen"
            );
        AssertMirrorContentMatchesDocument(afterBellScheduleMirror, afterBellSchedule);
        (await CountTrackedChangeRowsAsync("tracked_changes_edfi", "BellSchedule", bellScheduleDocumentUuid))
            .Should()
            .Be(
                bellScheduleKeyChangesBefore,
                "the referrer's identity did not change, so it must emit no key-change row"
            );

        // Upstream ClassPeriod: its own identity changed. All four stamps advance, and it emits
        // exactly one key-change row.
        afterClassPeriod.ContentVersion.Should().BeGreaterThan(beforeClassPeriod.ContentVersion);
        afterClassPeriod.ContentLastModifiedAt.Should().BeAfter(beforeClassPeriod.ContentLastModifiedAt);
        afterClassPeriod.IdentityVersion.Should().BeGreaterThan(beforeClassPeriod.IdentityVersion);
        afterClassPeriod.IdentityLastModifiedAt.Should().BeAfter(beforeClassPeriod.IdentityLastModifiedAt);
        AssertMirrorContentMatchesDocument(afterClassPeriodMirror, afterClassPeriod);
        (await CountTrackedChangeRowsAsync("tracked_changes_edfi", "ClassPeriod", classPeriodDocumentUuid))
            .Should()
            .Be(
                classPeriodKeyChangesBefore + 1,
                "the resource whose identity changed emits exactly one key-change row"
            );

        var classPeriodKeyChange = await GetLatestTrackedChangeRowAsync(
            "tracked_changes_edfi",
            "ClassPeriod",
            classPeriodDocumentUuid
        );
        Convert
            .ToString(classPeriodKeyChange["OldClassPeriodName"], CultureInfo.InvariantCulture)
            .Should()
            .Be(OldClassPeriodName);
        Convert
            .ToString(classPeriodKeyChange["NewClassPeriodName"], CultureInfo.InvariantCulture)
            .Should()
            .Be(NewClassPeriodName);
        // ClassPeriod identity is School + ClassPeriodName. The School half did not change, so
        // both the old and new sides must carry SchoolId 100 — a row that mangled the unchanged
        // component would be a malformed key-change and must fail here.
        Convert
            .ToInt64(classPeriodKeyChange["OldSchool_SchoolId"], CultureInfo.InvariantCulture)
            .Should()
            .Be(SchoolId);
        Convert
            .ToInt64(classPeriodKeyChange["NewSchool_SchoolId"], CultureInfo.InvariantCulture)
            .Should()
            .Be(SchoolId);
        // Three-way linkage: the key-change ChangeVersion equals dms.Document.ContentVersion
        // equals the edfi.ClassPeriod root-table mirror ContentVersion
        // (16-tracked-change-trigger-rendering §29/§33).
        Convert
            .ToInt64(classPeriodKeyChange["ChangeVersion"], CultureInfo.InvariantCulture)
            .Should()
            .Be(afterClassPeriod.ContentVersion);
        afterClassPeriodMirror.ContentVersion.Should().Be(afterClassPeriod.ContentVersion);
    }

    [Test]
    public async Task Updating_ClassPeriod_identity_propagates_to_SectionClassPeriod_and_bumps_Section_ContentVersion()
    {
        // Arrange — seed a School document, a ClassPeriod referencing that School, a
        // synthetic Section root, and a SectionClassPeriod child binding referencing both
        // Section and ClassPeriod. The Section row is seeded with
        // FK_Section_CourseOffering_RefKey temporarily disabled and Section's own triggers
        // disabled, which bypasses the deep upstream chain rooted at CourseOffering. None of
        // that chain is needed to exercise the ClassPeriod cascade fan-out into
        // SectionClassPeriod, which is the behaviour under test. This exercises a different
        // child-collection referrer cascading from ClassPeriod than the
        // BellScheduleClassPeriod case — proving the cascade fan-out works for any child
        // binding to ClassPeriod.
        const int SchoolId = 100;
        const string OldClassPeriodName = "Period 1";
        const string NewClassPeriodName = "Period 1A";

        var schoolDocumentId = await InsertSchoolDocumentAsync(SchoolId);
        var classPeriodDocumentId = await InsertClassPeriodAsync(
            schoolDocumentId,
            SchoolId,
            OldClassPeriodName
        );
        var sectionDocumentId = await InsertSectionAsync(SchoolId);
        await InsertSectionClassPeriodAsync(
            sectionDocumentId,
            ordinal: 1,
            classPeriodDocumentId: classPeriodDocumentId,
            classPeriodName: OldClassPeriodName,
            classPeriodSchoolId: SchoolId
        );

        var initialContentVersion = await QueryDocumentContentVersionAsync(sectionDocumentId);
        var childRowsBefore = await QuerySectionClassPeriodRowCountAsync(sectionDocumentId);
        var childAnchorBefore = await QuerySectionClassPeriodAnchorAsync(sectionDocumentId);
        childRowsBefore.Should().Be(1);

        // Small delay so any stamp comparison that checks ContentLastModifiedAt
        // would see a distinct timestamp too.
        await _database.ExecuteNonQueryAsync("WAITFOR DELAY '00:00:00.050';");

        // Act — update the upstream ClassPeriod identity column.
        await _database.ExecuteNonQueryAsync(
            """
            UPDATE [edfi].[ClassPeriod]
            SET [ClassPeriodName] = @newName
            WHERE [DocumentId] = @classPeriodDocumentId;
            """,
            new SqlParameter("@newName", NewClassPeriodName),
            new SqlParameter("@classPeriodDocumentId", classPeriodDocumentId)
        );

        // Assert — the cascade updated the projected identity column on the child row
        var childRow = await QuerySingleSectionClassPeriodAsync(sectionDocumentId);
        Convert
            .ToString(childRow["ClassPeriod_ClassPeriodName"], CultureInfo.InvariantCulture)
            .Should()
            .Be(NewClassPeriodName);

        // Row count must be unchanged — the cascade is an UPDATE, not an INSERT/DELETE.
        var childRowsAfter = await QuerySectionClassPeriodRowCountAsync(sectionDocumentId);
        childRowsAfter
            .Should()
            .Be(
                childRowsBefore,
                "the cascade must update projected identity columns, not insert/delete rows"
            );

        // FK anchor (ClassPeriod_DocumentId) must NOT change — propagation should only
        // touch the projected non-key identity columns, never the reference link itself.
        var childAnchorAfter = await QuerySectionClassPeriodAnchorAsync(sectionDocumentId);
        childAnchorAfter
            .Should()
            .Be(
                childAnchorBefore,
                "ClassPeriod_DocumentId is the reference anchor and must not change during an identity cascade"
            );

        // The child stamp trigger (TR_SectionClassPeriod_Stamp) must fire from the
        // cascade UPDATE and bump the owning Section root's ContentVersion via
        // the Section_DocumentId locator.
        var finalContentVersion = await QueryDocumentContentVersionAsync(sectionDocumentId);
        finalContentVersion
            .Should()
            .BeGreaterThan(
                initialContentVersion,
                "child stamp trigger must fire from the cascade UPDATE and bump the owning root ContentVersion"
            );
    }

    [Test]
    public async Task Updating_ClassPeriod_identity_within_a_held_transaction_stamps_atomically_before_commit_and_rolls_back_cleanly()
    {
        // Same-transaction atomicity (genuine pre-commit observation, no rollback-only fallback): the
        // cascade, the owning-root/upstream stamps, both root-table mirrors, and the ClassPeriod
        // key-change row must all be observable on the same connection BEFORE commit, and a ROLLBACK
        // must restore every baseline (sequence values excepted — allocation is non-transactional).
        const int SchoolId = 100;
        const string OldClassPeriodName = "Period 1";
        const string NewClassPeriodName = "Period 1A";
        const string BellScheduleName = "BS1";

        var schoolDocumentId = await InsertSchoolDocumentAsync(SchoolId);
        var classPeriodDocumentId = await InsertClassPeriodAsync(
            schoolDocumentId,
            SchoolId,
            OldClassPeriodName
        );
        var bellScheduleDocumentId = await InsertBellScheduleAsync(
            schoolDocumentId,
            SchoolId,
            BellScheduleName
        );
        await InsertBellScheduleClassPeriodAsync(
            bellScheduleDocumentId,
            ordinal: 1,
            classPeriodDocumentId: classPeriodDocumentId,
            classPeriodName: OldClassPeriodName,
            classPeriodSchoolId: SchoolId
        );

        var classPeriodDocumentUuid = await QueryDocumentUuidAsync(classPeriodDocumentId);
        var bellScheduleDocumentUuid = await QueryDocumentUuidAsync(bellScheduleDocumentId);

        // Committed baselines (each read opens a fresh connection).
        var baselineClassPeriod = await GetDocumentStampStateAsync(classPeriodDocumentId);
        var baselineBellSchedule = await GetDocumentStampStateAsync(bellScheduleDocumentId);
        var baselineClassPeriodMirror = await GetRootMirrorStampStateAsync(
            "edfi",
            "ClassPeriod",
            classPeriodDocumentId
        );
        var baselineBellScheduleMirror = await GetRootMirrorStampStateAsync(
            "edfi",
            "BellSchedule",
            bellScheduleDocumentId
        );
        var baselineClassPeriodKeyChanges = await CountTrackedChangeRowsAsync(
            "tracked_changes_edfi",
            "ClassPeriod",
            classPeriodDocumentUuid
        );
        var baselineBellScheduleKeyChanges = await CountTrackedChangeRowsAsync(
            "tracked_changes_edfi",
            "BellSchedule",
            bellScheduleDocumentUuid
        );

        // Distinct-timestamp gap so the in-transaction sysutcdatetime() stamps are strictly later.
        await _database.ExecuteNonQueryAsync("WAITFOR DELAY '00:00:00.050';");

        await using var connection = new SqlConnection(_database.ConnectionString);
        await connection.OpenAsync();
        // await using guarantees ROLLBACK + disposal even if an assertion throws (an uncommitted
        // SqlTransaction rolls back on dispose), so an assertion failure cannot leave an open transaction.
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();

        // Act — rename the upstream ClassPeriod identity inside the held transaction.
        await ExecuteNonQueryInTransactionAsync(
            connection,
            transaction,
            """
            UPDATE [edfi].[ClassPeriod]
            SET [ClassPeriodName] = @newName
            WHERE [DocumentId] = @classPeriodDocumentId;
            """,
            new SqlParameter("@newName", NewClassPeriodName),
            new SqlParameter("@classPeriodDocumentId", classPeriodDocumentId)
        );

        // Pre-commit, on the held connection: the source identity changed and the child binding cascaded.
        (
            await ExecuteScalarStringInTransactionAsync(
                connection,
                transaction,
                "SELECT [ClassPeriodName] FROM [edfi].[ClassPeriod] WHERE [DocumentId] = @documentId;",
                new SqlParameter("@documentId", classPeriodDocumentId)
            )
        )
            .Should()
            .Be(NewClassPeriodName);
        (
            await ExecuteScalarStringInTransactionAsync(
                connection,
                transaction,
                "SELECT [ClassPeriod_ClassPeriodName] FROM [edfi].[BellScheduleClassPeriod] WHERE [BellSchedule_DocumentId] = @documentId;",
                new SqlParameter("@documentId", bellScheduleDocumentId)
            )
        )
            .Should()
            .Be(NewClassPeriodName);

        var pendingClassPeriod = await GetDocumentStampStateInTransactionAsync(
            connection,
            transaction,
            classPeriodDocumentId
        );
        var pendingBellSchedule = await GetDocumentStampStateInTransactionAsync(
            connection,
            transaction,
            bellScheduleDocumentId
        );
        var pendingClassPeriodMirror = await GetRootMirrorStampStateInTransactionAsync(
            connection,
            transaction,
            "edfi",
            "ClassPeriod",
            classPeriodDocumentId
        );
        var pendingBellScheduleMirror = await GetRootMirrorStampStateInTransactionAsync(
            connection,
            transaction,
            "edfi",
            "BellSchedule",
            bellScheduleDocumentId
        );

        // Upstream ClassPeriod: all four stamps advance; mirror tracks the document.
        pendingClassPeriod.ContentVersion.Should().BeGreaterThan(baselineClassPeriod.ContentVersion);
        pendingClassPeriod.ContentLastModifiedAt.Should().BeAfter(baselineClassPeriod.ContentLastModifiedAt);
        pendingClassPeriod.IdentityVersion.Should().BeGreaterThan(baselineClassPeriod.IdentityVersion);
        pendingClassPeriod
            .IdentityLastModifiedAt.Should()
            .BeAfter(baselineClassPeriod.IdentityLastModifiedAt);
        AssertMirrorContentMatchesDocument(pendingClassPeriodMirror, pendingClassPeriod);

        // Owning root BellSchedule: content stamps advance, identity stamps frozen, mirror tracks.
        pendingBellSchedule.ContentVersion.Should().BeGreaterThan(baselineBellSchedule.ContentVersion);
        pendingBellSchedule
            .ContentLastModifiedAt.Should()
            .BeAfter(baselineBellSchedule.ContentLastModifiedAt);
        pendingBellSchedule.IdentityVersion.Should().Be(baselineBellSchedule.IdentityVersion);
        pendingBellSchedule.IdentityLastModifiedAt.Should().Be(baselineBellSchedule.IdentityLastModifiedAt);
        AssertMirrorContentMatchesDocument(pendingBellScheduleMirror, pendingBellSchedule);

        // ClassPeriod key-change delta is exactly one, with the full composite identity and three-way linkage.
        (
            await CountTrackedChangeRowsInTransactionAsync(
                connection,
                transaction,
                "tracked_changes_edfi",
                "ClassPeriod",
                classPeriodDocumentUuid
            )
        )
            .Should()
            .Be(baselineClassPeriodKeyChanges + 1);
        var pendingKeyChange = await GetLatestTrackedChangeRowInTransactionAsync(
            connection,
            transaction,
            "tracked_changes_edfi",
            "ClassPeriod",
            classPeriodDocumentUuid
        );
        Convert
            .ToString(pendingKeyChange["OldClassPeriodName"], CultureInfo.InvariantCulture)
            .Should()
            .Be(OldClassPeriodName);
        Convert
            .ToString(pendingKeyChange["NewClassPeriodName"], CultureInfo.InvariantCulture)
            .Should()
            .Be(NewClassPeriodName);
        Convert
            .ToInt64(pendingKeyChange["OldSchool_SchoolId"], CultureInfo.InvariantCulture)
            .Should()
            .Be(SchoolId);
        Convert
            .ToInt64(pendingKeyChange["NewSchool_SchoolId"], CultureInfo.InvariantCulture)
            .Should()
            .Be(SchoolId);
        Convert
            .ToInt64(pendingKeyChange["ChangeVersion"], CultureInfo.InvariantCulture)
            .Should()
            .Be(pendingClassPeriod.ContentVersion);
        pendingClassPeriodMirror.ContentVersion.Should().Be(pendingClassPeriod.ContentVersion);

        // Owning root BellSchedule emits no key-change row (its identity did not change).
        (
            await CountTrackedChangeRowsInTransactionAsync(
                connection,
                transaction,
                "tracked_changes_edfi",
                "BellSchedule",
                bellScheduleDocumentUuid
            )
        )
            .Should()
            .Be(baselineBellScheduleKeyChanges);

        await transaction.RollbackAsync();

        // After ROLLBACK, every source/binding/document/mirror/tracked-change baseline is restored
        // (verified through fresh connections). Sequence values are intentionally NOT asserted to roll back.
        (await QueryClassPeriodNameAsync(classPeriodDocumentId))
            .Should()
            .Be(OldClassPeriodName);
        var restoredChildRow = await QuerySingleBellScheduleClassPeriodAsync(bellScheduleDocumentId);
        Convert
            .ToString(restoredChildRow["ClassPeriod_ClassPeriodName"], CultureInfo.InvariantCulture)
            .Should()
            .Be(OldClassPeriodName);
        (await GetDocumentStampStateAsync(classPeriodDocumentId)).Should().Be(baselineClassPeriod);
        (await GetDocumentStampStateAsync(bellScheduleDocumentId)).Should().Be(baselineBellSchedule);
        (await GetRootMirrorStampStateAsync("edfi", "ClassPeriod", classPeriodDocumentId))
            .Should()
            .Be(baselineClassPeriodMirror);
        (await GetRootMirrorStampStateAsync("edfi", "BellSchedule", bellScheduleDocumentId))
            .Should()
            .Be(baselineBellScheduleMirror);
        (await CountTrackedChangeRowsAsync("tracked_changes_edfi", "ClassPeriod", classPeriodDocumentUuid))
            .Should()
            .Be(baselineClassPeriodKeyChanges);
        (await CountTrackedChangeRowsAsync("tracked_changes_edfi", "BellSchedule", bellScheduleDocumentUuid))
            .Should()
            .Be(baselineBellScheduleKeyChanges);
    }

    private async Task<long> InsertSchoolDocumentAsync(int schoolId)
    {
        var schoolResourceKeyId = await GetResourceKeyIdAsync("Ed-Fi", "School");
        var schoolDocumentId = await InsertDocumentAsync(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            schoolResourceKeyId
        );

        // The DS 5.2 School insert triggers attempt to maintain the
        // EducationOrganizationIdentity alias row. The BellSchedule/ClassPeriod
        // FKs only need [edfi].[School] populated with matching ([DocumentId], [SchoolId]),
        // so we disable triggers temporarily to bypass the alias side-effects.
        await _database.ExecuteNonQueryAsync("DISABLE TRIGGER ALL ON [edfi].[School];");

        try
        {
            await _database.ExecuteNonQueryAsync(
                """
                INSERT INTO [edfi].[School] ([DocumentId], [NameOfInstitution], [SchoolId])
                VALUES (@documentId, @nameOfInstitution, @schoolId);
                """,
                new SqlParameter("@documentId", schoolDocumentId),
                new SqlParameter("@nameOfInstitution", "Test School"),
                new SqlParameter("@schoolId", schoolId)
            );
        }
        finally
        {
            await _database.ExecuteNonQueryAsync("ENABLE TRIGGER ALL ON [edfi].[School];");
        }

        return schoolDocumentId;
    }

    private async Task<long> InsertClassPeriodAsync(
        long schoolDocumentId,
        int schoolId,
        string classPeriodName
    )
    {
        var classPeriodResourceKeyId = await GetResourceKeyIdAsync("Ed-Fi", "ClassPeriod");
        var classPeriodDocumentId = await InsertDocumentAsync(
            Guid.Parse("22222222-2222-2222-2222-222222222222"),
            classPeriodResourceKeyId
        );

        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO [edfi].[ClassPeriod] (
                [DocumentId],
                [School_DocumentId],
                [School_SchoolId],
                [ClassPeriodName]
            )
            VALUES (
                @documentId,
                @schoolDocumentId,
                @schoolId,
                @classPeriodName
            );
            """,
            new SqlParameter("@documentId", classPeriodDocumentId),
            new SqlParameter("@schoolDocumentId", schoolDocumentId),
            new SqlParameter("@schoolId", schoolId),
            new SqlParameter("@classPeriodName", classPeriodName)
        );

        return classPeriodDocumentId;
    }

    private async Task<long> InsertBellScheduleAsync(
        long schoolDocumentId,
        int schoolId,
        string bellScheduleName
    )
    {
        var bellScheduleResourceKeyId = await GetResourceKeyIdAsync("Ed-Fi", "BellSchedule");
        var bellScheduleDocumentId = await InsertDocumentAsync(
            Guid.Parse("33333333-3333-3333-3333-333333333333"),
            bellScheduleResourceKeyId
        );

        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO [edfi].[BellSchedule] (
                [DocumentId],
                [School_DocumentId],
                [School_SchoolId],
                [BellScheduleName]
            )
            VALUES (
                @documentId,
                @schoolDocumentId,
                @schoolId,
                @bellScheduleName
            );
            """,
            new SqlParameter("@documentId", bellScheduleDocumentId),
            new SqlParameter("@schoolDocumentId", schoolDocumentId),
            new SqlParameter("@schoolId", schoolId),
            new SqlParameter("@bellScheduleName", bellScheduleName)
        );

        return bellScheduleDocumentId;
    }

    private async Task InsertBellScheduleClassPeriodAsync(
        long bellScheduleDocumentId,
        int ordinal,
        long classPeriodDocumentId,
        string classPeriodName,
        int classPeriodSchoolId
    )
    {
        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO [edfi].[BellScheduleClassPeriod] (
                [BellSchedule_DocumentId],
                [Ordinal],
                [ClassPeriod_DocumentId],
                [ClassPeriod_ClassPeriodName],
                [ClassPeriod_SchoolId]
            )
            VALUES (
                @bellScheduleDocumentId,
                @ordinal,
                @classPeriodDocumentId,
                @classPeriodName,
                @classPeriodSchoolId
            );
            """,
            new SqlParameter("@bellScheduleDocumentId", bellScheduleDocumentId),
            new SqlParameter("@ordinal", ordinal),
            new SqlParameter("@classPeriodDocumentId", classPeriodDocumentId),
            new SqlParameter("@classPeriodName", classPeriodName),
            new SqlParameter("@classPeriodSchoolId", classPeriodSchoolId)
        );
    }

    private async Task<long> QueryDocumentContentVersionAsync(long documentId)
    {
        return await _database.ExecuteScalarAsync<long>(
            """
            SELECT [ContentVersion]
            FROM [dms].[Document]
            WHERE [DocumentId] = @documentId;
            """,
            new SqlParameter("@documentId", documentId)
        );
    }

    private async Task<int> QueryBellScheduleClassPeriodRowCountAsync(long bellScheduleDocumentId)
    {
        return await _database.ExecuteScalarAsync<int>(
            """
            SELECT COUNT(*)
            FROM [edfi].[BellScheduleClassPeriod]
            WHERE [BellSchedule_DocumentId] = @documentId;
            """,
            new SqlParameter("@documentId", bellScheduleDocumentId)
        );
    }

    private async Task<long> QueryBellScheduleClassPeriodAnchorAsync(long bellScheduleDocumentId)
    {
        return await _database.ExecuteScalarAsync<long>(
            """
            SELECT [ClassPeriod_DocumentId]
            FROM [edfi].[BellScheduleClassPeriod]
            WHERE [BellSchedule_DocumentId] = @documentId;
            """,
            new SqlParameter("@documentId", bellScheduleDocumentId)
        );
    }

    private async Task<IReadOnlyDictionary<string, object?>> QuerySingleBellScheduleClassPeriodAsync(
        long bellScheduleDocumentId
    )
    {
        var rows = await _database.QueryRowsAsync(
            """
            SELECT
                [CollectionItemId],
                [BellSchedule_DocumentId],
                [Ordinal],
                [ClassPeriod_DocumentId],
                [ClassPeriod_ClassPeriodName],
                [ClassPeriod_SchoolId]
            FROM [edfi].[BellScheduleClassPeriod]
            WHERE [BellSchedule_DocumentId] = @documentId;
            """,
            new SqlParameter("@documentId", bellScheduleDocumentId)
        );

        return rows.Single();
    }

    private async Task<long> InsertSectionAsync(int schoolId)
    {
        var sectionResourceKeyId = await GetResourceKeyIdAsync("Ed-Fi", "Section");
        var sectionDocumentId = await InsertDocumentAsync(
            Guid.Parse("44444444-4444-4444-4444-444444444444"),
            sectionResourceKeyId
        );

        // Section requires a non-null CourseOffering FK and a non-null SchoolId_Unified
        // plus the all-or-none CourseOffering CHECK columns. Seeding the full upstream
        // CourseOffering → Course + Session + School(again) chain just to exercise the
        // ClassPeriod → SectionClassPeriod cascade is out of scope for this smoke, so we
        // temporarily disable FK_Section_CourseOffering_RefKey and Section's own triggers
        // to insert a synthetic Section. The behaviour under test cascades from
        // ClassPeriod's FK and stamps Section via SectionClassPeriod — none of that
        // depends on real CourseOffering rows being present.
        await _database.ExecuteNonQueryAsync(
            "ALTER TABLE [edfi].[Section] NOCHECK CONSTRAINT [FK_Section_CourseOffering_RefKey];"
        );
        await _database.ExecuteNonQueryAsync("DISABLE TRIGGER ALL ON [edfi].[Section];");

        try
        {
            await _database.ExecuteNonQueryAsync(
                """
                INSERT INTO [edfi].[Section] (
                    [DocumentId],
                    [SchoolId_Unified],
                    [CourseOffering_DocumentId],
                    [CourseOffering_LocalCourseCode],
                    [CourseOffering_SchoolYear],
                    [CourseOffering_SessionName],
                    [SectionIdentifier]
                )
                VALUES (
                    @documentId,
                    @schoolId,
                    @courseOfferingDocumentId,
                    @localCourseCode,
                    @schoolYear,
                    @sessionName,
                    @sectionIdentifier
                );
                """,
                new SqlParameter("@documentId", sectionDocumentId),
                new SqlParameter("@schoolId", (long)schoolId),
                // Synthetic CourseOffering anchor — FK check is disabled for this insert.
                new SqlParameter("@courseOfferingDocumentId", -1L),
                new SqlParameter("@localCourseCode", "CRS-1"),
                new SqlParameter("@schoolYear", 2025),
                new SqlParameter("@sessionName", "Fall"),
                new SqlParameter("@sectionIdentifier", "Section-1")
            );
        }
        finally
        {
            await _database.ExecuteNonQueryAsync("ENABLE TRIGGER ALL ON [edfi].[Section];");
            await _database.ExecuteNonQueryAsync(
                "ALTER TABLE [edfi].[Section] CHECK CONSTRAINT [FK_Section_CourseOffering_RefKey];"
            );
        }

        return sectionDocumentId;
    }

    private async Task InsertSectionClassPeriodAsync(
        long sectionDocumentId,
        int ordinal,
        long classPeriodDocumentId,
        string classPeriodName,
        int classPeriodSchoolId
    )
    {
        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO [edfi].[SectionClassPeriod] (
                [Section_DocumentId],
                [Ordinal],
                [ClassPeriod_DocumentId],
                [ClassPeriod_ClassPeriodName],
                [ClassPeriod_SchoolId]
            )
            VALUES (
                @sectionDocumentId,
                @ordinal,
                @classPeriodDocumentId,
                @classPeriodName,
                @classPeriodSchoolId
            );
            """,
            new SqlParameter("@sectionDocumentId", sectionDocumentId),
            new SqlParameter("@ordinal", ordinal),
            new SqlParameter("@classPeriodDocumentId", classPeriodDocumentId),
            new SqlParameter("@classPeriodName", classPeriodName),
            new SqlParameter("@classPeriodSchoolId", classPeriodSchoolId)
        );
    }

    private async Task<int> QuerySectionClassPeriodRowCountAsync(long sectionDocumentId)
    {
        return await _database.ExecuteScalarAsync<int>(
            """
            SELECT COUNT(*)
            FROM [edfi].[SectionClassPeriod]
            WHERE [Section_DocumentId] = @documentId;
            """,
            new SqlParameter("@documentId", sectionDocumentId)
        );
    }

    private async Task<long> QuerySectionClassPeriodAnchorAsync(long sectionDocumentId)
    {
        return await _database.ExecuteScalarAsync<long>(
            """
            SELECT [ClassPeriod_DocumentId]
            FROM [edfi].[SectionClassPeriod]
            WHERE [Section_DocumentId] = @documentId;
            """,
            new SqlParameter("@documentId", sectionDocumentId)
        );
    }

    private async Task<IReadOnlyDictionary<string, object?>> QuerySingleSectionClassPeriodAsync(
        long sectionDocumentId
    )
    {
        var rows = await _database.QueryRowsAsync(
            """
            SELECT
                [CollectionItemId],
                [Section_DocumentId],
                [Ordinal],
                [ClassPeriod_DocumentId],
                [ClassPeriod_ClassPeriodName],
                [ClassPeriod_SchoolId]
            FROM [edfi].[SectionClassPeriod]
            WHERE [Section_DocumentId] = @documentId;
            """,
            new SqlParameter("@documentId", sectionDocumentId)
        );

        return rows.Single();
    }

    private async Task<long> InsertDocumentAsync(Guid documentUuid, short resourceKeyId)
    {
        return await _database.ExecuteScalarAsync<long>(
            """
            DECLARE @Inserted TABLE ([DocumentId] bigint);
            INSERT INTO [dms].[Document] ([DocumentUuid], [ResourceKeyId])
            OUTPUT INSERTED.[DocumentId] INTO @Inserted ([DocumentId])
            VALUES (@documentUuid, @resourceKeyId);
            SELECT TOP (1) [DocumentId] FROM @Inserted;
            """,
            new SqlParameter("@documentUuid", documentUuid),
            new SqlParameter("@resourceKeyId", resourceKeyId)
        );
    }

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

    private async Task<Guid> QueryDocumentUuidAsync(long documentId)
    {
        return await _database.ExecuteScalarAsync<Guid>(
            """
            SELECT [DocumentUuid]
            FROM [dms].[Document]
            WHERE [DocumentId] = @documentId;
            """,
            new SqlParameter("@documentId", documentId)
        );
    }

    private async Task<DocumentStampState> GetDocumentStampStateAsync(long documentId)
    {
        var row = (
            await _database.QueryRowsAsync(
                """
                SELECT
                    [ContentVersion],
                    [IdentityVersion],
                    [ContentLastModifiedAt],
                    [IdentityLastModifiedAt]
                FROM [dms].[Document]
                WHERE [DocumentId] = @documentId;
                """,
                new SqlParameter("@documentId", documentId)
            )
        ).Single();

        return new(
            Convert.ToInt64(row["ContentVersion"], CultureInfo.InvariantCulture),
            Convert.ToInt64(row["IdentityVersion"], CultureInfo.InvariantCulture),
            ReadDateTimeOffset(row["ContentLastModifiedAt"]),
            ReadDateTimeOffset(row["IdentityLastModifiedAt"])
        );
    }

    // Root tables carry ContentVersion/ContentLastModifiedAt mirror columns that resource
    // change-version queries filter on; this reads the mirror pair for the owning root row.
    private async Task<DocumentStampState> GetRootMirrorStampStateAsync(
        string schemaName,
        string tableName,
        long documentId
    )
    {
        var row = (
            await _database.QueryRowsAsync(
                $"""
                SELECT
                    [ContentVersion],
                    [ContentLastModifiedAt]
                FROM [{schemaName}].[{tableName}]
                WHERE [DocumentId] = @documentId;
                """,
                new SqlParameter("@documentId", documentId)
            )
        ).Single();

        return new(
            Convert.ToInt64(row["ContentVersion"], CultureInfo.InvariantCulture),
            IdentityVersion: 0,
            ReadDateTimeOffset(row["ContentLastModifiedAt"]),
            IdentityLastModifiedAt: DateTimeOffset.UnixEpoch
        );
    }

    private static void AssertMirrorContentMatchesDocument(
        DocumentStampState mirror,
        DocumentStampState document
    )
    {
        mirror.ContentVersion.Should().Be(document.ContentVersion);
        mirror.ContentLastModifiedAt.Should().Be(document.ContentLastModifiedAt);
    }

    // tracked_changes_* rows are keyed by [Id], which holds the DocumentUuid (not DocumentId).
    private async Task<long> CountTrackedChangeRowsAsync(
        string schemaName,
        string tableName,
        Guid documentUuid
    )
    {
        return await _database.ExecuteScalarAsync<long>(
            $"""
            SELECT COUNT(*)
            FROM [{schemaName}].[{tableName}]
            WHERE [Id] = @documentUuid;
            """,
            new SqlParameter("@documentUuid", documentUuid)
        );
    }

    private async Task<IReadOnlyDictionary<string, object?>> GetLatestTrackedChangeRowAsync(
        string schemaName,
        string tableName,
        Guid documentUuid
    )
    {
        var rows = await _database.QueryRowsAsync(
            $"""
            SELECT TOP (1) *
            FROM [{schemaName}].[{tableName}]
            WHERE [Id] = @documentUuid
            ORDER BY [ChangeVersion] DESC;
            """,
            new SqlParameter("@documentUuid", documentUuid)
        );

        return rows.Single();
    }

    private static DateTimeOffset ReadDateTimeOffset(object? value)
    {
        return value switch
        {
            DateTimeOffset dateTimeOffset => dateTimeOffset,
            DateTime dateTime => new DateTimeOffset(
                dateTime.Kind == DateTimeKind.Unspecified
                    ? DateTime.SpecifyKind(dateTime, DateTimeKind.Utc)
                    : dateTime
            ),
            string text => DateTimeOffset.Parse(text, CultureInfo.InvariantCulture),
            _ => throw new InvalidOperationException(
                $"Unsupported timestamp value type '{value?.GetType().FullName ?? "<null>"}'."
            ),
        };
    }

    private async Task<string?> QueryClassPeriodNameAsync(long classPeriodDocumentId)
    {
        var rows = await _database.QueryRowsAsync(
            """
            SELECT [ClassPeriodName]
            FROM [edfi].[ClassPeriod]
            WHERE [DocumentId] = @documentId;
            """,
            new SqlParameter("@documentId", classPeriodDocumentId)
        );

        return Convert.ToString(rows.Single()["ClassPeriodName"], CultureInfo.InvariantCulture);
    }

    // The pooled QueryRowsAsync/ExecuteScalarAsync helpers open a fresh connection per call and cannot
    // observe uncommitted state, so pre-commit reads issue transaction-bound commands on the held
    // connection/transaction.
    private static async Task ExecuteNonQueryInTransactionAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string sql,
        params SqlParameter[] parameters
    )
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddRange(parameters);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string?> ExecuteScalarStringInTransactionAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string sql,
        params SqlParameter[] parameters
    )
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddRange(parameters);
        var result = await command.ExecuteScalarAsync();
        return result is null or DBNull ? null : Convert.ToString(result, CultureInfo.InvariantCulture);
    }

    private static async Task<long> CountRowsInTransactionAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string sql,
        params SqlParameter[] parameters
    )
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddRange(parameters);
        var result = await command.ExecuteScalarAsync();
        return Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    private static async Task<
        IReadOnlyList<IReadOnlyDictionary<string, object?>>
    > QueryRowsInTransactionAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string sql,
        params SqlParameter[] parameters
    )
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddRange(parameters);

        List<IReadOnlyDictionary<string, object?>> rows = [];
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            Dictionary<string, object?> row = new(StringComparer.Ordinal);
            for (var ordinal = 0; ordinal < reader.FieldCount; ordinal++)
            {
                row[reader.GetName(ordinal)] = await reader.IsDBNullAsync(ordinal)
                    ? null
                    : reader.GetValue(ordinal);
            }
            rows.Add(row);
        }

        return rows;
    }

    private static async Task<DocumentStampState> GetDocumentStampStateInTransactionAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        long documentId
    )
    {
        var row = (
            await QueryRowsInTransactionAsync(
                connection,
                transaction,
                """
                SELECT
                    [ContentVersion],
                    [IdentityVersion],
                    [ContentLastModifiedAt],
                    [IdentityLastModifiedAt]
                FROM [dms].[Document]
                WHERE [DocumentId] = @documentId;
                """,
                new SqlParameter("@documentId", documentId)
            )
        ).Single();

        return new(
            Convert.ToInt64(row["ContentVersion"], CultureInfo.InvariantCulture),
            Convert.ToInt64(row["IdentityVersion"], CultureInfo.InvariantCulture),
            ReadDateTimeOffset(row["ContentLastModifiedAt"]),
            ReadDateTimeOffset(row["IdentityLastModifiedAt"])
        );
    }

    private static async Task<DocumentStampState> GetRootMirrorStampStateInTransactionAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string schemaName,
        string tableName,
        long documentId
    )
    {
        var row = (
            await QueryRowsInTransactionAsync(
                connection,
                transaction,
                $"""
                SELECT
                    [ContentVersion],
                    [ContentLastModifiedAt]
                FROM [{schemaName}].[{tableName}]
                WHERE [DocumentId] = @documentId;
                """,
                new SqlParameter("@documentId", documentId)
            )
        ).Single();

        return new(
            Convert.ToInt64(row["ContentVersion"], CultureInfo.InvariantCulture),
            IdentityVersion: 0,
            ReadDateTimeOffset(row["ContentLastModifiedAt"]),
            IdentityLastModifiedAt: DateTimeOffset.UnixEpoch
        );
    }

    private static async Task<long> CountTrackedChangeRowsInTransactionAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string schemaName,
        string tableName,
        Guid documentUuid
    )
    {
        return await CountRowsInTransactionAsync(
            connection,
            transaction,
            $"""
            SELECT COUNT(*)
            FROM [{schemaName}].[{tableName}]
            WHERE [Id] = @documentUuid;
            """,
            new SqlParameter("@documentUuid", documentUuid)
        );
    }

    private static async Task<
        IReadOnlyDictionary<string, object?>
    > GetLatestTrackedChangeRowInTransactionAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string schemaName,
        string tableName,
        Guid documentUuid
    )
    {
        var rows = await QueryRowsInTransactionAsync(
            connection,
            transaction,
            $"""
            SELECT TOP (1) *
            FROM [{schemaName}].[{tableName}]
            WHERE [Id] = @documentUuid
            ORDER BY [ChangeVersion] DESC;
            """,
            new SqlParameter("@documentUuid", documentUuid)
        );

        return rows.Single();
    }
}
