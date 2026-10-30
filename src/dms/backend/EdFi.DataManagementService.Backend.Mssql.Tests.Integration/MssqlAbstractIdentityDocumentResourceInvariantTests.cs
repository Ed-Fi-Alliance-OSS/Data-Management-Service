// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Tests.Common;
using EdFi.DataManagementService.Backend.Tests.Integration.Common;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Mssql.Tests.Integration;

/// <summary>
/// SQL Server counterpart to the PostgreSQL
/// <c>Given_A_Provisioned_Postgresql_Database_Abstract_Identity_Document_Resource_Invariant</c> fixture.
/// Every abstract identity row carries the concrete member's <c>ResourceKeyId</c>, written by the
/// member's maintenance trigger from a compile-time literal, and
/// <c>FK_&lt;Abstract&gt;Identity_DocumentResourceKey</c> pins it to the owning <c>dms.Document</c> row.
/// <c>ResourceKeyId</c> and <c>Discriminator</c> are payload only and never part of the natural key.
/// Seeds use raw SQL with every trigger enabled, so the generated triggers do the maintenance.
/// </summary>
[TestFixture]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
[Category(MssqlCiShards.Shard1)]
public class Given_A_Provisioned_Mssql_Database_Abstract_Identity_Document_Resource_Invariant
{
    private const string FixtureRelativePath = "src/dms/backend/Fixtures/authoritative/ds-5.2";
    private const int ForeignKeyViolationErrorNumber = 547;
    private const int UniqueConstraintViolationErrorNumber = 2627;
    private const string DocumentResourceKeyForeignKey =
        "FK_EducationOrganizationIdentity_DocumentResourceKey";
    private const string NaturalKeyConstraint = "UX_EducationOrganizationIdentity_NK";

    private static readonly DateOnly _beginDate = new(2026, 8, 15);

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
            "UPDATE [edfi].[School] SET [SchoolId] = 101 WHERE [DocumentId] = @documentId;",
            new SqlParameter("@documentId", schoolDocumentId)
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
        int identityRowsAfterRootDelete;
        int identityRowsAfterDocumentDelete;

