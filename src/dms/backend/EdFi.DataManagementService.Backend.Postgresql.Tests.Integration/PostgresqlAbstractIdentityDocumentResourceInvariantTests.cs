// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Tests.Common;
using EdFi.DataManagementService.Backend.Tests.Integration.Common;
using FluentAssertions;
using Npgsql;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Postgresql.Tests.Integration;

/// <summary>
/// Proves the document/resource invariant on the abstract identity tables of a database provisioned
/// from the authoritative <c>ds-5.2</c> DDL. Every abstract identity row carries the concrete member's
/// <c>ResourceKeyId</c>, written by the member's maintenance trigger from a compile-time literal, and
/// <c>FK_&lt;Abstract&gt;Identity_DocumentResourceKey</c> pins it to the owning <c>dms.Document</c> row.
/// <c>ResourceKeyId</c> and <c>Discriminator</c> are payload only and never part of the natural key.
/// Seeds use raw SQL with every trigger enabled, so the generated triggers do the maintenance.
/// </summary>
[TestFixture]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
public class Given_A_Provisioned_Postgresql_Database_Abstract_Identity_Document_Resource_Invariant
{
    private const string FixtureRelativePath = "src/dms/backend/Fixtures/authoritative/ds-5.2";
    private const string DocumentResourceKeyForeignKey =
        "FK_EducationOrganizationIdentity_DocumentResourceKey";
    private const string NaturalKeyConstraint = "UX_EducationOrganizationIdentity_NK";

    private static readonly DateOnly _beginDate = new(2026, 8, 15);

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
    public async Task It_stamps_each_member_row_with_its_own_concrete_resource_key()
    {
        var schoolResourceKeyId = await GetResourceKeyIdAsync("School");
        var stateEducationAgencyResourceKeyId = await GetResourceKeyIdAsync("StateEducationAgency");
        var schoolDocumentId = await SeedSchoolAsync(schoolId: 100);
        var stateEducationAgencyDocumentId = await SeedStateEducationAgencyAsync(stateEducationAgencyId: 200);

        var school = await ReadIdentityRowAsync(schoolDocumentId);
        var stateEducationAgency = await ReadIdentityRowAsync(stateEducationAgencyDocumentId);

        schoolResourceKeyId.Should().NotBe(stateEducationAgencyResourceKeyId);
        school
            .Should()
            .Be(new EducationOrganizationRow(schoolDocumentId, 100, schoolResourceKeyId, "Ed-Fi:School"));
        stateEducationAgency
            .Should()
            .Be(
                new EducationOrganizationRow(
                    stateEducationAgencyDocumentId,
                    200,
                    stateEducationAgencyResourceKeyId,
                    "Ed-Fi:StateEducationAgency"
                )
            );
        (await ReadDocumentResourceKeyIdAsync(schoolDocumentId)).Should().Be(school.ResourceKeyId);
        (await ReadDocumentResourceKeyIdAsync(stateEducationAgencyDocumentId))
            .Should()
            .Be(stateEducationAgency.ResourceKeyId);
    }

    [Test]
    public async Task It_projects_the_same_rows_from_the_union_view()
    {
        var schoolDocumentId = await SeedSchoolAsync(schoolId: 100);
        var stateEducationAgencyDocumentId = await SeedStateEducationAgencyAsync(stateEducationAgencyId: 200);

        foreach (var documentId in new[] { schoolDocumentId, stateEducationAgencyDocumentId })
        {
            (await ReadViewRowAsync(documentId)).Should().Be(await ReadIdentityRowAsync(documentId));
        }
    }

    [Test]
    public async Task It_follows_an_identity_rename_without_changing_the_resource_key()
    {
        var schoolResourceKeyId = await GetResourceKeyIdAsync("School");
        var schoolDocumentId = await SeedSchoolAsync(schoolId: 100);

        await _database.ExecuteNonQueryAsync(
            """UPDATE "edfi"."School" SET "SchoolId" = 101 WHERE "DocumentId" = @documentId;""",
            new NpgsqlParameter("documentId", schoolDocumentId)
        );

        var expected = new EducationOrganizationRow(
            schoolDocumentId,
            101,
            schoolResourceKeyId,
            "Ed-Fi:School"
        );
        (await ReadIdentityRowAsync(schoolDocumentId)).Should().Be(expected);
        (await ReadViewRowAsync(schoolDocumentId)).Should().Be(expected);
    }

