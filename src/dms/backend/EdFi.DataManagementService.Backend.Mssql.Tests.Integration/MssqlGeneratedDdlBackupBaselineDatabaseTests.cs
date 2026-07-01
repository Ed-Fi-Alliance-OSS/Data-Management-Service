// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Tests.Integration.Common;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Mssql.Tests.Integration;

internal sealed record MssqlGeneratedDdlBackupMutableCounts(
    long DocumentCount,
    long SchoolCount,
    long SchoolAddressCount
);

internal sealed record MssqlGeneratedDdlBackupDocumentState(
    long DocumentId,
    long ContentVersion,
    long IdentityVersion
);

[TestFixture]
[Category(MssqlCiShards.Shard4)]
public class Given_MssqlGeneratedDdlBackupBaselineFilePaths
{
    [Test]
    public void It_builds_a_backup_path_next_to_a_linux_container_data_file()
    {
        var backupPath = MssqlDatabaseFileMetadata.BuildBackupPath(
            databaseName: "dmsfp123",
            sourceDataFilePhysicalName: "/var/opt/mssql/data/dmsfp123.mdf"
        );

        backupPath.Should().Be("/var/opt/mssql/data/dmsfp123_baseline.bak");
    }

    [Test]
    public void It_builds_a_restore_path_next_to_a_windows_container_data_file()
    {
        MssqlDatabaseFileMetadata file = new(
            FileId: 1,
            LogicalName: "dmsfp123",
            Type: "D",
            PhysicalName: @"C:\SqlData\dmsfp123.mdf"
        );

        var restorePath = file.BuildRestorePath("dmsfp456");

        restorePath.Should().Be(@"C:\SqlData\dmsfp456_1_dmsfp123.mdf");
    }

    [Test]
    public void It_builds_distinct_restore_paths_for_the_same_logical_file_in_different_databases()
    {
        MssqlDatabaseFileMetadata file = new(
            FileId: 2,
            LogicalName: "dmsfp123_log",
            Type: "L",
            PhysicalName: "/var/opt/mssql/data/dmsfp123_log.ldf"
        );

        var firstRestorePath = file.BuildRestorePath("dmsfp111");
        var secondRestorePath = file.BuildRestorePath("dmsfp222");

        firstRestorePath.Should().Be("/var/opt/mssql/data/dmsfp111_2_dmsfp123_log.ldf");
        secondRestorePath.Should().Be("/var/opt/mssql/data/dmsfp222_2_dmsfp123_log.ldf");
    }
}

[TestFixture]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
[Category(MssqlCiShards.Shard4)]
public class Given_MssqlGeneratedDdlBackupBaselineDatabase
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
    public async Task It_restores_isolated_databases_from_one_backup()
    {
        await using var baselineDatabase = await MssqlGeneratedDdlBackupBaselineDatabase.CreateAsync(
            CreateFixtureSignature("isolated-restores"),
            _fixture.GeneratedDdl
        );
        await using var firstLease = await baselineDatabase.AcquireRestoredDatabaseAsync();
        await using var secondLease = await baselineDatabase.AcquireRestoredDatabaseAsync();

        firstLease.Database.DatabaseName.Should().NotBe(secondLease.Database.DatabaseName);

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

        secondMutableCountsBeforeInsert.Should().Be(new MssqlGeneratedDdlBackupMutableCounts(0, 0, 0));
        secondDocumentState.Should().Be(firstDocumentState);
        secondCollectionItemId.Should().Be(firstCollectionItemId);
    }

    [Test]
    public async Task It_drops_the_restored_database_when_the_lease_is_disposed()
    {
        await using var baselineDatabase = await MssqlGeneratedDdlBackupBaselineDatabase.CreateAsync(
            CreateFixtureSignature("drop-restored-database"),
            _fixture.GeneratedDdl
        );
        var lease = await baselineDatabase.AcquireRestoredDatabaseAsync();
        var databaseName = lease.Database.DatabaseName;

        (await DatabaseExistsAsync(databaseName)).Should().BeTrue();

        await lease.DisposeAsync();

        (await DatabaseExistsAsync(databaseName)).Should().BeFalse();
    }

    [Test]
    public async Task It_rejects_the_same_fixture_signature_with_different_generated_ddl()
    {
        var fixtureSignature = CreateFixtureSignature("hash-mismatch");
        await using var baselineDatabase = await MssqlGeneratedDdlBackupBaselineDatabase.CreateAsync(
            fixtureSignature,
            _fixture.GeneratedDdl
        );

        Func<Task> act = async () =>
            await MssqlGeneratedDdlBackupBaselineDatabase.CreateAsync(
                fixtureSignature,
                _fixture.GeneratedDdl + Environment.NewLine + "-- changed generated DDL hash"
            );

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Test]
    public async Task It_uses_unique_database_file_paths_for_concurrent_restored_leases()
    {
        await using var baselineDatabase = await MssqlGeneratedDdlBackupBaselineDatabase.CreateAsync(
            CreateFixtureSignature("concurrent-restore-file-paths"),
            _fixture.GeneratedDdl
        );
        MssqlGeneratedDdlBackupBaselineLease? firstLease = null;
        MssqlGeneratedDdlBackupBaselineLease? secondLease = null;

        try
        {
            MssqlGeneratedDdlBackupBaselineLease[] leases = await Task.WhenAll(
                baselineDatabase.AcquireRestoredDatabaseAsync(),
                baselineDatabase.AcquireRestoredDatabaseAsync()
            );
            firstLease = leases[0];
            secondLease = leases[1];

            IReadOnlyList<MssqlDatabaseFileMetadata> firstFiles =
                await MssqlGeneratedDdlBackupBaselineDatabase.ReadDatabaseFilesAsync(
                    firstLease.Database.DatabaseName
                );
            IReadOnlyList<MssqlDatabaseFileMetadata> secondFiles =
                await MssqlGeneratedDdlBackupBaselineDatabase.ReadDatabaseFilesAsync(
                    secondLease.Database.DatabaseName
                );

            firstFiles
                .Select(file => file.PhysicalName)
                .Should()
                .NotIntersectWith(secondFiles.Select(file => file.PhysicalName));
        }
        finally
        {
            if (secondLease is not null)
            {
                await secondLease.DisposeAsync();
            }

            if (firstLease is not null)
            {
                await firstLease.DisposeAsync();
            }
        }
    }

    private static async Task<MssqlGeneratedDdlBackupMutableCounts> ReadMutableCountsAsync(
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

    private static async Task<MssqlGeneratedDdlBackupDocumentState> InsertSchoolDocumentAsync(
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
        var documentState = new MssqlGeneratedDdlBackupDocumentState(
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

    private static async Task<long> InsertSchoolAddressAsync(
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
        return $"{FixtureRelativePath}#backup#{scenario}";
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
