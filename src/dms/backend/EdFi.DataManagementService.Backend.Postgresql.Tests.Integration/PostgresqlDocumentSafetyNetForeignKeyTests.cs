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
/// Proves the delete action of the safety-net foreign keys that reference <c>dms.Document</c> on a
/// database provisioned from the authoritative <c>ds-5.2</c> DDL (DMS-1236). Every resource root
/// table and <c>dms.Descriptor</c> must carry <c>ON DELETE RESTRICT</c>: the write path deletes the
/// root row before the <c>dms.Document</c> row, so the key never has anything to cascade, and
/// RESTRICT is PostgreSQL's single-probe check (NO ACTION would re-check the parent key before
/// every probe; CASCADE walked every root table). The cascade-maintained <c>dms.*</c> and
/// abstract-identity tables keep <c>ON DELETE CASCADE</c>. The behavioral consequence is also
/// pinned: a direct delete of a <c>dms.Document</c> row that still has a live root row is rejected,
/// while the ordered delete the application performs still succeeds.
/// </summary>
[TestFixture]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
public class Given_A_Provisioned_Postgresql_Database_Document_Safety_Net_Foreign_Keys
{
    private const string FixtureRelativePath = "src/dms/backend/Fixtures/authoritative/ds-5.2";

    private static readonly string[] _cascadeMaintainedTables =
    [
        "dms.DocumentCache",
        "dms.DocumentProjectionWork",
        "dms.ReferentialIdentity",
        "edfi.EducationOrganizationIdentity",
        "edfi.GeneralStudentProgramAssociationIdentity",
    ];

