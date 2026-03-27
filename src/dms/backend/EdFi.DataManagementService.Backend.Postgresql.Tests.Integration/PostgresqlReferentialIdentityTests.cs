// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using FluentAssertions;
using Npgsql;
using NUnit.Framework;
using static EdFi.DataManagementService.Backend.Tests.Common.ReferentialIdTestHelper;

namespace EdFi.DataManagementService.Backend.Postgresql.Tests.Integration;

[TestFixture]
[NonParallelizable]
public class PostgresqlReferentialIdentityTests
{
    private const string FixtureRelativePath =
        "src/dms/backend/EdFi.DataManagementService.Backend.Ddl.Tests.Unit/Fixtures/small/referential-identity";

    private PostgresqlGeneratedDdlFixture _fixture = null!;
    private PostgresqlGeneratedDdlTestDatabase _database = null!;

    [SetUp]
    public async Task Setup()
    {
        _fixture = PostgresqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(FixtureRelativePath);
        _database = await PostgresqlGeneratedDdlTestDatabase.CreateProvisionedAsync(_fixture.GeneratedDdl);
    }

    [TearDown]
    public async Task TearDown()
    {
        if (_database is not null)
        {
            await _database.DisposeAsync();
        }
    }

    [Test]
    public async Task Insert_concrete_creates_one_referential_identity_row()
    {
        // Arrange
        var documentId = await InsertDocumentAsync(Guid.NewGuid(), "Ed-Fi", "Student");

        // Act
        await InsertStudentAsync(documentId, "STU001");

        // Assert
        var referentialIds = await QueryReferentialIdentityRowsAsync();
        referentialIds.Should().HaveCount(1);
        var referentialId = referentialIds.Single();

        var expectedReferentialId = ComputeReferentialId(
            "Ed-Fi",
            "Student",
            ("$.studentUniqueId", "STU001")
        );
        var studentResourceKeyId = await GetResourceKeyIdAsync("Ed-Fi", "Student");

        ((long)referentialId["DocumentId"]!).Should().Be(documentId);
        ((Guid)referentialId["ReferentialId"]!).Should().Be(expectedReferentialId);
        ((short)referentialId["ResourceKeyId"]!).Should().Be(studentResourceKeyId);
    }

    [Test]
    public async Task Insert_subclass_creates_primary_and_alias_referential_identity_rows()
    {
        // Arrange
        var documentId = await InsertDocumentAsync(Guid.NewGuid(), "Ed-Fi", "School");

        // Act
        await InsertSchoolAsync(documentId, 100);

        // Assert
        var referentialIds = await QueryReferentialIdentityRowsAsync();
        referentialIds.Should().HaveCount(2);

        referentialIds
            .All(r => (long)r["DocumentId"]! == documentId)
            .Should().BeTrue();

        var edOrgResourceKeyId = await GetResourceKeyIdAsync("Ed-Fi", "EducationOrganization");
        var schoolResourceKeyId = await GetResourceKeyIdAsync("Ed-Fi", "School");

        var aliasReferentialId = referentialIds
            .Single(r => (short)r["ResourceKeyId"]! == edOrgResourceKeyId);

        var concreteReferentialId = referentialIds.
            Single(r => (short)r["ResourceKeyId"]! == schoolResourceKeyId);

        var expectedConcreteReferentialId = ComputeReferentialId(
            "Ed-Fi",
            "School",
            ("$.schoolId", "100")
        );
        var expectedAliasReferentialId = ComputeReferentialId(
            "Ed-Fi",
            "EducationOrganization",
            ("$.educationOrganizationId", "100")
        );

        ((Guid)concreteReferentialId["ReferentialId"]!).Should().Be(expectedConcreteReferentialId);
        ((Guid)aliasReferentialId["ReferentialId"]!).Should().Be(expectedAliasReferentialId);
        ((long)concreteReferentialId["DocumentId"]!).Should().Be(documentId);
        ((long)aliasReferentialId["DocumentId"]!).Should().Be(documentId);
    }

