// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using FluentAssertions;
using Npgsql;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Postgresql.Tests.Integration;

[TestFixture]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
public class Given_PostgresqlGeneratedDdlBaselineDatabase
{
    private const string FixtureRelativePath =
        "src/dms/backend/EdFi.DataManagementService.Backend.Ddl.Tests.Unit/Fixtures/focused/stable-key-extension-child-collections";

    private PostgresqlGeneratedDdlFixture _fixture = null!;

    [SetUp]
    public void Setup()
    {
        _fixture = PostgresqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(FixtureRelativePath);
    }

    [Test]
    public async Task It_creates_clean_isolated_clones_from_a_single_generated_ddl_baseline()
    {
        await using var baselineDatabase = await PostgresqlGeneratedDdlBaselineDatabase.CreateAsync(
            CreateFixtureSignature("isolated-clones"),
            _fixture.GeneratedDdl
        );

        PostgresqlGeneratedDdlBaselineCounts baselineCounts;
        PostgresqlGeneratedDdlDocumentState firstDocumentState;
        long firstCollectionItemId;
        string firstCloneDatabaseName;

        await using (var firstClone = await baselineDatabase.CreateIsolatedDatabaseAsync())
        {
            firstCloneDatabaseName = firstClone.DatabaseName;
            baselineCounts = await ReadBaselineCountsAsync(firstClone);
            firstDocumentState = await InsertSchoolDocumentAsync(
                firstClone,
                Guid.Parse("11111111-1111-1111-1111-111111111111"),
                100
            );
            firstCollectionItemId = await InsertSchoolAddressAsync(
                firstClone,
                firstDocumentState.DocumentId,
                1,
                "Austin"
            );
        }

        await using var secondClone = await baselineDatabase.CreateIsolatedDatabaseAsync();
        var secondBaselineCounts = await ReadBaselineCountsAsync(secondClone);
        var secondMutableCounts = await ReadMutableCountsAsync(secondClone);
        var secondDocumentState = await InsertSchoolDocumentAsync(
            secondClone,
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            100
        );
        var secondCollectionItemId = await InsertSchoolAddressAsync(
            secondClone,
            secondDocumentState.DocumentId,
            1,
            "Austin"
        );

        secondClone.DatabaseName.Should().NotBe(firstCloneDatabaseName);
        secondBaselineCounts.Should().Be(baselineCounts);
        secondMutableCounts.Should().Be(new PostgresqlGeneratedDdlMutableCounts(0, 0, 0));
        secondDocumentState.Should().Be(firstDocumentState);
        secondCollectionItemId.Should().Be(firstCollectionItemId);
    }

    [Test]
    public async Task It_reuses_a_shared_baseline_until_the_last_handle_is_disposed()
    {
        var fixtureSignature = CreateFixtureSignature("shared-baseline-reuse");
        PostgresqlGeneratedDdlBaselineDatabase? firstBaseline =
            await PostgresqlGeneratedDdlBaselineDatabase.CreateAsync(fixtureSignature, _fixture.GeneratedDdl);
        PostgresqlGeneratedDdlBaselineDatabase? secondBaseline = null;
        var baselineDatabaseName = firstBaseline.BaselineDatabaseName;

        try
        {
            secondBaseline = await PostgresqlGeneratedDdlBaselineDatabase.CreateAsync(
                fixtureSignature,
                _fixture.GeneratedDdl
            );
            secondBaseline.BaselineDatabaseName.Should().Be(baselineDatabaseName);

            await firstBaseline.DisposeAsync();
            firstBaseline = null;

            var existsAfterFirstDispose = await DatabaseExistsAsync(baselineDatabaseName);
            existsAfterFirstDispose.Should().BeTrue();

            await using var isolatedDatabase = await secondBaseline.CreateIsolatedDatabaseAsync();
            var mutableCounts = await ReadMutableCountsAsync(isolatedDatabase);
            mutableCounts.Should().Be(new PostgresqlGeneratedDdlMutableCounts(0, 0, 0));
        }
        finally
        {
            if (secondBaseline is not null)
            {
                await secondBaseline.DisposeAsync();
            }

            if (firstBaseline is not null)
            {
                await firstBaseline.DisposeAsync();
            }
        }

        var existsAfterLastDispose = await DatabaseExistsAsync(baselineDatabaseName);
        existsAfterLastDispose.Should().BeFalse();
    }

