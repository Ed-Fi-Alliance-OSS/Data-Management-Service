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
/// Runtime proof that the MSSQL identity-propagation trigger emitted for an upstream
/// resource (<c>ClassPeriod</c>) reaches a stored child-collection binding
/// (<c>BellScheduleClassPeriod</c>) and that the child stamp trigger then bumps the
/// owning root document's (<c>BellSchedule</c>) <c>ContentVersion</c>.
/// </summary>
[TestFixture]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
public class MssqlChildBindingIdentityPropagationTests
{
    private const string FixtureRelativePath = "src/dms/backend/Fixtures/authoritative/ds-5.2";

    private MssqlGeneratedDdlFixture _fixture = null!;
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
        _database = await MssqlGeneratedDdlTestDatabase.CreateProvisionedAsync(_fixture.GeneratedDdl);
    }

    [SetUp]
    public async Task SetUp()
    {
        await _database.ResetAsync();
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_database is not null)
        {
            await _database.DisposeAsync();
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

        var initialContentVersion = await QueryBellScheduleContentVersionAsync(bellScheduleDocumentId);
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

        // Assert — propagation updated the projected identity column on the child row
        var childRow = await QuerySingleBellScheduleClassPeriodAsync(bellScheduleDocumentId);
        Convert
            .ToString(childRow["ClassPeriod_ClassPeriodName"], CultureInfo.InvariantCulture)
            .Should()
            .Be(NewClassPeriodName);

        // Row count must be unchanged — propagation is an UPDATE, not an INSERT/DELETE
        var childRowsAfter = await QueryBellScheduleClassPeriodRowCountAsync(bellScheduleDocumentId);
        childRowsAfter
            .Should()
            .Be(
                childRowsBefore,
                "propagation must update projected identity columns, not insert/delete rows"
            );

        // FK anchor (ClassPeriod_DocumentId) must NOT change — propagation should only
        // touch the projected non-key identity columns, never the reference link itself.
        var childAnchorAfter = await QueryBellScheduleClassPeriodAnchorAsync(bellScheduleDocumentId);
        childAnchorAfter
            .Should()
            .Be(
                childAnchorBefore,
                "ClassPeriod_DocumentId is the reference anchor and must not change during identity propagation"
            );

        // The child stamp trigger (TR_BellScheduleClassPeriod_Stamp) must fire from the
        // propagation UPDATE and bump the owning BellSchedule root's ContentVersion.
        var finalContentVersion = await QueryBellScheduleContentVersionAsync(bellScheduleDocumentId);
        finalContentVersion
            .Should()
            .BeGreaterThan(
                initialContentVersion,
                "child stamp trigger must fire from the propagation UPDATE and bump the owning root ContentVersion"
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

    private async Task<long> QueryBellScheduleContentVersionAsync(long bellScheduleDocumentId)
    {
        return await _database.ExecuteScalarAsync<long>(
            """
            SELECT [ContentVersion]
            FROM [dms].[Document]
            WHERE [DocumentId] = @documentId;
            """,
            new SqlParameter("@documentId", bellScheduleDocumentId)
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