    [Test]
    public async Task Update_concrete_replaces_referential_identity()
    {
        // Arrange
        var documentId = await InsertDocumentAsync(Guid.NewGuid(), "Ed-Fi", "Student");
        await InsertStudentAsync(documentId, "STU-OLD");

        var expectedOldReferentialId = ComputeReferentialId("Ed-Fi", "Student", ("$.studentUniqueId", "STU-OLD"));
        var oldReferentialIds = await QueryReferentialIdentityRowsAsync();
        oldReferentialIds.Should().HaveCount(1);
        var oldReferentialId = oldReferentialIds.Single();

        ((Guid)oldReferentialId["ReferentialId"]!).Should().Be(expectedOldReferentialId);
        ((long)oldReferentialId["DocumentId"]!).Should().Be(documentId);

        // Act
        await _database.ExecuteNonQueryAsync(
            """
            UPDATE "edfi"."Student"
            SET "StudentUniqueId" = @newId
            WHERE "DocumentId" = @documentId;
            """,
            new NpgsqlParameter("newId", "STU-NEW"),
            new NpgsqlParameter("documentId", documentId)
        );

        // Assert
        var newExpectedReferentialId = ComputeReferentialId("Ed-Fi", "Student", ("$.studentUniqueId", "STU-NEW"));
        var newReferentialIds = await QueryReferentialIdentityRowsAsync();
        newReferentialIds.Should().HaveCount(1);

        var newReferentialId = newReferentialIds.Single();
        ((Guid)newReferentialId["ReferentialId"]!).Should().Be(newExpectedReferentialId);
        ((long)newReferentialId["DocumentId"]!).Should().Be(documentId);
    }

    [Test]
    public async Task Update_subclass_replaces_both_primary_and_alias_rows()
    {
        // Arrange
        var documentId = await InsertDocumentAsync(Guid.NewGuid(), "Ed-Fi", "School");
        await InsertSchoolAsync(documentId, 600);

        var oldExpectedConcreteReferentialId = ComputeReferentialId("Ed-Fi", "School", ("$.schoolId", "600"));
        var oldExpectedAliasReferentialId = ComputeReferentialId(
            "Ed-Fi",
            "EducationOrganization",
            ("$.educationOrganizationId", "600")
        );
        var oldReferentialIds = await QueryReferentialIdentityRowsAsync();
        oldReferentialIds.Should().HaveCount(2);

        oldReferentialIds.Should().Contain(r => (Guid)r["ReferentialId"]! == oldExpectedConcreteReferentialId && (long)r["DocumentId"]! == documentId);
        oldReferentialIds.Should().Contain(r => (Guid)r["ReferentialId"]! == oldExpectedAliasReferentialId && (long)r["DocumentId"]! == documentId);

        // Act — update SchoolId (and EducationOrganizationId for consistency)
        await _database.ExecuteNonQueryAsync(
            """
            UPDATE "edfi"."School"
            SET "SchoolId" = @newSchoolId, "EducationOrganizationId" = @newEdOrgId
            WHERE "DocumentId" = @documentId;
            """,
            new NpgsqlParameter("newSchoolId", 700),
            new NpgsqlParameter("newEdOrgId", 700),
            new NpgsqlParameter("documentId", documentId)
        );

        // Assert
        var newExpectedConcreteReferentialId = ComputeReferentialId("Ed-Fi", "School", ("$.schoolId", "700"));
        var newExpectedAliasReferentialId = ComputeReferentialId(
            "Ed-Fi",
            "EducationOrganization",
            ("$.educationOrganizationId", "700")
        );
        var newReferentialIds = await QueryReferentialIdentityRowsAsync();
        newReferentialIds.Should().HaveCount(2);

        newReferentialIds.Should().Contain(r => (Guid)r["ReferentialId"]! == newExpectedConcreteReferentialId && (long)r["DocumentId"]! == documentId);
        newReferentialIds.Should().Contain(r => (Guid)r["ReferentialId"]! == newExpectedAliasReferentialId && (long)r["DocumentId"]! == documentId);
    }

