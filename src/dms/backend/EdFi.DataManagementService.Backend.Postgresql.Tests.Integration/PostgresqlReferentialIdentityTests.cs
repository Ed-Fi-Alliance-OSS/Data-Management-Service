// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using FluentAssertions;
using Npgsql;
using NUnit.Framework;
using static EdFi.DataManagementService.Backend.Tests.Common.ReferentialIdTestHelper;

namespace EdFi.DataManagementService.Backend.Postgresql.Tests.Integration;

[TestFixture]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
public class PostgresqlReferentialIdentityTests
{
    private const string FixtureRelativePath =
        "src/dms/backend/EdFi.DataManagementService.Backend.Ddl.Tests.Unit/Fixtures/small/referential-identity";

    private PostgresqlGeneratedDdlFixture _fixture = null!;
    private PostgresqlGeneratedDdlTestDatabase _database = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _fixture = PostgresqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(FixtureRelativePath);
        _database = await PostgresqlGeneratedDdlTestDatabase.CreateProvisionedAsync(_fixture.GeneratedDdl);
    }

    [SetUp]
    public async Task Setup()
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

        var expectedReferentialId = ComputeReferentialId("Ed-Fi", "Student", ("$.studentUniqueId", "STU001"));
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

        referentialIds.All(r => (long)r["DocumentId"]! == documentId).Should().BeTrue();

        var edOrgResourceKeyId = await GetResourceKeyIdAsync("Ed-Fi", "EducationOrganization");
        var schoolResourceKeyId = await GetResourceKeyIdAsync("Ed-Fi", "School");

        var aliasReferentialId = referentialIds.Single(r => (short)r["ResourceKeyId"]! == edOrgResourceKeyId);

        var concreteReferentialId = referentialIds.Single(r =>
            (short)r["ResourceKeyId"]! == schoolResourceKeyId
        );

        var expectedConcreteReferentialId = ComputeReferentialId("Ed-Fi", "School", ("$.schoolId", "100"));
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

        var expectedOldReferentialId = ComputeReferentialId(
            "Ed-Fi",
            "Student",
            ("$.studentUniqueId", "STU-OLD")
        );
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
        var newExpectedReferentialId = ComputeReferentialId(
            "Ed-Fi",
            "Student",
            ("$.studentUniqueId", "STU-NEW")
        );
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

        oldReferentialIds
            .Should()
            .Contain(r =>
                (Guid)r["ReferentialId"]! == oldExpectedConcreteReferentialId
                && (long)r["DocumentId"]! == documentId
            );
        oldReferentialIds
            .Should()
            .Contain(r =>
                (Guid)r["ReferentialId"]! == oldExpectedAliasReferentialId
                && (long)r["DocumentId"]! == documentId
            );

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

        newReferentialIds
            .Should()
            .Contain(r =>
                (Guid)r["ReferentialId"]! == newExpectedConcreteReferentialId
                && (long)r["DocumentId"]! == documentId
            );
        newReferentialIds
            .Should()
            .Contain(r =>
                (Guid)r["ReferentialId"]! == newExpectedAliasReferentialId
                && (long)r["DocumentId"]! == documentId
            );
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
    [TestCaseSource(nameof(CollationScenarios))]
    public async Task Cascaded_recompute_updates_dependent_referential_identity(
        string oldStudentUniqueId,
        string newStudentUniqueId
    )
    {
        // Arrange — Insert Student
        var studentDocumentId = await InsertDocumentAsync(Guid.NewGuid(), "Ed-Fi", "Student");
        await InsertStudentAsync(studentDocumentId, oldStudentUniqueId);

        // Insert ResourceA referencing Student
        var resourceADocumentId = await InsertDocumentAsync(Guid.NewGuid(), "Ed-Fi", "ResourceA");
        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO "edfi"."ResourceA" ("DocumentId", "ResourceAId", "StudentReference_DocumentId", "StudentReference_StudentUniqueId")
            VALUES (@documentId, @resourceAId, @studentDocumentId, @studentUniqueId);
            """,
            new NpgsqlParameter("documentId", resourceADocumentId),
            new NpgsqlParameter("resourceAId", "resA-1"),
            new NpgsqlParameter("studentDocumentId", studentDocumentId),
            new NpgsqlParameter("studentUniqueId", oldStudentUniqueId)
        );

        // Insert ResourceB referencing Student
        var resourceBDocumentId = await InsertDocumentAsync(Guid.NewGuid(), "Ed-Fi", "ResourceB");
        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO "edfi"."ResourceB" ("DocumentId", "ResourceBId", "StudentReference_DocumentId", "StudentReference_StudentUniqueId")
            VALUES (@documentId, @resourceBId, @studentDocumentId, @studentUniqueId);
            """,
            new NpgsqlParameter("documentId", resourceBDocumentId),
            new NpgsqlParameter("resourceBId", "resB-1"),
            new NpgsqlParameter("studentDocumentId", studentDocumentId),
            new NpgsqlParameter("studentUniqueId", oldStudentUniqueId)
        );

        // Insert KeyUnifiedResource referencing both A and B
        var keyUnifiedDocumentId = await InsertDocumentAsync(Guid.NewGuid(), "Ed-Fi", "KeyUnifiedResource");
        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO "edfi"."KeyUnifiedResource" ("DocumentId", "KeyUnifiedResourceId", "ResourceAReference_DocumentId", "ResourceAReference_ResourceAId", "ResourceBReference_DocumentId", "ResourceBReference_ResourceBId", "StudentUniqueId_Unified")
            VALUES (@documentId, @keyUnifiedResourceId, @resourceADocumentId, @resourceAId, @resourceBDocumentId, @resourceBId, @studentUniqueId);
            """,
            new NpgsqlParameter("documentId", keyUnifiedDocumentId),
            new NpgsqlParameter("keyUnifiedResourceId", "unified-1"),
            new NpgsqlParameter("resourceADocumentId", resourceADocumentId),
            new NpgsqlParameter("resourceAId", "resA-1"),
            new NpgsqlParameter("resourceBDocumentId", resourceBDocumentId),
            new NpgsqlParameter("resourceBId", "resB-1"),
            new NpgsqlParameter("studentUniqueId", oldStudentUniqueId)
        );

        // Pre-assert: 4 RI rows with expected old referential IDs
        var (expectedStudentOld, expectedResAOld, expectedResBOld, expectedUnifiedOld) =
            ComputeExpectedReferentialIds(oldStudentUniqueId, "resA-1", "resB-1", "unified-1");

        var referentialIds = await QueryReferentialIdentityRowsAsync();
        referentialIds.Should().HaveCount(4);
        referentialIds
            .Should()
            .Contain(r =>
                (Guid)r["ReferentialId"]! == expectedStudentOld && (long)r["DocumentId"]! == studentDocumentId
            );
        referentialIds
            .Should()
            .Contain(r =>
                (Guid)r["ReferentialId"]! == expectedResAOld && (long)r["DocumentId"]! == resourceADocumentId
            );
        referentialIds
            .Should()
            .Contain(r =>
                (Guid)r["ReferentialId"]! == expectedResBOld && (long)r["DocumentId"]! == resourceBDocumentId
            );
        referentialIds
            .Should()
            .Contain(r =>
                (Guid)r["ReferentialId"]! == expectedUnifiedOld
                && (long)r["DocumentId"]! == keyUnifiedDocumentId
            );

        // Act — UPDATE Student's identity field
        await _database.ExecuteNonQueryAsync(
            """
            UPDATE "edfi"."Student"
            SET "StudentUniqueId" = @newId
            WHERE "DocumentId" = @documentId;
            """,
            new NpgsqlParameter("newId", newStudentUniqueId),
            new NpgsqlParameter("documentId", studentDocumentId)
        );

        // Assert — old RI IDs gone, new ones present
        var (expectedStudentNew, expectedResANew, expectedResBNew, expectedUnifiedNew) =
            ComputeExpectedReferentialIds(newStudentUniqueId, "resA-1", "resB-1", "unified-1");

        referentialIds = await QueryReferentialIdentityRowsAsync();
        referentialIds.Should().HaveCount(4);

        referentialIds.Should().NotContain(r => (Guid)r["ReferentialId"]! == expectedStudentOld);
        referentialIds.Should().NotContain(r => (Guid)r["ReferentialId"]! == expectedResAOld);
        referentialIds.Should().NotContain(r => (Guid)r["ReferentialId"]! == expectedResBOld);
        referentialIds.Should().NotContain(r => (Guid)r["ReferentialId"]! == expectedUnifiedOld);

        referentialIds
            .Should()
            .Contain(r =>
                (Guid)r["ReferentialId"]! == expectedStudentNew && (long)r["DocumentId"]! == studentDocumentId
            );
        referentialIds
            .Should()
            .Contain(r =>
                (Guid)r["ReferentialId"]! == expectedResANew && (long)r["DocumentId"]! == resourceADocumentId
            );
        referentialIds
            .Should()
            .Contain(r =>
                (Guid)r["ReferentialId"]! == expectedResBNew && (long)r["DocumentId"]! == resourceBDocumentId
            );
        referentialIds
            .Should()
            .Contain(r =>
                (Guid)r["ReferentialId"]! == expectedUnifiedNew
                && (long)r["DocumentId"]! == keyUnifiedDocumentId
            );
    }

    [Test]
    public async Task Cascaded_recompute_via_abstract_reference_updates_dependent_referential_identity()
    {
        // Arrange — Insert School (schoolId=100) → 2 RI rows (School + EducationOrganization alias)
        var schoolDocumentId = await InsertDocumentAsync(Guid.NewGuid(), "Ed-Fi", "School");
        await InsertSchoolAsync(schoolDocumentId, 100);

        // Insert EdOrgDependentResource referencing EducationOrganization (educationOrganizationId=100)
        var edOrgDepDocumentId = await InsertDocumentAsync(Guid.NewGuid(), "Ed-Fi", "EdOrgDependentResource");
        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO "edfi"."EdOrgDependentResource" ("DocumentId", "EdOrgDependentResourceId", "EducationOrganization_DocumentId", "EducationOrganization_EducationOrganizationId")
            VALUES (@documentId, @edOrgDependentResourceId, @schoolDocumentId, @educationOrganizationId);
            """,
            new NpgsqlParameter("documentId", edOrgDepDocumentId),
            new NpgsqlParameter("edOrgDependentResourceId", "dep-1"),
            new NpgsqlParameter("schoolDocumentId", schoolDocumentId),
            new NpgsqlParameter("educationOrganizationId", 100)
        );

        // Insert EdOrgDependentChildResource referencing EdOrgDependentResource (dep-1, educationOrganizationId=100)
        var edOrgDepChildDocumentId = await InsertDocumentAsync(
            Guid.NewGuid(),
            "Ed-Fi",
            "EdOrgDependentChildResource"
        );
        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO "edfi"."EdOrgDependentChildResource" ("DocumentId", "EdOrgDependentChildResourceId", "EdOrgDependentResourceReference_DocumentId", "EdOrgDependentResourceReference_EdOrgDependentResourceId", "EdOrgDependentResourceReference_EducationOrganizationId")
            VALUES (@documentId, @childResourceId, @edOrgDepDocumentId, @edOrgDependentResourceId, @educationOrganizationId);
            """,
            new NpgsqlParameter("documentId", edOrgDepChildDocumentId),
            new NpgsqlParameter("childResourceId", "child-1"),
            new NpgsqlParameter("edOrgDepDocumentId", edOrgDepDocumentId),
            new NpgsqlParameter("edOrgDependentResourceId", "dep-1"),
            new NpgsqlParameter("educationOrganizationId", 100)
        );

        // Pre-assert: 4 RI rows — School, EducationOrganization alias, EdOrgDependentResource, EdOrgDependentChildResource
        var oldSchoolRI = ComputeReferentialId("Ed-Fi", "School", ("$.schoolId", "100"));
        var oldEdOrgRI = ComputeReferentialId(
            "Ed-Fi",
            "EducationOrganization",
            ("$.educationOrganizationId", "100")
        );
        var oldEdOrgDepRI = ComputeReferentialId(
            "Ed-Fi",
            "EdOrgDependentResource",
            ("$.edOrgDependentResourceId", "dep-1"),
            ("$.educationOrganizationReference.educationOrganizationId", "100")
        );
        var oldEdOrgDepChildRI = ComputeReferentialId(
            "Ed-Fi",
            "EdOrgDependentChildResource",
            ("$.edOrgDependentChildResourceId", "child-1"),
            ("$.edOrgDependentResourceReference.edOrgDependentResourceId", "dep-1"),
            ("$.edOrgDependentResourceReference.educationOrganizationId", "100")
        );

        var referentialIds = await QueryReferentialIdentityRowsAsync();
        referentialIds.Should().HaveCount(4);
        referentialIds
            .Should()
            .Contain(r =>
                (Guid)r["ReferentialId"]! == oldSchoolRI && (long)r["DocumentId"]! == schoolDocumentId
            );
        referentialIds
            .Should()
            .Contain(r =>
                (Guid)r["ReferentialId"]! == oldEdOrgRI && (long)r["DocumentId"]! == schoolDocumentId
            );
        referentialIds
            .Should()
            .Contain(r =>
                (Guid)r["ReferentialId"]! == oldEdOrgDepRI && (long)r["DocumentId"]! == edOrgDepDocumentId
            );
        referentialIds
            .Should()
            .Contain(r =>
                (Guid)r["ReferentialId"]! == oldEdOrgDepChildRI
                && (long)r["DocumentId"]! == edOrgDepChildDocumentId
            );

        // Act — Update School identity (100 → 200)
        await _database.ExecuteNonQueryAsync(
            """
            UPDATE "edfi"."School"
            SET "SchoolId" = @newSchoolId, "EducationOrganizationId" = @newEdOrgId
            WHERE "DocumentId" = @documentId;
            """,
            new NpgsqlParameter("newSchoolId", 200),
            new NpgsqlParameter("newEdOrgId", 200),
            new NpgsqlParameter("documentId", schoolDocumentId)
        );

        // Assert — old RI IDs gone, new ones present
        var newSchoolRI = ComputeReferentialId("Ed-Fi", "School", ("$.schoolId", "200"));
        var newEdOrgRI = ComputeReferentialId(
            "Ed-Fi",
            "EducationOrganization",
            ("$.educationOrganizationId", "200")
        );
        var newEdOrgDepRI = ComputeReferentialId(
            "Ed-Fi",
            "EdOrgDependentResource",
            ("$.edOrgDependentResourceId", "dep-1"),
            ("$.educationOrganizationReference.educationOrganizationId", "200")
        );
        var newEdOrgDepChildRI = ComputeReferentialId(
            "Ed-Fi",
            "EdOrgDependentChildResource",
            ("$.edOrgDependentChildResourceId", "child-1"),
            ("$.edOrgDependentResourceReference.edOrgDependentResourceId", "dep-1"),
            ("$.edOrgDependentResourceReference.educationOrganizationId", "200")
        );

        referentialIds = await QueryReferentialIdentityRowsAsync();
        referentialIds.Should().HaveCount(4);

        referentialIds.Should().NotContain(r => (Guid)r["ReferentialId"]! == oldSchoolRI);
        referentialIds.Should().NotContain(r => (Guid)r["ReferentialId"]! == oldEdOrgRI);
        referentialIds.Should().NotContain(r => (Guid)r["ReferentialId"]! == oldEdOrgDepRI);
        referentialIds.Should().NotContain(r => (Guid)r["ReferentialId"]! == oldEdOrgDepChildRI);

        referentialIds
            .Should()
            .Contain(r =>
                (Guid)r["ReferentialId"]! == newSchoolRI && (long)r["DocumentId"]! == schoolDocumentId
            );
        referentialIds
            .Should()
            .Contain(r =>
                (Guid)r["ReferentialId"]! == newEdOrgRI && (long)r["DocumentId"]! == schoolDocumentId
            );
        referentialIds
            .Should()
            .Contain(r =>
                (Guid)r["ReferentialId"]! == newEdOrgDepRI && (long)r["DocumentId"]! == edOrgDepDocumentId
            );
        referentialIds
            .Should()
            .Contain(r =>
                (Guid)r["ReferentialId"]! == newEdOrgDepChildRI
                && (long)r["DocumentId"]! == edOrgDepChildDocumentId
            );
    }

    [Test]
    public async Task Cascaded_identity_update_bumps_stamps_and_is_visible_in_same_transaction()
    {
        // Arrange — Student → ResourceA/B → KeyUnifiedResource chain
        const string oldStudentUniqueId = "STU-XACT-OLD";
        const string newStudentUniqueId = "STU-XACT-NEW";

        var studentDocumentId = await InsertDocumentAsync(Guid.NewGuid(), "Ed-Fi", "Student");
        await InsertStudentAsync(studentDocumentId, oldStudentUniqueId);

        var resourceADocumentId = await InsertDocumentAsync(Guid.NewGuid(), "Ed-Fi", "ResourceA");
        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO "edfi"."ResourceA" ("DocumentId", "ResourceAId", "StudentReference_DocumentId", "StudentReference_StudentUniqueId")
            VALUES (@documentId, @resourceAId, @studentDocumentId, @studentUniqueId);
            """,
            new NpgsqlParameter("documentId", resourceADocumentId),
            new NpgsqlParameter("resourceAId", "resA-1"),
            new NpgsqlParameter("studentDocumentId", studentDocumentId),
            new NpgsqlParameter("studentUniqueId", oldStudentUniqueId)
        );

        var resourceBDocumentId = await InsertDocumentAsync(Guid.NewGuid(), "Ed-Fi", "ResourceB");
        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO "edfi"."ResourceB" ("DocumentId", "ResourceBId", "StudentReference_DocumentId", "StudentReference_StudentUniqueId")
            VALUES (@documentId, @resourceBId, @studentDocumentId, @studentUniqueId);
            """,
            new NpgsqlParameter("documentId", resourceBDocumentId),
            new NpgsqlParameter("resourceBId", "resB-1"),
            new NpgsqlParameter("studentDocumentId", studentDocumentId),
            new NpgsqlParameter("studentUniqueId", oldStudentUniqueId)
        );

        var keyUnifiedDocumentId = await InsertDocumentAsync(Guid.NewGuid(), "Ed-Fi", "KeyUnifiedResource");
        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO "edfi"."KeyUnifiedResource" ("DocumentId", "KeyUnifiedResourceId", "ResourceAReference_DocumentId", "ResourceAReference_ResourceAId", "ResourceBReference_DocumentId", "ResourceBReference_ResourceBId", "StudentUniqueId_Unified")
            VALUES (@documentId, @keyUnifiedResourceId, @resourceADocumentId, @resourceAId, @resourceBDocumentId, @resourceBId, @studentUniqueId);
            """,
            new NpgsqlParameter("documentId", keyUnifiedDocumentId),
            new NpgsqlParameter("keyUnifiedResourceId", "unified-1"),
            new NpgsqlParameter("resourceADocumentId", resourceADocumentId),
            new NpgsqlParameter("resourceAId", "resA-1"),
            new NpgsqlParameter("resourceBDocumentId", resourceBDocumentId),
            new NpgsqlParameter("resourceBId", "resB-1"),
            new NpgsqlParameter("studentUniqueId", oldStudentUniqueId)
        );

        var (expectedStudentOld, expectedResAOld, expectedResBOld, expectedUnifiedOld) =
            ComputeExpectedReferentialIds(oldStudentUniqueId, "resA-1", "resB-1", "unified-1");
        var (expectedStudentNew, expectedResANew, expectedResBNew, expectedUnifiedNew) =
            ComputeExpectedReferentialIds(newStudentUniqueId, "resA-1", "resB-1", "unified-1");

        // Capture before-snapshots of identity stamps for all four documents
        DocumentStampState beforeStudent;
        DocumentStampState beforeResourceA;
        DocumentStampState beforeResourceB;
        DocumentStampState beforeKeyUnified;
        await using (var snapshotConnection = new NpgsqlConnection(_database.ConnectionString))
        {
            await snapshotConnection.OpenAsync();
            beforeStudent = await GetDocumentStampStateAsync(
                snapshotConnection,
                transaction: null,
                studentDocumentId
            );
            beforeResourceA = await GetDocumentStampStateAsync(
                snapshotConnection,
                transaction: null,
                resourceADocumentId
            );
            beforeResourceB = await GetDocumentStampStateAsync(
                snapshotConnection,
                transaction: null,
                resourceBDocumentId
            );
            beforeKeyUnified = await GetDocumentStampStateAsync(
                snapshotConnection,
                transaction: null,
                keyUnifiedDocumentId
            );
        }

        // PostgreSQL's now() is transaction-start time; advance the wall clock on a
        // separate connection so the next transaction sees a strictly later timestamp.
        await DelayForDistinctTimestampsAsync();

        // Act — open an explicit transaction, UPDATE the parent identity, then
        // observe the cascade through the same connection BEFORE COMMIT.
        await using var connection = new NpgsqlConnection(_database.ConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        await using (var updateCmd = connection.CreateCommand())
        {
            updateCmd.Transaction = transaction;
            updateCmd.CommandText = """
                UPDATE "edfi"."Student"
                SET "StudentUniqueId" = @newId
                WHERE "DocumentId" = @documentId;
                """;
            updateCmd.Parameters.Add(new NpgsqlParameter("newId", newStudentUniqueId));
            updateCmd.Parameters.Add(new NpgsqlParameter("documentId", studentDocumentId));
            await updateCmd.ExecuteNonQueryAsync();
        }

        // Assert (pre-commit) — RI rows recomputed for every document on the chain.
        var inTxnReferentialIds = await QueryReferentialIdentityRowsAsync(connection, transaction);
        inTxnReferentialIds.Should().HaveCount(4);
        inTxnReferentialIds.Should().NotContain(r => (Guid)r["ReferentialId"]! == expectedStudentOld);
        inTxnReferentialIds.Should().NotContain(r => (Guid)r["ReferentialId"]! == expectedResAOld);
        inTxnReferentialIds.Should().NotContain(r => (Guid)r["ReferentialId"]! == expectedResBOld);
        inTxnReferentialIds.Should().NotContain(r => (Guid)r["ReferentialId"]! == expectedUnifiedOld);
        inTxnReferentialIds
            .Should()
            .Contain(r =>
                (Guid)r["ReferentialId"]! == expectedStudentNew && (long)r["DocumentId"]! == studentDocumentId
            );
        inTxnReferentialIds
            .Should()
            .Contain(r =>
                (Guid)r["ReferentialId"]! == expectedResANew && (long)r["DocumentId"]! == resourceADocumentId
            );
        inTxnReferentialIds
            .Should()
            .Contain(r =>
                (Guid)r["ReferentialId"]! == expectedResBNew && (long)r["DocumentId"]! == resourceBDocumentId
            );
        inTxnReferentialIds
            .Should()
            .Contain(r =>
                (Guid)r["ReferentialId"]! == expectedUnifiedNew
                && (long)r["DocumentId"]! == keyUnifiedDocumentId
            );

        // Assert (pre-commit) — IdentityVersion / IdentityLastModifiedAt strictly bumped
        // on the parent and on every cascaded dependent.
        var inTxnStudent = await GetDocumentStampStateAsync(connection, transaction, studentDocumentId);
        var inTxnResourceA = await GetDocumentStampStateAsync(connection, transaction, resourceADocumentId);
        var inTxnResourceB = await GetDocumentStampStateAsync(connection, transaction, resourceBDocumentId);
        var inTxnKeyUnified = await GetDocumentStampStateAsync(connection, transaction, keyUnifiedDocumentId);

        inTxnStudent.IdentityVersion.Should().BeGreaterThan(beforeStudent.IdentityVersion);
        inTxnStudent.IdentityLastModifiedAt.Should().BeAfter(beforeStudent.IdentityLastModifiedAt);
        inTxnResourceA.IdentityVersion.Should().BeGreaterThan(beforeResourceA.IdentityVersion);
        inTxnResourceA.IdentityLastModifiedAt.Should().BeAfter(beforeResourceA.IdentityLastModifiedAt);
        inTxnResourceB.IdentityVersion.Should().BeGreaterThan(beforeResourceB.IdentityVersion);
        inTxnResourceB.IdentityLastModifiedAt.Should().BeAfter(beforeResourceB.IdentityLastModifiedAt);
        inTxnKeyUnified.IdentityVersion.Should().BeGreaterThan(beforeKeyUnified.IdentityVersion);
        inTxnKeyUnified.IdentityLastModifiedAt.Should().BeAfter(beforeKeyUnified.IdentityLastModifiedAt);

        await transaction.CommitAsync();

        // Post-commit — end state on a fresh connection matches the pre-commit observation.
        var postCommitReferentialIds = await QueryReferentialIdentityRowsAsync();
        postCommitReferentialIds.Should().HaveCount(4);
        postCommitReferentialIds
            .Should()
            .Contain(r =>
                (Guid)r["ReferentialId"]! == expectedStudentNew && (long)r["DocumentId"]! == studentDocumentId
            );
        postCommitReferentialIds
            .Should()
            .Contain(r =>
                (Guid)r["ReferentialId"]! == expectedResANew && (long)r["DocumentId"]! == resourceADocumentId
            );
        postCommitReferentialIds
            .Should()
            .Contain(r =>
                (Guid)r["ReferentialId"]! == expectedResBNew && (long)r["DocumentId"]! == resourceBDocumentId
            );
        postCommitReferentialIds
            .Should()
            .Contain(r =>
                (Guid)r["ReferentialId"]! == expectedUnifiedNew
                && (long)r["DocumentId"]! == keyUnifiedDocumentId
            );
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
        var act = () =>
            _database.ExecuteNonQueryAsync(
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
        var act = () =>
            _database.ExecuteNonQueryAsync(
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
    public async Task Update_to_duplicate_natural_key_is_rejected()
    {
        // Arrange — two students with different keys
        var documentIdA = await InsertDocumentAsync(Guid.NewGuid(), "Ed-Fi", "Student");
        await InsertStudentAsync(documentIdA, "STU-A");

        var documentIdB = await InsertDocumentAsync(Guid.NewGuid(), "Ed-Fi", "Student");
        await InsertStudentAsync(documentIdB, "STU-B");

        var expectedReferentialIdA = ComputeReferentialId("Ed-Fi", "Student", ("$.studentUniqueId", "STU-A"));
        var expectedReferentialIdB = ComputeReferentialId("Ed-Fi", "Student", ("$.studentUniqueId", "STU-B"));

        var act = () =>
            _database.ExecuteNonQueryAsync(
                """
                UPDATE "edfi"."Student"
                SET "StudentUniqueId" = @newId
                WHERE "DocumentId" = @documentId;
                """,
                new NpgsqlParameter("newId", "STU-A"),
                new NpgsqlParameter("documentId", documentIdB)
            );

        var ex = (await act.Should().ThrowAsync<PostgresException>()).Which;
        ex.SqlState.Should().Be(PostgresErrorCodes.UniqueViolation);

        // Assert — rollback preserved both original RI rows
        var referentialIds = await QueryReferentialIdentityRowsAsync();
        referentialIds.Should().HaveCount(2);
        referentialIds
            .Should()
            .Contain(r =>
                (Guid)r["ReferentialId"]! == expectedReferentialIdA && (long)r["DocumentId"]! == documentIdA
            );
        referentialIds
            .Should()
            .Contain(r =>
                (Guid)r["ReferentialId"]! == expectedReferentialIdB && (long)r["DocumentId"]! == documentIdB
            );
    }

    [Test]
    public async Task Bulk_insert_with_duplicate_natural_key_is_rejected()
    {
        // Arrange — one existing student
        var documentIdA = await InsertDocumentAsync(Guid.NewGuid(), "Ed-Fi", "Student");
        await InsertStudentAsync(documentIdA, "STU-EXISTING");

        var expectedReferentialIdA = ComputeReferentialId(
            "Ed-Fi",
            "Student",
            ("$.studentUniqueId", "STU-EXISTING")
        );

        var documentIdB = await InsertDocumentAsync(Guid.NewGuid(), "Ed-Fi", "Student");
        var documentIdC = await InsertDocumentAsync(Guid.NewGuid(), "Ed-Fi", "Student");

        var act = () =>
            _database.ExecuteNonQueryAsync(
                """
                INSERT INTO "edfi"."Student" ("DocumentId", "StudentUniqueId", "FirstName")
                VALUES (@documentIdB, @studentUniqueIdB, @firstNameB),
                       (@documentIdC, @studentUniqueIdC, @firstNameC);
                """,
                new NpgsqlParameter("documentIdB", documentIdB),
                new NpgsqlParameter("studentUniqueIdB", "STU-BULK-1"),
                new NpgsqlParameter("firstNameB", "BulkB"),
                new NpgsqlParameter("documentIdC", documentIdC),
                new NpgsqlParameter("studentUniqueIdC", "STU-EXISTING"),
                new NpgsqlParameter("firstNameC", "BulkC")
            );

        var ex = (await act.Should().ThrowAsync<PostgresException>()).Which;
        ex.SqlState.Should().Be(PostgresErrorCodes.UniqueViolation);

        // Assert — rollback of the Student insert: only A's original RI row exists; B and C have no RI rows
        var referentialIds = await QueryReferentialIdentityRowsAsync();
        referentialIds.Should().HaveCount(1);
        referentialIds
            .Should()
            .Contain(r =>
                (Guid)r["ReferentialId"]! == expectedReferentialIdA && (long)r["DocumentId"]! == documentIdA
            );
    }

    [Test]
    public async Task Bulk_insert_creates_correct_referential_identity_for_each_row()
    {
        // Arrange — three document shells
        var documentIdA = await InsertDocumentAsync(Guid.NewGuid(), "Ed-Fi", "Student");
        var documentIdB = await InsertDocumentAsync(Guid.NewGuid(), "Ed-Fi", "Student");
        var documentIdC = await InsertDocumentAsync(Guid.NewGuid(), "Ed-Fi", "Student");

        // Act — single multi-row INSERT with distinct keys
        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO "edfi"."Student" ("DocumentId", "StudentUniqueId", "FirstName")
            VALUES (@documentIdA, @studentUniqueIdA, @firstNameA),
                   (@documentIdB, @studentUniqueIdB, @firstNameB),
                   (@documentIdC, @studentUniqueIdC, @firstNameC);
            """,
            new NpgsqlParameter("documentIdA", documentIdA),
            new NpgsqlParameter("studentUniqueIdA", "STU-BULK-A"),
            new NpgsqlParameter("firstNameA", "BulkA"),
            new NpgsqlParameter("documentIdB", documentIdB),
            new NpgsqlParameter("studentUniqueIdB", "STU-BULK-B"),
            new NpgsqlParameter("firstNameB", "BulkB"),
            new NpgsqlParameter("documentIdC", documentIdC),
            new NpgsqlParameter("studentUniqueIdC", "STU-BULK-C"),
            new NpgsqlParameter("firstNameC", "BulkC")
        );

        // Assert — each row maps to the correct DocumentId
        var expectedA = ComputeReferentialId("Ed-Fi", "Student", ("$.studentUniqueId", "STU-BULK-A"));
        var expectedB = ComputeReferentialId("Ed-Fi", "Student", ("$.studentUniqueId", "STU-BULK-B"));
        var expectedC = ComputeReferentialId("Ed-Fi", "Student", ("$.studentUniqueId", "STU-BULK-C"));

        var referentialIds = await QueryReferentialIdentityRowsAsync();
        referentialIds.Should().HaveCount(3);

        referentialIds
            .Should()
            .Contain(r => (Guid)r["ReferentialId"]! == expectedA && (long)r["DocumentId"]! == documentIdA);
        referentialIds
            .Should()
            .Contain(r => (Guid)r["ReferentialId"]! == expectedB && (long)r["DocumentId"]! == documentIdB);
        referentialIds
            .Should()
            .Contain(r => (Guid)r["ReferentialId"]! == expectedC && (long)r["DocumentId"]! == documentIdC);
    }

    [Test]
    public async Task Bulk_update_creates_correct_referential_identity_for_each_row()
    {
        // Arrange — two students inserted individually
        var documentIdA = await InsertDocumentAsync(Guid.NewGuid(), "Ed-Fi", "Student");
        await InsertStudentAsync(documentIdA, "STU-UPD-A");

        var documentIdB = await InsertDocumentAsync(Guid.NewGuid(), "Ed-Fi", "Student");
        await InsertStudentAsync(documentIdB, "STU-UPD-B");

        var oldExpectedA = ComputeReferentialId("Ed-Fi", "Student", ("$.studentUniqueId", "STU-UPD-A"));
        var oldExpectedB = ComputeReferentialId("Ed-Fi", "Student", ("$.studentUniqueId", "STU-UPD-B"));

        // Pre-assert — both RI rows exist with old values
        var referentialIds = await QueryReferentialIdentityRowsAsync();
        referentialIds.Should().HaveCount(2);
        referentialIds
            .Should()
            .Contain(r => (Guid)r["ReferentialId"]! == oldExpectedA && (long)r["DocumentId"]! == documentIdA);
        referentialIds
            .Should()
            .Contain(r => (Guid)r["ReferentialId"]! == oldExpectedB && (long)r["DocumentId"]! == documentIdB);

        // Act — single multi-row UPDATE changing both identity values
        await _database.ExecuteNonQueryAsync(
            """
            UPDATE "edfi"."Student"
            SET "StudentUniqueId" = CASE
                WHEN "DocumentId" = @docA THEN @newIdA
                WHEN "DocumentId" = @docB THEN @newIdB
            END
            WHERE "DocumentId" IN (@docA, @docB);
            """,
            new NpgsqlParameter("docA", documentIdA),
            new NpgsqlParameter("newIdA", "STU-UPD-A2"),
            new NpgsqlParameter("docB", documentIdB),
            new NpgsqlParameter("newIdB", "STU-UPD-B2")
        );

        // Assert — old RI IDs gone, new ones correct
        var newExpectedA = ComputeReferentialId("Ed-Fi", "Student", ("$.studentUniqueId", "STU-UPD-A2"));
        var newExpectedB = ComputeReferentialId("Ed-Fi", "Student", ("$.studentUniqueId", "STU-UPD-B2"));

        referentialIds = await QueryReferentialIdentityRowsAsync();
        referentialIds.Should().HaveCount(2);

        referentialIds.Should().NotContain(r => (Guid)r["ReferentialId"]! == oldExpectedA);
        referentialIds.Should().NotContain(r => (Guid)r["ReferentialId"]! == oldExpectedB);

        referentialIds
            .Should()
            .Contain(r => (Guid)r["ReferentialId"]! == newExpectedA && (long)r["DocumentId"]! == documentIdA);
        referentialIds
            .Should()
            .Contain(r => (Guid)r["ReferentialId"]! == newExpectedB && (long)r["DocumentId"]! == documentIdB);
    }

    [Test]
    public async Task Delete_concrete_removes_referential_identity_row()
    {
        // Arrange
        var documentId = await InsertDocumentAsync(Guid.NewGuid(), "Ed-Fi", "Student");
        await InsertStudentAsync(documentId, "STU-DEL");

        var expectedReferentialId = ComputeReferentialId(
            "Ed-Fi",
            "Student",
            ("$.studentUniqueId", "STU-DEL")
        );
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
        referentialIds.All(r => (long)r["DocumentId"]! == documentId).Should().BeTrue();

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

    [Test]
    public async Task DateTime_key_trigger_emits_utc_canonical_form_regardless_of_session_timezone()
    {
        // Arrange — document shell
        var documentId = await InsertDocumentAsync(Guid.NewGuid(), "Ed-Fi", "DateTimeKeyResource");

        // Act — open single connection with Eastern timezone, then insert
        await using var connection = new NpgsqlConnection(_database.ConnectionString);
        await connection.OpenAsync();

        await using var transaction = await connection.BeginTransactionAsync();

        await using var setTzCmd = connection.CreateCommand();
        setTzCmd.Transaction = transaction;
        setTzCmd.CommandText = "SET LOCAL TIME ZONE 'America/New_York'";
        await setTzCmd.ExecuteNonQueryAsync();

        await using var insertCmd = connection.CreateCommand();
        insertCmd.Transaction = transaction;
        insertCmd.CommandText = """
            INSERT INTO "edfi"."DateTimeKeyResource" ("DocumentId", "EventTimestamp")
            VALUES (@documentId, @eventTimestamp);
            """;
        insertCmd.Parameters.Add(new NpgsqlParameter("documentId", documentId));
        insertCmd.Parameters.Add(
            new NpgsqlParameter("eventTimestamp", NpgsqlTypes.NpgsqlDbType.TimestampTz)
            {
                Value = new DateTimeOffset(2025, 1, 1, 12, 0, 0, TimeSpan.Zero),
            }
        );
        await insertCmd.ExecuteNonQueryAsync();

        await transaction.CommitAsync();

        // Assert — trigger must emit UTC form; Eastern session would yield '2025-01-01T07:00:00Z' without AT TIME ZONE fix
        var referentialIds = await QueryReferentialIdentityRowsAsync();
        referentialIds.Should().HaveCount(1);

        var expectedReferentialId = ComputeReferentialId(
            "Ed-Fi",
            "DateTimeKeyResource",
            ("$.eventTimestamp", "2025-01-01T12:00:00Z")
        );
        ((Guid)referentialIds.Single()["ReferentialId"]!).Should().Be(expectedReferentialId);
        ((long)referentialIds.Single()["DocumentId"]!).Should().Be(documentId);
    }

    [Test]
    [TestCase(1.5, "1.5", TestName = "Decimal_1point5_trims_single_trailing_zero")]
    [TestCase(2.0, "2", TestName = "Decimal_2point00_trims_trailing_zeros_and_dot")]
    [TestCase(100.0, "100", TestName = "Decimal_100point00_does_not_eat_integer_zeros")]
    public async Task Decimal_key_trigger_emits_canonical_trimmed_form(
        double rawValue,
        string expectedCanonical
    )
    {
        // Arrange
        var documentId = await InsertDocumentAsync(Guid.NewGuid(), "Ed-Fi", "DecimalKeyResource");

        // Act
        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO "edfi"."DecimalKeyResource" ("DocumentId", "DecimalKey")
            VALUES (@documentId, @decimalKey);
            """,
            new NpgsqlParameter("documentId", documentId),
            new NpgsqlParameter("decimalKey", (decimal)rawValue)
        );

        // Assert
        var referentialIds = await QueryReferentialIdentityRowsAsync();
        referentialIds.Should().HaveCount(1);

        var expectedReferentialId = ComputeReferentialId(
            "Ed-Fi",
            "DecimalKeyResource",
            ("$.decimalKey", expectedCanonical)
        );
        ((Guid)referentialIds.Single()["ReferentialId"]!).Should().Be(expectedReferentialId);
        ((long)referentialIds.Single()["DocumentId"]!).Should().Be(documentId);
    }

    [Test]
    public async Task Decimal_reference_trigger_emits_canonical_form_for_reference_identity_path()
    {
        // Arrange — insert a DecimalKeyResource with decimalKey = 1.50
        var decimalKeyDocumentId = await InsertDocumentAsync(Guid.NewGuid(), "Ed-Fi", "DecimalKeyResource");
        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO "edfi"."DecimalKeyResource" ("DocumentId", "DecimalKey")
            VALUES (@documentId, @decimalKey);
            """,
            new NpgsqlParameter("documentId", decimalKeyDocumentId),
            new NpgsqlParameter("decimalKey", 1.50m)
        );

        // Insert a DecimalRefResource referencing the above
        var refResourceId = "ref-001";
        var refDocumentId = await InsertDocumentAsync(Guid.NewGuid(), "Ed-Fi", "DecimalRefResource");
        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO "edfi"."DecimalRefResource" ("DocumentId", "RefResourceId", "DecimalKeyReference_DocumentId", "DecimalKeyReference_DecimalKey")
            VALUES (@documentId, @refResourceId, @decimalKeyDocumentId, @decimalKey);
            """,
            new NpgsqlParameter("documentId", refDocumentId),
            new NpgsqlParameter("refResourceId", refResourceId),
            new NpgsqlParameter("decimalKeyDocumentId", decimalKeyDocumentId),
            new NpgsqlParameter("decimalKey", 1.50m)
        );

        // Assert — DecimalRefResource's ReferentialId uses the canonical "1.5" form for decimalKey
        var referentialIds = await QueryReferentialIdentityRowsAsync();
        referentialIds.Should().HaveCount(2);

        var expectedRefResourceReferentialId = ComputeReferentialId(
            "Ed-Fi",
            "DecimalRefResource",
            ("$.refResourceId", refResourceId),
            ("$.decimalKeyReference.decimalKey", "1.5")
        );

        var refRow = referentialIds.Single(r => (long)r["DocumentId"]! == refDocumentId);
        ((Guid)refRow["ReferentialId"]!).Should().Be(expectedRefResourceReferentialId);
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

    private async Task<
        IReadOnlyList<IReadOnlyDictionary<string, object?>>
    > QueryReferentialIdentityRowsAsync()
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

    private static IEnumerable<TestCaseData> CollationScenarios()
    {
        yield return new TestCaseData("STU001", "STU002").SetName("Plain value change");
        yield return new TestCaseData("STU001", "stu001").SetName("Case-only change");
        yield return new TestCaseData("STU001", "STU001 ").SetName("Trailing space added");
        yield return new TestCaseData("STU001 ", "STU001").SetName("Trailing space removed");
        yield return new TestCaseData("STU001", " STU001").SetName("Leading space added");
    }

    private static (
        Guid studentReferentialId,
        Guid resourceAReferentialId,
        Guid resourceBReferentialId,
        Guid keyUnifiedReferentialId
    ) ComputeExpectedReferentialIds(
        string studentUniqueId,
        string resourceAId,
        string resourceBId,
        string keyUnifiedResourceId
    )
    {
        var studentReferentialId = ComputeReferentialId(
            "Ed-Fi",
            "Student",
            ("$.studentUniqueId", studentUniqueId)
        );

        var resourceAReferentialId = ComputeReferentialId(
            "Ed-Fi",
            "ResourceA",
            ("$.resourceAId", resourceAId),
            ("$.studentReference.studentUniqueId", studentUniqueId)
        );

        var resourceBReferentialId = ComputeReferentialId(
            "Ed-Fi",
            "ResourceB",
            ("$.resourceBId", resourceBId),
            ("$.studentReference.studentUniqueId", studentUniqueId)
        );

        var keyUnifiedReferentialId = ComputeReferentialId(
            "Ed-Fi",
            "KeyUnifiedResource",
            ("$.keyUnifiedResourceId", keyUnifiedResourceId),
            ("$.resourceAReference.resourceAId", resourceAId),
            ("$.resourceAReference.studentUniqueId", studentUniqueId),
            ("$.resourceBReference.resourceBId", resourceBId),
            ("$.resourceBReference.studentUniqueId", studentUniqueId)
        );

        return (
            studentReferentialId,
            resourceAReferentialId,
            resourceBReferentialId,
            keyUnifiedReferentialId
        );
    }

    private sealed record DocumentStampState(long IdentityVersion, DateTimeOffset IdentityLastModifiedAt);

    private static async Task<DocumentStampState> GetDocumentStampStateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        long documentId
    )
    {
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            SELECT "IdentityVersion", "IdentityLastModifiedAt"
            FROM "dms"."Document"
            WHERE "DocumentId" = @documentId;
            """;
        cmd.Parameters.Add(new NpgsqlParameter("documentId", documentId));

        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new InvalidOperationException($"No dms.Document row for DocumentId={documentId}.");
        }

        return new DocumentStampState(
            Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture),
            ReadDateTimeOffset(reader.GetValue(1))
        );
    }

    private static async Task<
        IReadOnlyList<IReadOnlyDictionary<string, object?>>
    > QueryReferentialIdentityRowsAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction)
    {
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            SELECT "ReferentialId", "DocumentId", "ResourceKeyId"
            FROM "dms"."ReferentialIdentity"
            ORDER BY "ResourceKeyId";
            """;

        await using var reader = await cmd.ExecuteReaderAsync();
        var rows = new List<IReadOnlyDictionary<string, object?>>();
        while (await reader.ReadAsync())
        {
            rows.Add(
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["ReferentialId"] = reader.GetValue(0),
                    ["DocumentId"] = reader.GetValue(1),
                    ["ResourceKeyId"] = reader.GetValue(2),
                }
            );
        }
        return rows;
    }

    private async Task DelayForDistinctTimestampsAsync()
    {
        await _database.ExecuteNonQueryAsync("""SELECT pg_sleep(0.02);""");
    }

    private static DateTimeOffset ReadDateTimeOffset(object? value)
    {
        return value switch
        {
            DateTimeOffset dateTimeOffset => dateTimeOffset,
            DateTime dateTime => new DateTimeOffset(
                dateTime.Kind == DateTimeKind.Unspecified
                    ? DateTime.SpecifyKind(dateTime, DateTimeKind.Utc)
                    : dateTime
            ),
            string text => DateTimeOffset.Parse(text, CultureInfo.InvariantCulture),
            _ => throw new InvalidOperationException(
                $"Unsupported timestamp value type '{value?.GetType().FullName ?? "<null>"}'."
            ),
        };
    }
}
