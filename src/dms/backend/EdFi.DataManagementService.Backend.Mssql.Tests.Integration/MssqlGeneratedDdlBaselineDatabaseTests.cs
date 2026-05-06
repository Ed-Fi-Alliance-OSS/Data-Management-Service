// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using FluentAssertions;
using Microsoft.Data.SqlClient;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Mssql.Tests.Integration;

internal sealed record MssqlGeneratedDdlBaselineMutableCounts(
    long DocumentCount,
    long SchoolCount,
    long SchoolAddressCount
);

[TestFixture]
public class Given_MssqlGeneratedDdlBaselineSnapshotPath
{
    [Test]
    public void It_preserves_linux_container_path_separators()
    {
        var snapshotPath = MssqlGeneratedDdlBaselineDatabase.BuildSnapshotPath(
            physicalName: "/var/opt/mssql/data/dmsfp123.mdf",
            databaseName: "dmsfp123",
            fileId: 1,
            logicalName: "dmsfp123"
        );

        snapshotPath.Should().Be("/var/opt/mssql/data/dmsfp123_baseline_1_dmsfp123.ss");
    }

    [Test]
    public void It_preserves_windows_path_separators()
    {
        var snapshotPath = MssqlGeneratedDdlBaselineDatabase.BuildSnapshotPath(
            physicalName: @"C:\SqlData\dmsfp123.mdf",
            databaseName: "dmsfp123",
            fileId: 1,
            logicalName: "dmsfp123"
        );

        snapshotPath.Should().Be(@"C:\SqlData\dmsfp123_baseline_1_dmsfp123.ss");
    }
}

[TestFixture]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
public class Given_MssqlGeneratedDdlBaselineDatabase
{
    private const string FixtureRelativePath =
        "src/dms/backend/EdFi.DataManagementService.Backend.Ddl.Tests.Unit/Fixtures/focused/stable-key-extension-child-collections";

    private MssqlGeneratedDdlFixture _fixture = null!;

    [SetUp]
    public void Setup()
    {
        if (!MssqlTestDatabaseHelper.IsConfigured())
        {
            Assert.Ignore(
                "SQL Server integration tests require a MssqlAdmin connection string in appsettings.Test.json"
            );
        }

        _fixture = MssqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(FixtureRelativePath);
    }

    [Test]
    public async Task It_restores_a_returned_slot_to_a_clean_snapshot_baseline_before_reuse()
    {
        await using var baselineDatabase = await MssqlGeneratedDdlBaselineDatabase.CreateAsync(
            CreateFixtureSignature("clean-snapshot-baseline"),
            _fixture.GeneratedDdl
        );

        string databaseName = string.Empty;
        string snapshotName = string.Empty;
        MssqlGeneratedDdlBaselineCounts baselineCounts = null!;
        MssqlGeneratedDdlDocumentState firstDocumentState = null!;
        var firstCollectionItemId = 0L;

        await using (var firstLease = await baselineDatabase.AcquireRestoredDatabaseAsync())
        {
            databaseName = firstLease.Database.DatabaseName;
            snapshotName = firstLease.SnapshotName;
            baselineCounts = await ReadBaselineCountsAsync(firstLease.Database);
            firstDocumentState = await InsertSchoolDocumentAsync(
                firstLease.Database,
                Guid.Parse("11111111-1111-1111-1111-111111111111"),
                100
            );
            firstCollectionItemId = await InsertSchoolAddressAsync(
                firstLease.Database,
                firstDocumentState.DocumentId,
                1,
                "Austin"
            );
        }

        await using var secondLease = await baselineDatabase.AcquireRestoredDatabaseAsync();
        var resetBaselineCounts = await ReadBaselineCountsAsync(secondLease.Database);
        var resetMutableCounts = await ReadMutableCountsAsync(secondLease.Database);
        var secondDocumentState = await InsertSchoolDocumentAsync(
            secondLease.Database,
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            100
        );
        var secondCollectionItemId = await InsertSchoolAddressAsync(
            secondLease.Database,
            secondDocumentState.DocumentId,
            1,
            "Austin"
        );

        secondLease.Database.DatabaseName.Should().Be(databaseName);
        secondLease.SnapshotName.Should().Be(snapshotName);
        resetBaselineCounts.Should().Be(baselineCounts);
        resetMutableCounts.Should().Be(new MssqlGeneratedDdlBaselineMutableCounts(0, 0, 0));
        secondDocumentState.Should().Be(firstDocumentState);
        secondCollectionItemId.Should().Be(firstCollectionItemId);
    }