    [Test]
    public async Task It_removes_the_abstract_identity_row_with_the_ordered_delete()
    {
        var schoolDocumentId = await SeedSchoolAsync(schoolId: 100);
        long identityRowsAfterRootDelete;
        long identityRowsAfterDocumentDelete;

        // OrderedDeleteCommandBuilder: root row first, then the dms.Document row, one transaction. The
        // root delete leaves the identity row in place; the composite FK's cascade removes it.
        await using var connection = new NpgsqlConnection(_database.ConnectionString);
        await connection.OpenAsync();
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await ExecuteInTransactionAsync(
                connection,
                transaction,
                """DELETE FROM "edfi"."School" WHERE "DocumentId" = @documentId;""",
                schoolDocumentId
            );
            identityRowsAfterRootDelete = await CountIdentityRowsInTransactionAsync(
                connection,
                transaction,
                schoolDocumentId
            );
            await ExecuteInTransactionAsync(
                connection,
                transaction,
                """DELETE FROM "dms"."Document" WHERE "DocumentId" = @documentId;""",
                schoolDocumentId
            );
            identityRowsAfterDocumentDelete = await CountIdentityRowsInTransactionAsync(
                connection,
                transaction,
                schoolDocumentId
            );
            await transaction.CommitAsync();
        }