    [Test]
    public async Task Noop_update_does_not_change_referential_identity()
    {
        // Arrange
        var documentId = await InsertDocumentAsync(Guid.NewGuid(), "Ed-Fi", "Student");
        await InsertStudentAsync(documentId, "STU-NOOP", "Original");

        var expectedReferentialId = ComputeReferentialId(
            "Ed-Fi",
            "Student",
            ("$.studentUniqueId", "STU-NOOP")
        );
        var referentialIds = await QueryReferentialIdentityRowsAsync();
        referentialIds.Should().HaveCount(1);

        var referentialId = referentialIds.Single();
        ((Guid)referentialId["ReferentialId"]!).Should().Be(expectedReferentialId);
        ((long)referentialId["DocumentId"]!).Should().Be(documentId);

        // Act — update non-identity column only
        await _database.ExecuteNonQueryAsync(
            """
            UPDATE "edfi"."Student"
            SET "FirstName" = @newName
            WHERE "DocumentId" = @documentId;
            """,
            new NpgsqlParameter("newName", "Updated"),
            new NpgsqlParameter("documentId", documentId)
        );

        // Assert — RI row unchanged
        referentialIds = await QueryReferentialIdentityRowsAsync();
        referentialIds.Should().HaveCount(1);

        referentialId = referentialIds.Single();
        ((Guid)referentialId["ReferentialId"]!).Should().Be(expectedReferentialId);
        ((long)referentialId["DocumentId"]!).Should().Be(documentId);
    }

    [Test]
    public async Task Cascaded_recompute_updates_dependent_referential_identity()
    {
        // Arrange — insert School(900) and SSA referencing it
        var schoolDocumentId = await InsertDocumentAsync(Guid.NewGuid(), "Ed-Fi", "School");
        await InsertSchoolAsync(schoolDocumentId, 900);

        var ssaDocumentId = await InsertDocumentAsync(Guid.NewGuid(), "Ed-Fi", "StudentSchoolAssociation");
        await InsertStudentSchoolAssociationAsync(ssaDocumentId, "STU-CASCADE", schoolDocumentId, 900);

        var oldExpectedSsaReferentialId = ComputeReferentialId(
            "Ed-Fi",
            "StudentSchoolAssociation",
            ("$.studentUniqueId", "STU-CASCADE"),
            ("$.schoolReference.schoolId", "900")
        );
        var referentialIds = await QueryReferentialIdentityRowsAsync();
        referentialIds.Should().HaveCount(3);
        referentialIds.Should().Contain(r => (Guid)r["ReferentialId"]! == oldExpectedSsaReferentialId && (long)r["DocumentId"]! == ssaDocumentId);

        // Act — update School's SchoolId; cascade propagates to SSA's SchoolReference_SchoolId
        await _database.ExecuteNonQueryAsync(
            """
            UPDATE "edfi"."School"
            SET "SchoolId" = @newSchoolId, "EducationOrganizationId" = @newEdOrgId
            WHERE "DocumentId" = @documentId;
            """,
            new NpgsqlParameter("newSchoolId", 901),
            new NpgsqlParameter("newEdOrgId", 901),
            new NpgsqlParameter("documentId", schoolDocumentId)
        );

        // Assert — SSA's old RefId gone, new RefId present
        referentialIds = await QueryReferentialIdentityRowsAsync();
        var newExpectedSsaReferentialId = ComputeReferentialId(
            "Ed-Fi",
            "StudentSchoolAssociation",
            ("$.studentUniqueId", "STU-CASCADE"),
            ("$.schoolReference.schoolId", "901")
        );

        referentialIds.Should().HaveCount(3);
        referentialIds.Should().NotContain(r => (Guid)r["ReferentialId"]! == oldExpectedSsaReferentialId);
        referentialIds.Should().Contain(r => (Guid)r["ReferentialId"]! == newExpectedSsaReferentialId && (long)r["DocumentId"]! == ssaDocumentId);
    }

