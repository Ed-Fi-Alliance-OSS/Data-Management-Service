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
/// foreign keys and that the child stamp triggers then bump the owning root document's
/// <c>ContentVersion</c>. Exercises two different child-collection referrers cascading
/// from the same target (<c>BellScheduleClassPeriod</c> with owning root
/// <c>BellSchedule</c>, and <c>SectionClassPeriod</c> with owning root <c>Section</c>)
/// to demonstrate that the cascade fan-out is general across multiple child-collection
/// referrers of the same target, not hardcoded to a single binding.
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

        var initialContentVersion = await QueryDocumentContentVersionAsync(bellScheduleDocumentId);
        var childRowsBefore = await QueryBellScheduleClassPeriodRowCountAsync(bellScheduleDocumentId);
        var childAnchorBefore = await QueryBellScheduleClassPeriodAnchorAsync(bellScheduleDocumentId);
        childRowsBefore.Should().Be(1);

        // Add a small delay so any stamp comparison that checks
        // ContentLastModifiedAt would see a distinct timestamp too.
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
        var childRow = await QuerySingleBellScheduleClassPeriodAsync(bellScheduleDocumentId);
        Convert
            .ToString(childRow["ClassPeriod_ClassPeriodName"], CultureInfo.InvariantCulture)
            .Should()
            .Be(NewClassPeriodName);

        // Row count must be unchanged — the cascade is an UPDATE, not an INSERT/DELETE
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

        // The child stamp trigger (TR_BellScheduleClassPeriod_Stamp) must fire from the
        // cascade UPDATE and bump the owning BellSchedule root's ContentVersion.
        var finalContentVersion = await QueryDocumentContentVersionAsync(bellScheduleDocumentId);
        finalContentVersion
            .Should()
            .BeGreaterThan(
                initialContentVersion,
                "child stamp trigger must fire from the cascade UPDATE and bump the owning root ContentVersion"
            );
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
}