    private PostgresqlGeneratedDdlFixture _fixture = null!;
    private PostgresqlGeneratedDdlTestDatabase _database = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _fixture = PostgresqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(
            FixtureRelativePath,
            strict: true
        );
        _database = await PostgresqlGeneratedDdlTestDatabase.CreateProvisionedAsync(_fixture.GeneratedDdl);
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
        }
    }

    [Test]
    public async Task It_should_restrict_deletes_on_every_root_and_descriptor_foreign_key_and_cascade_only_the_maintained_tables()
    {
        var foreignKeys = await QueryDocumentForeignKeysAsync();

        var cascading = foreignKeys
            .Where(fk => fk.DeleteAction == "CASCADE")
            .Select(fk => fk.Table)
            .OrderBy(table => table, StringComparer.Ordinal)
            .ToArray();
        cascading.Should().Equal(_cascadeMaintainedTables.OrderBy(t => t, StringComparer.Ordinal));

        var others = foreignKeys.Where(fk => !_cascadeMaintainedTables.Contains(fk.Table)).ToArray();
        others.Should().NotBeEmpty();
        others.Should().OnlyContain(fk => fk.DeleteAction == "RESTRICT");
        others.Should().Contain(fk => fk.Table == "dms.Descriptor");
        others.Should().Contain(fk => fk.Table == "edfi.School");

        // One safety-net key per referencing table on a freshly provisioned database.
        foreignKeys.Select(fk => fk.Table).Should().OnlyHaveUniqueItems();
    }

    [Test]
    public async Task It_should_report_the_school_root_foreign_key_as_restrict_on_delete_and_no_action_on_update()
    {
        var schoolForeignKeys = await _database.GetForeignKeyMetadataAsync("edfi", "School");
        var documentKey = schoolForeignKeys.Single(fk => fk.ConstraintName == "FK_School_Document");

        documentKey.Columns.Should().Equal("DocumentId");
        documentKey.ReferencedSchema.Should().Be("dms");
        documentKey.ReferencedTable.Should().Be("Document");
        documentKey.ReferencedColumns.Should().Equal("DocumentId");
        documentKey.DeleteAction.Should().Be("RESTRICT");
        documentKey.UpdateAction.Should().Be("NO ACTION");
    }

    [Test]
    public async Task It_should_reject_a_direct_document_delete_while_the_root_row_exists()
    {
        var schoolDocumentId = await InsertSchoolDocumentAsync(schoolId: 100);

        var act = () =>
            _database.ExecuteNonQueryAsync(
                """DELETE FROM "dms"."Document" WHERE "DocumentId" = @documentId;""",
                new NpgsqlParameter("documentId", schoolDocumentId)
            );

        // PostgreSQL 18 reports a RESTRICT failure as SQLSTATE 23001 (restrict_violation); PostgreSQL 16 and
        // 17 report every referential-action failure as 23503 (foreign_key_violation). The delete action
        // itself is pinned by the catalog assertions above, so either code proves the rejection here.
        var exception = await act.Should().ThrowAsync<PostgresException>();
        exception
            .Which.SqlState.Should()
            .BeOneOf(PostgresErrorCodes.RestrictViolation, PostgresErrorCodes.ForeignKeyViolation);
        exception.Which.ConstraintName.Should().Be("FK_School_Document");

        (await CountRowsAsync("dms", "Document", schoolDocumentId)).Should().Be(1);
        (await CountRowsAsync("edfi", "School", schoolDocumentId)).Should().Be(1);
    }

    [Test]
    public async Task It_should_allow_the_ordered_delete_the_write_path_performs()
    {
        var schoolDocumentId = await InsertSchoolDocumentAsync(schoolId: 101);

        // OrderedDeleteCommandBuilder: root row first, then the dms.Document row, one transaction.
        await using var connection = new NpgsqlConnection(_database.ConnectionString);
        await connection.OpenAsync();
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                DELETE FROM "edfi"."School" WHERE "DocumentId" = @documentId;
                DELETE FROM "dms"."Document" WHERE "DocumentId" = @documentId;
                """;
            command.Parameters.AddWithValue("documentId", schoolDocumentId);
            await command.ExecuteNonQueryAsync();
            await transaction.CommitAsync();
        }

        (await CountRowsAsync("edfi", "School", schoolDocumentId)).Should().Be(0);
        (await CountRowsAsync("dms", "Document", schoolDocumentId)).Should().Be(0);
    }

    private sealed record DocumentForeignKey(string Table, string ConstraintName, string DeleteAction);

    private async Task<IReadOnlyList<DocumentForeignKey>> QueryDocumentForeignKeysAsync()
    {
        var rows = await _database.QueryRowsAsync(
            """
            SELECT child_namespace.nspname || '.' || child.relname AS table_name,
                   constraints.conname AS constraint_name,
                   CASE constraints.confdeltype
                       WHEN 'a' THEN 'NO ACTION'
                       WHEN 'r' THEN 'RESTRICT'
                       WHEN 'c' THEN 'CASCADE'
                       WHEN 'n' THEN 'SET NULL'
                       WHEN 'd' THEN 'SET DEFAULT'
                   END AS delete_action
            FROM pg_constraint constraints
            JOIN pg_class child ON child.oid = constraints.conrelid
            JOIN pg_namespace child_namespace ON child_namespace.oid = child.relnamespace
            WHERE constraints.contype = 'f'
              AND constraints.confrelid = '"dms"."Document"'::regclass
            ORDER BY 1, 2;
            """
        );

        return rows.Select(row => new DocumentForeignKey(
                (string)row["table_name"]!,
                (string)row["constraint_name"]!,
                (string)row["delete_action"]!
            ))
            .ToArray();
    }

    private async Task<long> InsertSchoolDocumentAsync(int schoolId)
    {
        var resourceKeyId = await _database.ExecuteScalarAsync<short>(
            """
            SELECT "ResourceKeyId"
            FROM "dms"."ResourceKey"
            WHERE "ProjectName" = 'Ed-Fi' AND "ResourceName" = 'School';
            """
        );
        var schoolDocumentId = await _database.ExecuteScalarAsync<long>(
            """
            INSERT INTO "dms"."Document" ("DocumentUuid", "ResourceKeyId")
            VALUES (@documentUuid, @resourceKeyId)
            RETURNING "DocumentId";
            """,
            new NpgsqlParameter("documentUuid", Guid.NewGuid()),
            new NpgsqlParameter("resourceKeyId", resourceKeyId)
        );

        // The School insert triggers maintain the EducationOrganizationIdentity alias row, which this
        // scenario does not need; FK enforcement (internal constraint triggers) stays active.
        await _database.ExecuteNonQueryAsync("""ALTER TABLE "edfi"."School" DISABLE TRIGGER USER;""");
        try
        {
            await _database.ExecuteNonQueryAsync(
                """
                INSERT INTO "edfi"."School" ("DocumentId", "NameOfInstitution", "SchoolId")
                VALUES (@documentId, 'Test School', @schoolId);
                """,
                new NpgsqlParameter("documentId", schoolDocumentId),
                new NpgsqlParameter("schoolId", schoolId)
            );
        }
        finally
        {
            await _database.ExecuteNonQueryAsync("""ALTER TABLE "edfi"."School" ENABLE TRIGGER USER;""");
        }

        return schoolDocumentId;
    }

    private Task<long> CountRowsAsync(string schema, string table, long documentId)
    {
        return _database.ExecuteScalarAsync<long>(
            $"""SELECT count(*) FROM "{schema}"."{table}" WHERE "DocumentId" = @documentId;""",
            new NpgsqlParameter("documentId", documentId)
        );
    }
}