    [Test]
    public async Task Direct_insert_duplicate_referential_id_is_rejected()
    {
        // Arrange — two documents so (DocumentId, ResourceKeyId) pairs differ
        var documentIdA = await InsertDocumentAsync(Guid.NewGuid(), "Ed-Fi", "Student");
        var documentIdB = await InsertDocumentAsync(Guid.NewGuid(), "Ed-Fi", "Student");
        var studentResourceKeyId = await GetResourceKeyIdAsync("Ed-Fi", "Student");
        var duplicateReferentialId = Guid.NewGuid();

        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO "dms"."ReferentialIdentity" ("ReferentialId", "DocumentId", "ResourceKeyId")
            VALUES (@referentialId, @documentId, @resourceKeyId);
            """,
            new NpgsqlParameter("referentialId", duplicateReferentialId),
            new NpgsqlParameter("documentId", documentIdA),
            new NpgsqlParameter("resourceKeyId", studentResourceKeyId)
        );

        // Act & Assert — same ReferentialId violates PK
        var act = () => _database.ExecuteNonQueryAsync(
            """
            INSERT INTO "dms"."ReferentialIdentity" ("ReferentialId", "DocumentId", "ResourceKeyId")
            VALUES (@referentialId, @documentId, @resourceKeyId);
            """,
            new NpgsqlParameter("referentialId", duplicateReferentialId),
            new NpgsqlParameter("documentId", documentIdB),
            new NpgsqlParameter("resourceKeyId", studentResourceKeyId)
        );

        var ex = (await act.Should().ThrowAsync<PostgresException>()).Which;
        ex.SqlState.Should().Be(PostgresErrorCodes.UniqueViolation);
        ex.ConstraintName.Should().Be("PK_ReferentialIdentity");
    }

    [Test]
    [Ignore("Re-enable after DMS-1122 has been fixed.")]
    public async Task Direct_insert_duplicate_document_resource_key_is_rejected()
    {
        // Arrange
        var documentId = await InsertDocumentAsync(Guid.NewGuid(), "Ed-Fi", "Student");
        var studentResourceKeyId = await GetResourceKeyIdAsync("Ed-Fi", "Student");

        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO "dms"."ReferentialIdentity" ("ReferentialId", "DocumentId", "ResourceKeyId")
            VALUES (@referentialId, @documentId, @resourceKeyId);
            """,
            new NpgsqlParameter("referentialId", Guid.NewGuid()),
            new NpgsqlParameter("documentId", documentId),
            new NpgsqlParameter("resourceKeyId", studentResourceKeyId)
        );

        // Act & Assert — same (DocumentId, ResourceKeyId) violates unique constraint
        var act = () => _database.ExecuteNonQueryAsync(
            """
            INSERT INTO "dms"."ReferentialIdentity" ("ReferentialId", "DocumentId", "ResourceKeyId")
            VALUES (@referentialId, @documentId, @resourceKeyId);
            """,
            new NpgsqlParameter("referentialId", Guid.NewGuid()),
            new NpgsqlParameter("documentId", documentId),
            new NpgsqlParameter("resourceKeyId", studentResourceKeyId)
        );