    [Test]
    public async Task It_provides_isolated_slots_to_overlapping_consumers_sharing_a_fixture_signature()
    {
        var fixtureSignature = CreateFixtureSignature("isolated-overlapping-consumers");
        await using var firstBaseline = await MssqlGeneratedDdlBaselineDatabase.CreateAsync(
            fixtureSignature,
            _fixture.GeneratedDdl
        );
        await using var secondBaseline = await MssqlGeneratedDdlBaselineDatabase.CreateAsync(
            fixtureSignature,
            _fixture.GeneratedDdl
        );
        await using var firstLease = await firstBaseline.AcquireRestoredDatabaseAsync();
        await using var secondLease = await secondBaseline.AcquireRestoredDatabaseAsync();

        firstLease.Database.DatabaseName.Should().NotBe(secondLease.Database.DatabaseName);
        firstLease.SnapshotName.Should().NotBe(secondLease.SnapshotName);

        var firstDocumentState = await InsertSchoolDocumentAsync(
            firstLease.Database,
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            100
        );
        var firstCollectionItemId = await InsertSchoolAddressAsync(
            firstLease.Database,
            firstDocumentState.DocumentId,
            1,
            "Austin"
        );

        var secondMutableCountsBeforeInsert = await ReadMutableCountsAsync(secondLease.Database);
        var secondDocumentState = await InsertSchoolDocumentAsync(
            secondLease.Database,
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            100
        );
        var secondCollectionItemId = await InsertSchoolAddressAsync(
            secondLease.Database,
            secondDocumentState.DocumentId,
            1,
            "Austin"
        );

        secondMutableCountsBeforeInsert.Should().Be(new MssqlGeneratedDdlBaselineMutableCounts(0, 0, 0));
        secondDocumentState.Should().Be(firstDocumentState);
        secondCollectionItemId.Should().Be(firstCollectionItemId);
    }

    [Test]
    public async Task It_keeps_pooled_slots_alive_until_the_last_manager_and_slot_lease_are_disposed()
    {
        var fixtureSignature = CreateFixtureSignature("shared-baseline-reuse");
        MssqlGeneratedDdlBaselineDatabase? firstBaseline =
            await MssqlGeneratedDdlBaselineDatabase.CreateAsync(fixtureSignature, _fixture.GeneratedDdl);
        MssqlGeneratedDdlBaselineDatabase? secondBaseline = null;
        MssqlGeneratedDdlBaselineLease? baselineLease = null;
        var databaseName = string.Empty;
        var snapshotName = string.Empty;

        try
        {
            secondBaseline = await MssqlGeneratedDdlBaselineDatabase.CreateAsync(
                fixtureSignature,
                _fixture.GeneratedDdl
            );
            baselineLease = await secondBaseline.AcquireRestoredDatabaseAsync();
            databaseName = baselineLease.Database.DatabaseName;
            snapshotName = baselineLease.SnapshotName;

            await firstBaseline.DisposeAsync();
            firstBaseline = null;
            await secondBaseline.DisposeAsync();
            secondBaseline = null;

            var databaseExistsAfterManagerDispose = await DatabaseExistsAsync(databaseName);
            var snapshotExistsAfterManagerDispose = await DatabaseExistsAsync(snapshotName);
            databaseExistsAfterManagerDispose.Should().BeTrue();
            snapshotExistsAfterManagerDispose.Should().BeTrue();

            var mutableCounts = await ReadMutableCountsAsync(baselineLease.Database);
            mutableCounts.Should().Be(new MssqlGeneratedDdlBaselineMutableCounts(0, 0, 0));
        }
        finally
        {
            if (baselineLease is not null)
            {
                await baselineLease.DisposeAsync();
            }

            if (secondBaseline is not null)
            {
                await secondBaseline.DisposeAsync();
            }

            if (firstBaseline is not null)
            {
                await firstBaseline.DisposeAsync();
            }
        }

        var databaseExistsAfterLastDispose = await DatabaseExistsAsync(databaseName);
        var snapshotExistsAfterLastDispose = await DatabaseExistsAsync(snapshotName);
        databaseExistsAfterLastDispose.Should().BeFalse();
        snapshotExistsAfterLastDispose.Should().BeFalse();
    }

    private static async Task<MssqlGeneratedDdlBaselineCounts> ReadBaselineCountsAsync(
        MssqlGeneratedDdlTestDatabase database
    )
    {
        return new(
            await ReadTableCountAsync(database, "[dms].[EffectiveSchema]"),
            await ReadTableCountAsync(database, "[dms].[ResourceKey]"),
            await ReadTableCountAsync(database, "[dms].[SchemaComponent]")
        );
    }