    private static async Task<PostgresqlGeneratedDdlBaselineCounts> ReadBaselineCountsAsync(
        PostgresqlGeneratedDdlTestDatabase database
    )
    {
        return new(
            await ReadTableCountAsync(database, "dms.\"EffectiveSchema\""),
            await ReadTableCountAsync(database, "dms.\"ResourceKey\""),
            await ReadTableCountAsync(database, "dms.\"SchemaComponent\"")
        );
    }

    private static async Task<PostgresqlGeneratedDdlMutableCounts> ReadMutableCountsAsync(
        PostgresqlGeneratedDdlTestDatabase database
    )
    {
        return new(
            await ReadTableCountAsync(database, "dms.\"Document\""),
            await ReadTableCountAsync(database, "edfi.\"School\""),
            await ReadTableCountAsync(database, "edfi.\"SchoolAddress\"")
        );
    }

    private static Task<long> ReadTableCountAsync(
        PostgresqlGeneratedDdlTestDatabase database,
        string qualifiedTableName
    )
    {
        return database.ExecuteScalarAsync<long>($"""SELECT COUNT(*) FROM {qualifiedTableName};""");
    }

    private static async Task<PostgresqlGeneratedDdlDocumentState> InsertSchoolDocumentAsync(
        PostgresqlGeneratedDdlTestDatabase database,
        Guid documentUuid,
        int schoolId
    )
    {
        var resourceKeyId = await database.ExecuteScalarAsync<short>(
            """
            SELECT "ResourceKeyId"
            FROM "dms"."ResourceKey"
            WHERE "ProjectName" = @projectName
              AND "ResourceName" = @resourceName;
            """,
            new NpgsqlParameter("projectName", "Ed-Fi"),
            new NpgsqlParameter("resourceName", "School")
        );

        var documentRows = await database.QueryRowsAsync(
            """
            INSERT INTO "dms"."Document" ("DocumentUuid", "ResourceKeyId")
            VALUES (@documentUuid, @resourceKeyId)
            RETURNING "DocumentId", "ContentVersion", "IdentityVersion";
            """,
            new NpgsqlParameter("documentUuid", documentUuid),
            new NpgsqlParameter("resourceKeyId", resourceKeyId)
        );
        var documentRow = documentRows.Should().ContainSingle().Which;
        var documentState = new PostgresqlGeneratedDdlDocumentState(
            Convert.ToInt64(documentRow["DocumentId"]),
            Convert.ToInt64(documentRow["ContentVersion"]),
            Convert.ToInt64(documentRow["IdentityVersion"])
        );

        await database.ExecuteNonQueryAsync(
            """
            INSERT INTO "edfi"."School" ("DocumentId", "SchoolId")
            VALUES (@documentId, @schoolId);
            """,
            new NpgsqlParameter("documentId", documentState.DocumentId),
            new NpgsqlParameter("schoolId", schoolId)
        );

        return documentState;
    }

    private static Task<long> InsertSchoolAddressAsync(
        PostgresqlGeneratedDdlTestDatabase database,
        long schoolDocumentId,
        int ordinal,
        string city
    )
    {
        return database.ExecuteScalarAsync<long>(
            """
            INSERT INTO "edfi"."SchoolAddress" ("Ordinal", "School_DocumentId", "City")
            VALUES (@ordinal, @schoolDocumentId, @city)
            RETURNING "CollectionItemId";
            """,
            new NpgsqlParameter("ordinal", ordinal),
            new NpgsqlParameter("schoolDocumentId", schoolDocumentId),
            new NpgsqlParameter("city", city)
        );
    }

    private static string CreateFixtureSignature(string scenario)
    {
        return $"{FixtureRelativePath}#{scenario}";
    }

    private static async Task<bool> DatabaseExistsAsync(string databaseName)
    {
        var builder = new NpgsqlConnectionStringBuilder(Configuration.DatabaseConnectionString)
        {
            Database = "postgres",
        };

        await using var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT EXISTS (
                SELECT 1
                FROM pg_database
                WHERE datname = @databaseName
            );
            """;
        command.Parameters.Add(new NpgsqlParameter("databaseName", databaseName));

        return (bool)(await command.ExecuteScalarAsync() ?? false);
    }
}
