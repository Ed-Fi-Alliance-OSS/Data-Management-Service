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
public class Given_A_Provisioned_Postgresql_Database_With_Journaling_Trigger
{
    private const string FixtureRelativePath =
        "src/dms/backend/EdFi.DataManagementService.Backend.Ddl.Tests.Unit/Fixtures/small/minimal";

    private PostgresqlGeneratedDdlTestDatabase _database = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        var fixture = PostgresqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(FixtureRelativePath);
        _database = await PostgresqlGeneratedDdlTestDatabase.CreateProvisionedAsync(fixture.GeneratedDdl);
    }

    [SetUp]
    public async Task Setup()
    {
        await _database.ExecuteNonQueryAsync(
            """
            DELETE FROM dms."Document";
            """
        );
    }

    [OneTimeTearDown]
    public async Task TearDown()
    {
        if (_database is not null)
        {
            await _database.DisposeAsync();
        }
    }

    [Test]
    public async Task It_emits_journal_row_on_document_insert()
    {
        var resourceKeyId = await _database.ExecuteScalarAsync<short>(
            """SELECT MIN("ResourceKeyId") FROM dms."ResourceKey";"""
        );

        var documentId = await _database.ExecuteScalarAsync<long>(
            """
            INSERT INTO dms."Document" ("DocumentUuid", "ResourceKeyId")
            VALUES (@uuid, @resourceKeyId)
            RETURNING "DocumentId";
            """,
            new NpgsqlParameter("uuid", Guid.NewGuid()),
            new NpgsqlParameter("resourceKeyId", resourceKeyId)
        );

        var journalRows = await _database.QueryRowsAsync(
            """
            SELECT "ChangeVersion", "DocumentId", "ResourceKeyId"
            FROM dms."DocumentChangeEvent"
            """,
            new NpgsqlParameter("documentId", documentId)
        );

        journalRows.Should().HaveCount(1);
        var journalRow = journalRows.Single();
        Convert.ToInt64(journalRow["DocumentId"]).Should().Be(documentId);
        Convert.ToInt16(journalRow["ResourceKeyId"]).Should().Be(resourceKeyId);
        Convert.ToInt64(journalRow["ChangeVersion"]).Should().BeGreaterThan(0);
    }

    [Test]
    public async Task It_emits_distinct_change_versions_on_multi_row_update()
    {
        var resourceKeyId = await _database.ExecuteScalarAsync<short>(
            """SELECT MIN("ResourceKeyId") FROM dms."ResourceKey";"""
        );

        var documentIds = new long[3];
        for (int i = 0; i < 3; i++)
        {
            documentIds[i] = await _database.ExecuteScalarAsync<long>(
                """
                INSERT INTO dms."Document" ("DocumentUuid", "ResourceKeyId")
                VALUES (@uuid, @resourceKeyId)
                RETURNING "DocumentId";
                """,
                new NpgsqlParameter("uuid", Guid.NewGuid()),
                new NpgsqlParameter("resourceKeyId", resourceKeyId)
            );
        }

        await _database.ExecuteNonQueryAsync(
            """
            DELETE FROM dms."DocumentChangeEvent";
            """
        );

        await _database.ExecuteNonQueryAsync(
            """
            UPDATE dms."Document"
            SET "ContentVersion" = nextval('"dms"."ChangeVersionSequence"')
            """
        );

        var journalRows = await _database.QueryRowsAsync(
            """
            SELECT "ChangeVersion", "DocumentId", "ResourceKeyId"
            FROM dms."DocumentChangeEvent"
            """
        );

        journalRows.Should().HaveCount(3, "one journal row per updated document");
        journalRows
            .Select(r => r["ChangeVersion"])
            .Distinct()
            .Should()
            .HaveCount(3, "each ChangeVersion must be distinct");

        var journalRow1 = journalRows.Single(r => Convert.ToInt64(r["DocumentId"]) == documentIds[0]);
        Convert.ToInt16(journalRow1["ResourceKeyId"]).Should().Be(resourceKeyId);
        Convert.ToInt64(journalRow1["ChangeVersion"]).Should().BeGreaterThan(0);

        var journalRow2 = journalRows.Single(r => Convert.ToInt64(r["DocumentId"]) == documentIds[1]);
        Convert.ToInt16(journalRow2["ResourceKeyId"]).Should().Be(resourceKeyId);
        Convert.ToInt64(journalRow2["ChangeVersion"]).Should().BeGreaterThan(0);

        var journalRow3 = journalRows.Single(r => Convert.ToInt64(r["DocumentId"]) == documentIds[2]);
        Convert.ToInt16(journalRow3["ResourceKeyId"]).Should().Be(resourceKeyId);
        Convert.ToInt64(journalRow3["ChangeVersion"]).Should().BeGreaterThan(0);
    }
}