    private static async Task<MssqlGeneratedDdlBaselineMutableCounts> ReadMutableCountsAsync(
        MssqlGeneratedDdlTestDatabase database
    )
    {
        return new(
            await ReadTableCountAsync(database, "[dms].[Document]"),
            await ReadTableCountAsync(database, "[edfi].[School]"),
            await ReadTableCountAsync(database, "[edfi].[SchoolAddress]")
        );
    }

    private static Task<long> ReadTableCountAsync(
        MssqlGeneratedDdlTestDatabase database,
        string qualifiedTableName
    )
    {
        return database.ExecuteScalarAsync<long>($"""SELECT COUNT(*) FROM {qualifiedTableName};""");
    }

    private static async Task<MssqlGeneratedDdlDocumentState> InsertSchoolDocumentAsync(
        MssqlGeneratedDdlTestDatabase database,
        Guid documentUuid,
        int schoolId
    )
    {
        var resourceKeyId = await database.ExecuteScalarAsync<short>(
            """
            SELECT [ResourceKeyId]
            FROM [dms].[ResourceKey]
            WHERE [ProjectName] = @projectName
              AND [ResourceName] = @resourceName;
            """,
            new SqlParameter("@projectName", "Ed-Fi"),
            new SqlParameter("@resourceName", "School")
        );

        await database.ExecuteNonQueryAsync(
            """
            INSERT INTO [dms].[Document] ([DocumentUuid], [ResourceKeyId])
            VALUES (@documentUuid, @resourceKeyId);
            """,
            new SqlParameter("@documentUuid", documentUuid),
            new SqlParameter("@resourceKeyId", resourceKeyId)
        );

        var documentRows = await database.QueryRowsAsync(
            """
            SELECT [DocumentId], [ContentVersion], [IdentityVersion]
            FROM [dms].[Document]
            WHERE [DocumentUuid] = @documentUuid;
            """,
            new SqlParameter("@documentUuid", documentUuid)
        );
        var documentRow = documentRows.Should().ContainSingle().Which;
        var documentState = new MssqlGeneratedDdlDocumentState(
            Convert.ToInt64(documentRow["DocumentId"]),
            Convert.ToInt64(documentRow["ContentVersion"]),
            Convert.ToInt64(documentRow["IdentityVersion"])
        );

        await database.ExecuteNonQueryAsync(
            """
            INSERT INTO [edfi].[School] ([DocumentId], [SchoolId])
            VALUES (@documentId, @schoolId);
            """,
            new SqlParameter("@documentId", documentState.DocumentId),
            new SqlParameter("@schoolId", schoolId)
        );

        return documentState;
    }

    private static Task<long> InsertSchoolAddressAsync(
        MssqlGeneratedDdlTestDatabase database,
        long schoolDocumentId,
        int ordinal,
        string city
    )
    {
        return InsertSchoolAddressCoreAsync(database, schoolDocumentId, ordinal, city);
    }

    private static async Task<long> InsertSchoolAddressCoreAsync(
        MssqlGeneratedDdlTestDatabase database,
        long schoolDocumentId,
        int ordinal,
        string city
    )
    {
        await database.ExecuteNonQueryAsync(
            """
            INSERT INTO [edfi].[SchoolAddress] ([School_DocumentId], [Ordinal], [City])
            VALUES (@schoolDocumentId, @ordinal, @city);
            """,
            new SqlParameter("@schoolDocumentId", schoolDocumentId),
            new SqlParameter("@ordinal", ordinal),
            new SqlParameter("@city", city)
        );

        return await database.ExecuteScalarAsync<long>(
            """
            SELECT [CollectionItemId]
            FROM [edfi].[SchoolAddress]
            WHERE [School_DocumentId] = @schoolDocumentId
              AND [Ordinal] = @ordinal;
            """,
            new SqlParameter("@schoolDocumentId", schoolDocumentId),
            new SqlParameter("@ordinal", ordinal)
        );
    }

    private static string CreateFixtureSignature(string scenario)
    {
        return $"{FixtureRelativePath}#{scenario}";
    }

    private static async Task<bool> DatabaseExistsAsync(string databaseName)
    {
        await using SqlConnection connection = new(Configuration.MssqlAdminConnectionString!);
        await connection.OpenAsync();
        await using SqlCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT CASE
                WHEN EXISTS (
                    SELECT 1
                    FROM sys.databases
                    WHERE [name] = @databaseName
                )
                THEN CAST(1 AS bit)
                ELSE CAST(0 AS bit)
            END;
            """;
        command.Parameters.Add(new SqlParameter("@databaseName", databaseName));

        return (bool)(await command.ExecuteScalarAsync() ?? false);
    }
}
