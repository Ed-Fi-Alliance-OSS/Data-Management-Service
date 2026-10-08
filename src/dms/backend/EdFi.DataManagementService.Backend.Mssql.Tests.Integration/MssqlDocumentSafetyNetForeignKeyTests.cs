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
/// SQL Server counterpart to the PostgreSQL
/// <c>Given_A_Provisioned_Postgresql_Database_Document_Safety_Net_Foreign_Keys</c> fixture.
/// The relational model's <c>Restrict</c> delete action renders as <c>ON DELETE NO ACTION</c> here,
/// because SQL Server has no RESTRICT and its NO ACTION is already a single immediate probe. What
/// matters is that no resource root or <c>dms.Descriptor</c> key cascades any more: with CASCADE the
/// <c>dms.Document</c> DELETE plan referenced every root table and its children (hundreds of
/// tables) and ran 16x slower on the volume test. The cascade-maintained <c>dms.*</c> and
/// abstract-identity tables keep <c>ON DELETE CASCADE</c>. A direct delete of a <c>dms.Document</c>
/// row that still has a live root row is rejected (error 547); the ordered delete the application
/// performs still succeeds.
/// </summary>
[TestFixture]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
[Category(MssqlCiShards.Shard1)]
public class Given_A_Provisioned_Mssql_Database_Document_Safety_Net_Foreign_Keys
{
    private const string FixtureRelativePath = "src/dms/backend/Fixtures/authoritative/ds-5.2";
    private const int ForeignKeyViolationErrorNumber = 547;

    private static readonly string[] _cascadeMaintainedTables =
    [
        "dms.DocumentCache",
        "dms.DocumentProjectionWork",
        "dms.ReferentialIdentity",
        "edfi.EducationOrganizationIdentity",
        "edfi.GeneralStudentProgramAssociationIdentity",
    ];

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
    public async Task It_should_use_no_action_on_every_root_and_descriptor_foreign_key_and_cascade_only_the_maintained_tables()
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
        others.Should().OnlyContain(fk => fk.DeleteAction == "NO ACTION");
        others.Should().Contain(fk => fk.Table == "dms.Descriptor");
        others.Should().Contain(fk => fk.Table == "edfi.School");

        // One safety-net key per referencing table on a freshly provisioned database.
        foreignKeys.Select(fk => fk.Table).Should().OnlyHaveUniqueItems();
    }

    [Test]
    public async Task It_should_report_the_school_root_foreign_key_as_no_action_on_delete_and_update()
    {
        var schoolForeignKeys = await _database.GetForeignKeyMetadataAsync("edfi", "School");
        var documentKey = schoolForeignKeys.Single(fk => fk.ConstraintName == "FK_School_Document");

        documentKey.Columns.Should().Equal("DocumentId");
        documentKey.ReferencedSchema.Should().Be("dms");
        documentKey.ReferencedTable.Should().Be("Document");
        documentKey.ReferencedColumns.Should().Equal("DocumentId");
        documentKey.DeleteAction.Should().Be("NO ACTION");
        documentKey.UpdateAction.Should().Be("NO ACTION");
    }

    [Test]
    public async Task It_should_reject_a_direct_document_delete_while_the_root_row_exists()
    {
        var schoolDocumentId = await InsertSchoolDocumentAsync(schoolId: 100);

        var act = () =>
            _database.ExecuteNonQueryAsync(
                "DELETE FROM [dms].[Document] WHERE [DocumentId] = @documentId;",
                new SqlParameter("@documentId", schoolDocumentId)
            );

        var exception = await act.Should().ThrowAsync<SqlException>();
        exception.Which.Number.Should().Be(ForeignKeyViolationErrorNumber);
        exception.Which.Message.Should().Contain("FK_School_Document");

        (await CountRowsAsync("dms", "Document", schoolDocumentId)).Should().Be(1);
        (await CountRowsAsync("edfi", "School", schoolDocumentId)).Should().Be(1);
    }

    [Test]
    public async Task It_should_allow_the_ordered_delete_the_write_path_performs()
    {
        var schoolDocumentId = await InsertSchoolDocumentAsync(schoolId: 101);

        // OrderedDeleteCommandBuilder: root row first, then the dms.Document row, one transaction.
        await using var connection = new SqlConnection(_database.ConnectionString);
        await connection.OpenAsync();
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await using var command = connection.CreateCommand();
            command.Transaction = (SqlTransaction)transaction;
            command.CommandText = """
                DELETE FROM [edfi].[School] WHERE [DocumentId] = @documentId;
                DELETE FROM [dms].[Document] WHERE [DocumentId] = @documentId;
                """;
            command.Parameters.Add(new SqlParameter("@documentId", schoolDocumentId));
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
            SELECT SCHEMA_NAME(parent.schema_id) + '.' + parent.name AS table_name,
                   foreign_keys.name AS constraint_name,
                   REPLACE(foreign_keys.delete_referential_action_desc, '_', ' ') AS delete_action
            FROM sys.foreign_keys
            JOIN sys.tables AS parent ON parent.object_id = foreign_keys.parent_object_id
            WHERE foreign_keys.referenced_object_id = OBJECT_ID(N'[dms].[Document]')
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
            SELECT [ResourceKeyId]
            FROM [dms].[ResourceKey]
            WHERE [ProjectName] = 'Ed-Fi' AND [ResourceName] = 'School';
            """
        );
        var schoolDocumentId = await _database.ExecuteScalarAsync<long>(
            """
            DECLARE @Inserted TABLE ([DocumentId] bigint);
            INSERT INTO [dms].[Document] ([DocumentUuid], [ResourceKeyId])
            OUTPUT INSERTED.[DocumentId] INTO @Inserted ([DocumentId])
            VALUES (@documentUuid, @resourceKeyId);
            SELECT TOP (1) [DocumentId] FROM @Inserted;
            """,
            new SqlParameter("@documentUuid", Guid.NewGuid()),
            new SqlParameter("@resourceKeyId", resourceKeyId)
        );

        // The School insert triggers maintain the EducationOrganizationIdentity alias row, which this
        // scenario does not need; foreign keys are constraints, not triggers, and stay enforced.
        await _database.ExecuteNonQueryAsync("DISABLE TRIGGER ALL ON [edfi].[School];");
        try
        {
            await _database.ExecuteNonQueryAsync(
                """
                INSERT INTO [edfi].[School] ([DocumentId], [NameOfInstitution], [SchoolId])
                VALUES (@documentId, 'Test School', @schoolId);
                """,
                new SqlParameter("@documentId", schoolDocumentId),
                new SqlParameter("@schoolId", schoolId)
            );
        }
        finally
        {
            await _database.ExecuteNonQueryAsync("ENABLE TRIGGER ALL ON [edfi].[School];");
        }

        return schoolDocumentId;
    }

    private Task<int> CountRowsAsync(string schema, string table, long documentId)
    {
        return _database.ExecuteScalarAsync<int>(
            $"SELECT COUNT(*) FROM [{schema}].[{table}] WHERE [DocumentId] = @documentId;",
            new SqlParameter("@documentId", documentId)
        );
    }
}