        // OrderedDeleteCommandBuilder: root row first, then the dms.Document row, one transaction. The
        // root delete leaves the identity row in place; the composite FK's cascade removes it.
        await using var connection = new SqlConnection(_database.ConnectionString);
        await connection.OpenAsync();
        await using (var transaction = (SqlTransaction)await connection.BeginTransactionAsync())
        {
            await ExecuteInTransactionAsync(
                connection,
                transaction,
                "DELETE FROM [edfi].[School] WHERE [DocumentId] = @documentId;",
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
                "DELETE FROM [dms].[Document] WHERE [DocumentId] = @documentId;",
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
        // Going through a second member root instead would let SQL Server's unordered AFTER triggers fail
        // on another key first.
        var act = () =>
            _database.ExecuteNonQueryAsync(
                """
                INSERT INTO [edfi].[EducationOrganizationIdentity]
                    ([DocumentId], [EducationOrganizationId], [ResourceKeyId], [Discriminator])
                VALUES (@documentId, 300, @resourceKeyId, N'Ed-Fi:StateEducationAgency');
                """,
                new SqlParameter("@documentId", stateEducationAgencyDocumentId),
                new SqlParameter("@resourceKeyId", stateEducationAgencyResourceKeyId)
            );

        var exception = await act.Should().ThrowAsync<SqlException>();
        exception.Which.Number.Should().Be(UniqueConstraintViolationErrorNumber);
        exception.Which.Message.Should().Contain(NaturalKeyConstraint);
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

        var exception = await act.Should().ThrowAsync<SqlException>();
        exception.Which.Number.Should().Be(ForeignKeyViolationErrorNumber);
        exception.Which.Message.Should().Contain(DocumentResourceKeyForeignKey);
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
                UPDATE [edfi].[EducationOrganizationIdentity]
                SET [ResourceKeyId] = @resourceKeyId
                WHERE [DocumentId] = @documentId;
                """,
                new SqlParameter("@resourceKeyId", stateEducationAgencyResourceKeyId),
                new SqlParameter("@documentId", schoolDocumentId)
            );

        var exception = await act.Should().ThrowAsync<SqlException>();
        exception.Which.Number.Should().Be(ForeignKeyViolationErrorNumber);
        exception.Which.Message.Should().Contain(DocumentResourceKeyForeignKey);
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
                UPDATE [dms].[Document]
                SET [ResourceKeyId] = @resourceKeyId
                WHERE [DocumentId] = @documentId;
                """,
                new SqlParameter("@resourceKeyId", stateEducationAgencyResourceKeyId),
                new SqlParameter("@documentId", schoolDocumentId)
            );

        // ON UPDATE NO ACTION: the referenced (DocumentId, ResourceKeyId) cannot move under the row.
        var exception = await act.Should().ThrowAsync<SqlException>();
        exception.Which.Number.Should().Be(ForeignKeyViolationErrorNumber);
        exception.Which.Message.Should().Contain(DocumentResourceKeyForeignKey);
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
    public async Task It_follows_a_case_only_identity_change_under_the_identity_collation()
    {
        var association = await SeedStudentProgramAssociationAsync();

        // FK_StudentProgramAssociation_ProgramProgram_RefKey is pruned to ON UPDATE NO ACTION on SQL
        // Server, so the member's copy changes directly. The case-insensitive identity collation lets
        // the FK accept it, and the trigger's binary diff still propagates the new bytes.
        await _database.ExecuteNonQueryAsync(
            """
            UPDATE [edfi].[StudentProgramAssociation]
            SET [ProgramProgram_ProgramName] = N'ART CLUB'
            WHERE [DocumentId] = @documentId;
            """,
            new SqlParameter("@documentId", association.DocumentId)
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

    private sealed record SeededProgramAssociation(long DocumentId, short ResourceKeyId);

    private Task<short> GetResourceKeyIdAsync(string resourceName)
    {
        return _database.ExecuteScalarAsync<short>(
            """
            SELECT [ResourceKeyId]
            FROM [dms].[ResourceKey]
            WHERE [ProjectName] = N'Ed-Fi' AND [ResourceName] = @resourceName;
            """,
            new SqlParameter("@resourceName", resourceName)
        );
    }

    private async Task<long> InsertDocumentAsync(short resourceKeyId)
    {
        await _database.ExecuteNonQueryAsync(CompactDescriptorSeedSupport.MssqlSeparateDocumentIdsSql);
        return await _database.ExecuteScalarAsync<long>(
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
            INSERT INTO [edfi].[School] ([DocumentId], [NameOfInstitution], [SchoolId])
            VALUES (@documentId, N'Test School', @schoolId);
            """,
            new SqlParameter("@documentId", documentId),
            new SqlParameter("@schoolId", schoolId)
        );
    }

    private async Task<long> SeedStateEducationAgencyAsync(long stateEducationAgencyId)
    {
        var documentId = await InsertDocumentAsync(await GetResourceKeyIdAsync("StateEducationAgency"));
        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO [edfi].[StateEducationAgency] ([DocumentId], [NameOfInstitution], [StateEducationAgencyId])
            VALUES (@documentId, N'Test State Education Agency', @stateEducationAgencyId);
            """,
            new SqlParameter("@documentId", documentId),
            new SqlParameter("@stateEducationAgencyId", stateEducationAgencyId)
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
            DECLARE @Inserted TABLE ([DescriptorId] int);
            INSERT INTO [dms].[Descriptor] ([DocumentId], [ResourceKeyId], [Namespace], [CodeValue], [ShortDescription])
            OUTPUT INSERTED.[DescriptorId] INTO @Inserted ([DescriptorId])
            SELECT d.[DocumentId], d.[ResourceKeyId], N'uri://ed-fi.org/ProgramTypeDescriptor', N'Other', N'Other'
            FROM [dms].[Document] d
            WHERE d.[DocumentId] = @documentId;
            SELECT TOP (1) [DescriptorId] FROM @Inserted;
            """,
            new SqlParameter("@documentId", descriptorDocumentId)
        );

        var schoolDocumentId = await SeedSchoolAsync(schoolId: 100);

        var programDocumentId = await InsertDocumentAsync(await GetResourceKeyIdAsync("Program"));
        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO [edfi].[Program] (
                [DocumentId],
                [EducationOrganization_DocumentId],
                [EducationOrganization_EducationOrganizationId],
                [ProgramTypeDescriptor_DescriptorId],
                [ProgramName]
            )
            VALUES (@documentId, @schoolDocumentId, 100, @programTypeDescriptorId, N'Art Club');
            """,
            new SqlParameter("@documentId", programDocumentId),
            new SqlParameter("@schoolDocumentId", schoolDocumentId),
            new SqlParameter("@programTypeDescriptorId", programTypeDescriptorId)
        );

        var studentDocumentId = await InsertDocumentAsync(await GetResourceKeyIdAsync("Student"));
        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO [edfi].[Student] ([DocumentId], [BirthDate], [FirstName], [LastSurname], [StudentUniqueId])
            VALUES (@documentId, @birthDate, N'Ada', N'Lovelace', N'AbC-1');
            """,
            new SqlParameter("@documentId", studentDocumentId),
            new SqlParameter("@birthDate", new DateOnly(2010, 1, 1))
        );

        var associationResourceKeyId = await GetResourceKeyIdAsync("StudentProgramAssociation");
        var associationDocumentId = await InsertDocumentAsync(associationResourceKeyId);
        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO [edfi].[StudentProgramAssociation] (
                [DocumentId],
                [EducationOrganization_DocumentId],
                [EducationOrganization_EducationOrganizationId],
                [ProgramProgram_DocumentId],
                [ProgramProgram_EducationOrganizationId],
                [ProgramProgram_ProgramName],
                [ProgramProgram_ProgramTypeDescriptor_DescriptorId],
                [Student_DocumentId],
                [Student_StudentUniqueId],
                [BeginDate]
            )
            VALUES (
                @documentId,
                @schoolDocumentId,
                100,
                @programDocumentId,
                100,
                N'Art Club',
                @programTypeDescriptorId,
                @studentDocumentId,
                N'AbC-1',
                @beginDate
            );
            """,
            new SqlParameter("@documentId", associationDocumentId),
            new SqlParameter("@schoolDocumentId", schoolDocumentId),
            new SqlParameter("@programDocumentId", programDocumentId),
            new SqlParameter("@programTypeDescriptorId", programTypeDescriptorId),
            new SqlParameter("@studentDocumentId", studentDocumentId),
            new SqlParameter("@beginDate", _beginDate)
        );

        return new(associationDocumentId, associationResourceKeyId);
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
            SELECT [DocumentId], [EducationOrganizationId], [ResourceKeyId], [Discriminator]
            FROM [edfi].[{relation}]
            WHERE [DocumentId] = @documentId;
            """,
            new SqlParameter("@documentId", documentId)
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
            SELECT [Program_ProgramName], [Student_StudentUniqueId], [ResourceKeyId], [Discriminator]
            FROM [edfi].[GeneralStudentProgramAssociationIdentity]
            WHERE [DocumentId] = @documentId;
            """,
            new SqlParameter("@documentId", documentId)
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
            "SELECT [ResourceKeyId] FROM [dms].[Document] WHERE [DocumentId] = @documentId;",
            new SqlParameter("@documentId", documentId)
        );
    }

    private Task<int> CountRowsAsync(string schema, string relation, long documentId)
    {
        return _database.ExecuteScalarAsync<int>(
            $"SELECT COUNT(*) FROM [{schema}].[{relation}] WHERE [DocumentId] = @documentId;",
            new SqlParameter("@documentId", documentId)
        );
    }

    private static async Task ExecuteInTransactionAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string sql,
        long documentId
    )
    {
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add(new SqlParameter("@documentId", documentId));
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<int> CountIdentityRowsInTransactionAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        long documentId
    )
    {
        await using var command = new SqlCommand(
            "SELECT COUNT(*) FROM [edfi].[EducationOrganizationIdentity] WHERE [DocumentId] = @documentId;",
            connection,
            transaction
        );
        command.Parameters.Add(new SqlParameter("@documentId", documentId));
        return (int)(await command.ExecuteScalarAsync())!;
    }
}