        var ex = (await act.Should().ThrowAsync<PostgresException>()).Which;
        ex.SqlState.Should().Be(PostgresErrorCodes.UniqueViolation);
        ex.ConstraintName.Should().Be("UX_ReferentialIdentity_DocumentId_ResourceKeyId");
    }

    [Test]
    public async Task Delete_concrete_removes_referential_identity_row()
    {
        // Arrange
        var documentId = await InsertDocumentAsync(Guid.NewGuid(), "Ed-Fi", "Student");
        await InsertStudentAsync(documentId, "STU-DEL");

        var expectedReferentialId = ComputeReferentialId("Ed-Fi", "Student", ("$.studentUniqueId", "STU-DEL"));
        var referentialIds = await QueryReferentialIdentityRowsAsync();
        referentialIds.Should().HaveCount(1);

        var referentialId = referentialIds.Single();
        ((Guid)referentialId["ReferentialId"]!).Should().Be(expectedReferentialId);
        ((long)referentialId["DocumentId"]!).Should().Be(documentId);

        // Act — delete the dms.Document row (CASCADE removes RI via FK)
        await _database.ExecuteNonQueryAsync(
            """
            DELETE FROM "dms"."Document" WHERE "DocumentId" = @documentId;
            """,
            new NpgsqlParameter("documentId", documentId)
        );

        // Assert
        referentialIds = await QueryReferentialIdentityRowsAsync();
        referentialIds.Should().BeEmpty();
    }

    [Test]
    public async Task Delete_subclass_removes_both_primary_and_alias_rows()
    {
        // Arrange
        var documentId = await InsertDocumentAsync(Guid.NewGuid(), "Ed-Fi", "School");
        await InsertSchoolAsync(documentId, 1000);

        var referentialIds = await QueryReferentialIdentityRowsAsync();
        referentialIds.Should().HaveCount(2);
        referentialIds
            .All(r => (long)r["DocumentId"]! == documentId)
            .Should().BeTrue();

        // Act
        await _database.ExecuteNonQueryAsync(
            """
            DELETE FROM "dms"."Document" WHERE "DocumentId" = @documentId;
            """,
            new NpgsqlParameter("documentId", documentId)
        );

        // Assert
        referentialIds = await QueryReferentialIdentityRowsAsync();
        referentialIds.Should().BeEmpty();
    }

    private async Task<short> GetResourceKeyIdAsync(string projectName, string resourceName)
    {
        return await _database.ExecuteScalarAsync<short>(
            """
            SELECT "ResourceKeyId"
            FROM "dms"."ResourceKey"
            WHERE "ProjectName" = @projectName
              AND "ResourceName" = @resourceName;
            """,
            new NpgsqlParameter("projectName", projectName),
            new NpgsqlParameter("resourceName", resourceName)
        );
    }

    private async Task<long> InsertDocumentAsync(Guid documentUuid, string projectName, string resourceName)
    {
        var resourceKeyId = await GetResourceKeyIdAsync(projectName, resourceName);

        return await _database.ExecuteScalarAsync<long>(
            """
            INSERT INTO "dms"."Document" ("DocumentUuid", "ResourceKeyId")
            VALUES (@documentUuid, @resourceKeyId)
            RETURNING "DocumentId";
            """,
            new NpgsqlParameter("documentUuid", documentUuid),
            new NpgsqlParameter("resourceKeyId", resourceKeyId)
        );
    }

    private async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> QueryReferentialIdentityRowsAsync()
    {
        return await _database.QueryRowsAsync(
            """
            SELECT "ReferentialId", "DocumentId", "ResourceKeyId"
            FROM "dms"."ReferentialIdentity"
            ORDER BY "ResourceKeyId";
            """
        );
    }

    private async Task InsertStudentAsync(long documentId, string studentUniqueId, string firstName = "Test")
    {
        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO "edfi"."Student" ("DocumentId", "StudentUniqueId", "FirstName")
            VALUES (@documentId, @studentUniqueId, @firstName);
            """,
            new NpgsqlParameter("documentId", documentId),
            new NpgsqlParameter("studentUniqueId", studentUniqueId),
            new NpgsqlParameter("firstName", firstName)
        );
    }

    private async Task InsertSchoolAsync(
        long documentId,
        int schoolId,
        string nameOfInstitution = "Test School"
    )
    {
        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO "edfi"."School" ("DocumentId", "EducationOrganizationId", "NameOfInstitution", "SchoolId")
            VALUES (@documentId, @educationOrganizationId, @nameOfInstitution, @schoolId);
            """,
            new NpgsqlParameter("documentId", documentId),
            new NpgsqlParameter("educationOrganizationId", schoolId),
            new NpgsqlParameter("nameOfInstitution", nameOfInstitution),
            new NpgsqlParameter("schoolId", schoolId)
        );
    }

    private async Task InsertStudentSchoolAssociationAsync(
        long documentId,
        string studentUniqueId,
        long schoolDocumentId,
        int schoolId
    )
    {
        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO "edfi"."StudentSchoolAssociation" ("DocumentId", "SchoolReference_DocumentId", "SchoolReference_SchoolId", "StudentUniqueId")
            VALUES (@documentId, @schoolDocumentId, @schoolId, @studentUniqueId);
            """,
            new NpgsqlParameter("documentId", documentId),
            new NpgsqlParameter("schoolDocumentId", schoolDocumentId),
            new NpgsqlParameter("schoolId", schoolId),
            new NpgsqlParameter("studentUniqueId", studentUniqueId)
        );
    }
}