        identityRowsAfterRootDelete.Should().Be(1);
        identityRowsAfterDocumentDelete.Should().Be(0);
        (await CountRowsAsync("edfi", "EducationOrganization_View", schoolDocumentId)).Should().Be(0);
        (await CountRowsAsync("dms", "Document", schoolDocumentId)).Should().Be(0);
    }

    [Test]
    public async Task It_keeps_the_resource_key_and_discriminator_out_of_the_natural_key()
    {
        var stateEducationAgencyResourceKeyId = await GetResourceKeyIdAsync("StateEducationAgency");
        await SeedSchoolAsync(schoolId: 300);
        var stateEducationAgencyDocumentId = await InsertDocumentAsync(stateEducationAgencyResourceKeyId);

        // A direct insert isolates the natural key: both payload columns differ from the School row's.
        var act = () =>
            _database.ExecuteNonQueryAsync(
                """
                INSERT INTO "edfi"."EducationOrganizationIdentity"
                    ("DocumentId", "EducationOrganizationId", "ResourceKeyId", "Discriminator")
                VALUES (@documentId, 300, @resourceKeyId, 'Ed-Fi:StateEducationAgency');
                """,
                new NpgsqlParameter("documentId", stateEducationAgencyDocumentId),
                new NpgsqlParameter("resourceKeyId", stateEducationAgencyResourceKeyId)
            );

        var exception = await act.Should().ThrowAsync<PostgresException>();
        exception.Which.SqlState.Should().Be(PostgresErrorCodes.UniqueViolation);
        exception.Which.ConstraintName.Should().Be(NaturalKeyConstraint);
        (await CountRowsAsync("edfi", "EducationOrganizationIdentity", stateEducationAgencyDocumentId))
            .Should()
            .Be(0);
    }

    [Test]
    public async Task It_rejects_a_member_row_whose_document_has_another_resource_key()
    {
        var stateEducationAgencyResourceKeyId = await GetResourceKeyIdAsync("StateEducationAgency");
        var documentId = await InsertDocumentAsync(stateEducationAgencyResourceKeyId);

        var act = () => InsertSchoolAsync(documentId, schoolId: 100);

        var exception = await act.Should().ThrowAsync<PostgresException>();
        exception.Which.SqlState.Should().Be(PostgresErrorCodes.ForeignKeyViolation);
        exception.Which.ConstraintName.Should().Be(DocumentResourceKeyForeignKey);
        (await CountRowsAsync("edfi", "School", documentId)).Should().Be(0);
        (await CountRowsAsync("edfi", "EducationOrganizationIdentity", documentId)).Should().Be(0);
    }

    [Test]
    public async Task It_rejects_rewriting_the_abstract_identity_resource_key()
    {
        var schoolResourceKeyId = await GetResourceKeyIdAsync("School");
        var stateEducationAgencyResourceKeyId = await GetResourceKeyIdAsync("StateEducationAgency");
        var schoolDocumentId = await SeedSchoolAsync(schoolId: 100);

        var act = () =>
            _database.ExecuteNonQueryAsync(
                """
                UPDATE "edfi"."EducationOrganizationIdentity"
                SET "ResourceKeyId" = @resourceKeyId
                WHERE "DocumentId" = @documentId;
                """,
                new NpgsqlParameter("resourceKeyId", stateEducationAgencyResourceKeyId),
                new NpgsqlParameter("documentId", schoolDocumentId)
            );

        var exception = await act.Should().ThrowAsync<PostgresException>();
        exception.Which.SqlState.Should().Be(PostgresErrorCodes.ForeignKeyViolation);
        exception.Which.ConstraintName.Should().Be(DocumentResourceKeyForeignKey);
        (await ReadIdentityRowAsync(schoolDocumentId)).ResourceKeyId.Should().Be(schoolResourceKeyId);
    }

    [Test]
    public async Task It_rejects_changing_the_document_resource_key_under_an_abstract_identity_row()
    {
        var schoolResourceKeyId = await GetResourceKeyIdAsync("School");
        var stateEducationAgencyResourceKeyId = await GetResourceKeyIdAsync("StateEducationAgency");
        var schoolDocumentId = await SeedSchoolAsync(schoolId: 100);

        var act = () =>
            _database.ExecuteNonQueryAsync(
                """
                UPDATE "dms"."Document"
                SET "ResourceKeyId" = @resourceKeyId
                WHERE "DocumentId" = @documentId;
                """,
                new NpgsqlParameter("resourceKeyId", stateEducationAgencyResourceKeyId),
                new NpgsqlParameter("documentId", schoolDocumentId)
            );

        // ON UPDATE NO ACTION: the referenced (DocumentId, ResourceKeyId) cannot move under the row.
        var exception = await act.Should().ThrowAsync<PostgresException>();
        exception.Which.SqlState.Should().Be(PostgresErrorCodes.ForeignKeyViolation);
        exception.Which.ConstraintName.Should().Be(DocumentResourceKeyForeignKey);
        (await ReadDocumentResourceKeyIdAsync(schoolDocumentId)).Should().Be(schoolResourceKeyId);
        (await ReadIdentityRowAsync(schoolDocumentId)).ResourceKeyId.Should().Be(schoolResourceKeyId);
    }

    [Test]
    public async Task It_copies_identity_strings_byte_for_byte()
    {
        var association = await SeedStudentProgramAssociationAsync();

        var row = await ReadProgramAssociationIdentityRowAsync(association.DocumentId);

        row.ProgramName.Should().Be("Art Club");
        row.StudentUniqueId.Should().Be("AbC-1");
        row.ResourceKeyId.Should().Be(association.ResourceKeyId);
        row.Discriminator.Should().Be("Ed-Fi:StudentProgramAssociation");
    }

    [Test]
    public async Task It_follows_a_case_only_program_rename_through_the_cascade()
    {
        var association = await SeedStudentProgramAssociationAsync();

        // FK_StudentProgramAssociation_ProgramProgram_RefKey is ON UPDATE CASCADE on PostgreSQL, and
        // PostgreSQL compares the bytes, so a case-only rename is a real identity change.
        await _database.ExecuteNonQueryAsync(
            """UPDATE "edfi"."Program" SET "ProgramName" = 'ART CLUB' WHERE "DocumentId" = @documentId;""",
            new NpgsqlParameter("documentId", association.ProgramDocumentId)
        );

        var row = await ReadProgramAssociationIdentityRowAsync(association.DocumentId);
        row.ProgramName.Should().Be("ART CLUB");
        row.StudentUniqueId.Should().Be("AbC-1");
        row.ResourceKeyId.Should().Be(association.ResourceKeyId);
    }

    private sealed record EducationOrganizationRow(
        long DocumentId,
        long EducationOrganizationId,
        short ResourceKeyId,
        string Discriminator
    );

    private sealed record ProgramAssociationIdentityRow(
        string ProgramName,
        string StudentUniqueId,
        short ResourceKeyId,
        string Discriminator
    );

    private sealed record SeededProgramAssociation(
        long DocumentId,
        long ProgramDocumentId,
        short ResourceKeyId
    );

    private Task<short> GetResourceKeyIdAsync(string resourceName)
    {
        return _database.ExecuteScalarAsync<short>(
            """
            SELECT "ResourceKeyId"
            FROM "dms"."ResourceKey"
            WHERE "ProjectName" = 'Ed-Fi' AND "ResourceName" = @resourceName;
            """,
            new NpgsqlParameter("resourceName", resourceName)
        );
    }

    private async Task<long> InsertDocumentAsync(short resourceKeyId)
    {
        await _database.ExecuteNonQueryAsync(CompactDescriptorSeedSupport.PostgresqlSeparateDocumentIdsSql);
        return await _database.ExecuteScalarAsync<long>(
            """
            INSERT INTO "dms"."Document" ("DocumentUuid", "ResourceKeyId")
            VALUES (@documentUuid, @resourceKeyId)
            RETURNING "DocumentId";
            """,
            new NpgsqlParameter("documentUuid", Guid.NewGuid()),
            new NpgsqlParameter("resourceKeyId", resourceKeyId)
        );
    }

    private async Task<long> SeedSchoolAsync(long schoolId)
    {
        var documentId = await InsertDocumentAsync(await GetResourceKeyIdAsync("School"));
        await InsertSchoolAsync(documentId, schoolId);
        return documentId;
    }

    private Task InsertSchoolAsync(long documentId, long schoolId)
    {
        return _database.ExecuteNonQueryAsync(
            """
            INSERT INTO "edfi"."School" ("DocumentId", "NameOfInstitution", "SchoolId")
            VALUES (@documentId, 'Test School', @schoolId);
            """,
            new NpgsqlParameter("documentId", documentId),
            new NpgsqlParameter("schoolId", schoolId)
        );
    }

    private async Task<long> SeedStateEducationAgencyAsync(long stateEducationAgencyId)
    {
        var documentId = await InsertDocumentAsync(await GetResourceKeyIdAsync("StateEducationAgency"));
        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO "edfi"."StateEducationAgency" ("DocumentId", "NameOfInstitution", "StateEducationAgencyId")
            VALUES (@documentId, 'Test State Education Agency', @stateEducationAgencyId);
            """,
            new NpgsqlParameter("documentId", documentId),
            new NpgsqlParameter("stateEducationAgencyId", stateEducationAgencyId)
        );
        return documentId;
    }

    private async Task<SeededProgramAssociation> SeedStudentProgramAssociationAsync()
    {
        var descriptorDocumentId = await InsertDocumentAsync(
            await GetResourceKeyIdAsync("ProgramTypeDescriptor")
        );
        var programTypeDescriptorId = await _database.ExecuteScalarAsync<int>(
            """
            INSERT INTO "dms"."Descriptor" ("DocumentId", "ResourceKeyId", "Namespace", "CodeValue", "ShortDescription")
            SELECT d."DocumentId", d."ResourceKeyId", 'uri://ed-fi.org/ProgramTypeDescriptor', 'Other', 'Other'
            FROM "dms"."Document" d
            WHERE d."DocumentId" = @documentId
            RETURNING "DescriptorId";
            """,
            new NpgsqlParameter("documentId", descriptorDocumentId)
        );

        var schoolDocumentId = await SeedSchoolAsync(schoolId: 100);

        var programDocumentId = await InsertDocumentAsync(await GetResourceKeyIdAsync("Program"));
        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO "edfi"."Program" (
                "DocumentId",
                "EducationOrganization_DocumentId",
                "EducationOrganization_EducationOrganizationId",
                "ProgramTypeDescriptor_DescriptorId",
                "ProgramName"
            )
            VALUES (@documentId, @schoolDocumentId, 100, @programTypeDescriptorId, 'Art Club');
            """,
            new NpgsqlParameter("documentId", programDocumentId),
            new NpgsqlParameter("schoolDocumentId", schoolDocumentId),
            new NpgsqlParameter("programTypeDescriptorId", programTypeDescriptorId)
        );

        var studentDocumentId = await InsertDocumentAsync(await GetResourceKeyIdAsync("Student"));
        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO "edfi"."Student" ("DocumentId", "BirthDate", "FirstName", "LastSurname", "StudentUniqueId")
            VALUES (@documentId, @birthDate, 'Ada', 'Lovelace', 'AbC-1');
            """,
            new NpgsqlParameter("documentId", studentDocumentId),
            new NpgsqlParameter("birthDate", new DateOnly(2010, 1, 1))
        );

        var associationResourceKeyId = await GetResourceKeyIdAsync("StudentProgramAssociation");
        var associationDocumentId = await InsertDocumentAsync(associationResourceKeyId);
        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO "edfi"."StudentProgramAssociation" (
                "DocumentId",
                "EducationOrganization_DocumentId",
                "EducationOrganization_EducationOrganizationId",
                "ProgramProgram_DocumentId",
                "ProgramProgram_EducationOrganizationId",
                "ProgramProgram_ProgramName",
                "ProgramProgram_ProgramTypeDescriptor_DescriptorId",
                "Student_DocumentId",
                "Student_StudentUniqueId",
                "BeginDate"
            )
            VALUES (
                @documentId,
                @schoolDocumentId,
                100,
                @programDocumentId,
                100,
                'Art Club',
                @programTypeDescriptorId,
                @studentDocumentId,
                'AbC-1',
                @beginDate
            );
            """,
            new NpgsqlParameter("documentId", associationDocumentId),
            new NpgsqlParameter("schoolDocumentId", schoolDocumentId),
            new NpgsqlParameter("programDocumentId", programDocumentId),
            new NpgsqlParameter("programTypeDescriptorId", programTypeDescriptorId),
            new NpgsqlParameter("studentDocumentId", studentDocumentId),
            new NpgsqlParameter("beginDate", _beginDate)
        );

        return new(associationDocumentId, programDocumentId, associationResourceKeyId);
    }

    private Task<EducationOrganizationRow> ReadIdentityRowAsync(long documentId)
    {
        return ReadEducationOrganizationRowAsync("EducationOrganizationIdentity", documentId);
    }

    private Task<EducationOrganizationRow> ReadViewRowAsync(long documentId)
    {
        return ReadEducationOrganizationRowAsync("EducationOrganization_View", documentId);
    }

    private async Task<EducationOrganizationRow> ReadEducationOrganizationRowAsync(
        string relation,
        long documentId
    )
    {
        var rows = await _database.QueryRowsAsync(
            $"""
            SELECT "DocumentId", "EducationOrganizationId", "ResourceKeyId", "Discriminator"
            FROM "edfi"."{relation}"
            WHERE "DocumentId" = @documentId;
            """,
            new NpgsqlParameter("documentId", documentId)
        );

        var row = rows.Should().ContainSingle().Subject;
        return new(
            (long)row["DocumentId"]!,
            (long)row["EducationOrganizationId"]!,
            (short)row["ResourceKeyId"]!,
            (string)row["Discriminator"]!
        );
    }

    private async Task<ProgramAssociationIdentityRow> ReadProgramAssociationIdentityRowAsync(long documentId)
    {
        var rows = await _database.QueryRowsAsync(
            """
            SELECT "Program_ProgramName", "Student_StudentUniqueId", "ResourceKeyId", "Discriminator"
            FROM "edfi"."GeneralStudentProgramAssociationIdentity"
            WHERE "DocumentId" = @documentId;
            """,
            new NpgsqlParameter("documentId", documentId)
        );

        var row = rows.Should().ContainSingle().Subject;
        return new(
            (string)row["Program_ProgramName"]!,
            (string)row["Student_StudentUniqueId"]!,
            (short)row["ResourceKeyId"]!,
            (string)row["Discriminator"]!
        );
    }

    private Task<short> ReadDocumentResourceKeyIdAsync(long documentId)
    {
        return _database.ExecuteScalarAsync<short>(
            """SELECT "ResourceKeyId" FROM "dms"."Document" WHERE "DocumentId" = @documentId;""",
            new NpgsqlParameter("documentId", documentId)
        );
    }

    private Task<long> CountRowsAsync(string schema, string relation, long documentId)
    {
        return _database.ExecuteScalarAsync<long>(
            $"""SELECT count(*) FROM "{schema}"."{relation}" WHERE "DocumentId" = @documentId;""",
            new NpgsqlParameter("documentId", documentId)
        );
    }

    private static async Task ExecuteInTransactionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        long documentId
    )
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("documentId", documentId);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> CountIdentityRowsInTransactionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long documentId
    )
    {
        await using var command = new NpgsqlCommand(
            """SELECT count(*) FROM "edfi"."EducationOrganizationIdentity" WHERE "DocumentId" = @documentId;""",
            connection,
            transaction
        );
        command.Parameters.AddWithValue("documentId", documentId);
        return (long)(await command.ExecuteScalarAsync())!;
    }
}
