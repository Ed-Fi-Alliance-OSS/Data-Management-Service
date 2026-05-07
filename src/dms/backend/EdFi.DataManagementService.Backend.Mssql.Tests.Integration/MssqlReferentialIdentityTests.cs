// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using EdFi.DataManagementService.Backend.Tests.Common;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using NUnit.Framework;
using static EdFi.DataManagementService.Backend.Tests.Common.ReferentialIdTestHelper;

namespace EdFi.DataManagementService.Backend.Mssql.Tests.Integration;

[TestFixture]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
public class MssqlReferentialIdentityTests
{
    private const string FixtureRelativePath =
        "src/dms/backend/EdFi.DataManagementService.Backend.Ddl.Tests.Unit/Fixtures/small/referential-identity";

    private MssqlGeneratedDdlFixture _fixture = null!;
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

        _fixture = MssqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(FixtureRelativePath);
        _database = await MssqlGeneratedDdlTestDatabase.CreateProvisionedAsync(_fixture.GeneratedDdl);
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
            UPDATE [edfi].[Student]
            SET [StudentUniqueId] = @newId
            WHERE [DocumentId] = @documentId;
            """,
            new SqlParameter("@newId", "STU-NEW"),
            new SqlParameter("@documentId", documentId)
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
            UPDATE [edfi].[School]
            SET [SchoolId] = @newSchoolId, [EducationOrganizationId] = @newEdOrgId
            WHERE [DocumentId] = @documentId;
            """,
            new SqlParameter("@newSchoolId", 700),
            new SqlParameter("@newEdOrgId", 700),
            new SqlParameter("@documentId", documentId)
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
            UPDATE [edfi].[Student]
            SET [FirstName] = @newName
            WHERE [DocumentId] = @documentId;
            """,
            new SqlParameter("@newName", "Updated"),
            new SqlParameter("@documentId", documentId)
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
            INSERT INTO [edfi].[ResourceA] ([DocumentId], [ResourceAId], [StudentReference_DocumentId], [StudentReference_StudentUniqueId])
            VALUES (@documentId, @resourceAId, @studentDocumentId, @studentUniqueId);
            """,
            new SqlParameter("@documentId", resourceADocumentId),
            new SqlParameter("@resourceAId", "resA-1"),
            new SqlParameter("@studentDocumentId", studentDocumentId),
            new SqlParameter("@studentUniqueId", oldStudentUniqueId)
        );

        // Insert ResourceB referencing Student
        var resourceBDocumentId = await InsertDocumentAsync(Guid.NewGuid(), "Ed-Fi", "ResourceB");
        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO [edfi].[ResourceB] ([DocumentId], [ResourceBId], [StudentReference_DocumentId], [StudentReference_StudentUniqueId])
            VALUES (@documentId, @resourceBId, @studentDocumentId, @studentUniqueId);
            """,
            new SqlParameter("@documentId", resourceBDocumentId),
            new SqlParameter("@resourceBId", "resB-1"),
            new SqlParameter("@studentDocumentId", studentDocumentId),
            new SqlParameter("@studentUniqueId", oldStudentUniqueId)
        );

        // Insert KeyUnifiedResource referencing both A and B
        var keyUnifiedDocumentId = await InsertDocumentAsync(Guid.NewGuid(), "Ed-Fi", "KeyUnifiedResource");
        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO [edfi].[KeyUnifiedResource] ([DocumentId], [KeyUnifiedResourceId], [ResourceAReference_DocumentId], [ResourceAReference_ResourceAId], [ResourceBReference_DocumentId], [ResourceBReference_ResourceBId], [StudentUniqueId_Unified])
            VALUES (@documentId, @keyUnifiedResourceId, @resourceADocumentId, @resourceAId, @resourceBDocumentId, @resourceBId, @studentUniqueId);
            """,
            new SqlParameter("@documentId", keyUnifiedDocumentId),
            new SqlParameter("@keyUnifiedResourceId", "unified-1"),
            new SqlParameter("@resourceADocumentId", resourceADocumentId),
            new SqlParameter("@resourceAId", "resA-1"),
            new SqlParameter("@resourceBDocumentId", resourceBDocumentId),
            new SqlParameter("@resourceBId", "resB-1"),
            new SqlParameter("@studentUniqueId", oldStudentUniqueId)
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
            UPDATE [edfi].[Student]
            SET [StudentUniqueId] = @newId
            WHERE [DocumentId] = @documentId;
            """,
            new SqlParameter("@newId", newStudentUniqueId),
            new SqlParameter("@documentId", studentDocumentId)
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
            INSERT INTO [edfi].[EdOrgDependentResource] ([DocumentId], [EdOrgDependentResourceId], [EducationOrganization_DocumentId], [EducationOrganization_EducationOrganizationId])
            VALUES (@documentId, @edOrgDependentResourceId, @schoolDocumentId, @educationOrganizationId);
            """,
            new SqlParameter("@documentId", edOrgDepDocumentId),
            new SqlParameter("@edOrgDependentResourceId", "dep-1"),
            new SqlParameter("@schoolDocumentId", schoolDocumentId),
            new SqlParameter("@educationOrganizationId", 100)
        );

        // Insert EdOrgDependentChildResource referencing EdOrgDependentResource (dep-1, educationOrganizationId=100)
        var edOrgDepChildDocumentId = await InsertDocumentAsync(
            Guid.NewGuid(),
            "Ed-Fi",
            "EdOrgDependentChildResource"
        );
        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO [edfi].[EdOrgDependentChildResource] ([DocumentId], [EdOrgDependentChildResourceId], [EdOrgDependentResourceReference_DocumentId], [EdOrgDependentResourceReference_EdOrgDependentResourceId], [EdOrgDependentResourceReference_EducationOrganizationId])
            VALUES (@documentId, @childResourceId, @edOrgDepDocumentId, @edOrgDependentResourceId, @educationOrganizationId);
            """,
            new SqlParameter("@documentId", edOrgDepChildDocumentId),
            new SqlParameter("@childResourceId", "child-1"),
            new SqlParameter("@edOrgDepDocumentId", edOrgDepDocumentId),
            new SqlParameter("@edOrgDependentResourceId", "dep-1"),
            new SqlParameter("@educationOrganizationId", 100)
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
            UPDATE [edfi].[School]
            SET [SchoolId] = @newSchoolId, [EducationOrganizationId] = @newEdOrgId
            WHERE [DocumentId] = @documentId;
            """,
            new SqlParameter("@newSchoolId", 200),
            new SqlParameter("@newEdOrgId", 200),
            new SqlParameter("@documentId", schoolDocumentId)
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
            INSERT INTO [edfi].[ResourceA] ([DocumentId], [ResourceAId], [StudentReference_DocumentId], [StudentReference_StudentUniqueId])
            VALUES (@documentId, @resourceAId, @studentDocumentId, @studentUniqueId);
            """,
            new SqlParameter("@documentId", resourceADocumentId),
            new SqlParameter("@resourceAId", "resA-1"),
            new SqlParameter("@studentDocumentId", studentDocumentId),
            new SqlParameter("@studentUniqueId", oldStudentUniqueId)
        );

        var resourceBDocumentId = await InsertDocumentAsync(Guid.NewGuid(), "Ed-Fi", "ResourceB");
        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO [edfi].[ResourceB] ([DocumentId], [ResourceBId], [StudentReference_DocumentId], [StudentReference_StudentUniqueId])
            VALUES (@documentId, @resourceBId, @studentDocumentId, @studentUniqueId);
            """,
            new SqlParameter("@documentId", resourceBDocumentId),
            new SqlParameter("@resourceBId", "resB-1"),
            new SqlParameter("@studentDocumentId", studentDocumentId),
            new SqlParameter("@studentUniqueId", oldStudentUniqueId)
        );

        var keyUnifiedDocumentId = await InsertDocumentAsync(Guid.NewGuid(), "Ed-Fi", "KeyUnifiedResource");
        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO [edfi].[KeyUnifiedResource] ([DocumentId], [KeyUnifiedResourceId], [ResourceAReference_DocumentId], [ResourceAReference_ResourceAId], [ResourceBReference_DocumentId], [ResourceBReference_ResourceBId], [StudentUniqueId_Unified])
            VALUES (@documentId, @keyUnifiedResourceId, @resourceADocumentId, @resourceAId, @resourceBDocumentId, @resourceBId, @studentUniqueId);
            """,
            new SqlParameter("@documentId", keyUnifiedDocumentId),
            new SqlParameter("@keyUnifiedResourceId", "unified-1"),
            new SqlParameter("@resourceADocumentId", resourceADocumentId),
            new SqlParameter("@resourceAId", "resA-1"),
            new SqlParameter("@resourceBDocumentId", resourceBDocumentId),
            new SqlParameter("@resourceBId", "resB-1"),
            new SqlParameter("@studentUniqueId", oldStudentUniqueId)
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
        await using (var snapshotConnection = new SqlConnection(_database.ConnectionString))
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

        // sysutcdatetime() resolution is high but not guaranteed monotonic across
        // sub-millisecond calls; advance the wall clock so BeAfter comparisons hold.
        await DelayForDistinctTimestampsAsync();

        // Act — open an explicit transaction, UPDATE the parent identity, then
        // observe the cascade (driven by the IdentityPropagationFallback trigger
        // on SQL Server) through the same connection BEFORE COMMIT.
        await using var connection = new SqlConnection(_database.ConnectionString);
        await connection.OpenAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();

        await using (var updateCmd = connection.CreateCommand())
        {
            updateCmd.Transaction = transaction;
            updateCmd.CommandText = """
                UPDATE [edfi].[Student]
                SET [StudentUniqueId] = @newId
                WHERE [DocumentId] = @documentId;
                """;
            updateCmd.Parameters.Add(new SqlParameter("@newId", newStudentUniqueId));
            updateCmd.Parameters.Add(new SqlParameter("@documentId", studentDocumentId));
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
    public async Task Cascaded_identity_update_via_abstract_reference_bumps_stamps_and_is_visible_in_same_transaction()
    {
        // Arrange — School (concrete + EducationOrganization alias) → EdOrgDependentResource → EdOrgDependentChildResource.
        // The cascade traverses the abstract reference and the alias-row maintenance must fire in-statement.
        var schoolDocumentId = await InsertDocumentAsync(Guid.NewGuid(), "Ed-Fi", "School");
        await InsertSchoolAsync(schoolDocumentId, 100);

        var edOrgDepDocumentId = await InsertDocumentAsync(Guid.NewGuid(), "Ed-Fi", "EdOrgDependentResource");
        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO [edfi].[EdOrgDependentResource] ([DocumentId], [EdOrgDependentResourceId], [EducationOrganization_DocumentId], [EducationOrganization_EducationOrganizationId])
            VALUES (@documentId, @edOrgDependentResourceId, @schoolDocumentId, @educationOrganizationId);
            """,
            new SqlParameter("@documentId", edOrgDepDocumentId),
            new SqlParameter("@edOrgDependentResourceId", "dep-1"),
            new SqlParameter("@schoolDocumentId", schoolDocumentId),
            new SqlParameter("@educationOrganizationId", 100)
        );

        var edOrgDepChildDocumentId = await InsertDocumentAsync(
            Guid.NewGuid(),
            "Ed-Fi",
            "EdOrgDependentChildResource"
        );
        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO [edfi].[EdOrgDependentChildResource] ([DocumentId], [EdOrgDependentChildResourceId], [EdOrgDependentResourceReference_DocumentId], [EdOrgDependentResourceReference_EdOrgDependentResourceId], [EdOrgDependentResourceReference_EducationOrganizationId])
            VALUES (@documentId, @childResourceId, @edOrgDepDocumentId, @edOrgDependentResourceId, @educationOrganizationId);
            """,
            new SqlParameter("@documentId", edOrgDepChildDocumentId),
            new SqlParameter("@childResourceId", "child-1"),
            new SqlParameter("@edOrgDepDocumentId", edOrgDepDocumentId),
            new SqlParameter("@edOrgDependentResourceId", "dep-1"),
            new SqlParameter("@educationOrganizationId", 100)
        );

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

        DocumentStampState beforeSchool;
        DocumentStampState beforeEdOrgDep;
        DocumentStampState beforeEdOrgDepChild;
        await using (var snapshotConnection = new SqlConnection(_database.ConnectionString))
        {
            await snapshotConnection.OpenAsync();
            beforeSchool = await GetDocumentStampStateAsync(
                snapshotConnection,
                transaction: null,
                schoolDocumentId
            );
            beforeEdOrgDep = await GetDocumentStampStateAsync(
                snapshotConnection,
                transaction: null,
                edOrgDepDocumentId
            );
            beforeEdOrgDepChild = await GetDocumentStampStateAsync(
                snapshotConnection,
                transaction: null,
                edOrgDepChildDocumentId
            );
        }

        await DelayForDistinctTimestampsAsync();

        await using var connection = new SqlConnection(_database.ConnectionString);
        await connection.OpenAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();

        await using (var updateCmd = connection.CreateCommand())
        {
            updateCmd.Transaction = transaction;
            updateCmd.CommandText = """
                UPDATE [edfi].[School]
                SET [SchoolId] = @newSchoolId, [EducationOrganizationId] = @newEdOrgId
                WHERE [DocumentId] = @documentId;
                """;
            updateCmd.Parameters.Add(new SqlParameter("@newSchoolId", 200));
            updateCmd.Parameters.Add(new SqlParameter("@newEdOrgId", 200));
            updateCmd.Parameters.Add(new SqlParameter("@documentId", schoolDocumentId));
            await updateCmd.ExecuteNonQueryAsync();
        }

        // Assert (pre-commit) — concrete + alias + dependent + grand-dependent RI rows all recomputed.
        var inTxnReferentialIds = await QueryReferentialIdentityRowsAsync(connection, transaction);
        inTxnReferentialIds.Should().HaveCount(4);
        inTxnReferentialIds.Should().NotContain(r => (Guid)r["ReferentialId"]! == oldSchoolRI);
        inTxnReferentialIds.Should().NotContain(r => (Guid)r["ReferentialId"]! == oldEdOrgRI);
        inTxnReferentialIds.Should().NotContain(r => (Guid)r["ReferentialId"]! == oldEdOrgDepRI);
        inTxnReferentialIds.Should().NotContain(r => (Guid)r["ReferentialId"]! == oldEdOrgDepChildRI);
        inTxnReferentialIds
            .Should()
            .Contain(r =>
                (Guid)r["ReferentialId"]! == newSchoolRI && (long)r["DocumentId"]! == schoolDocumentId
            );
        inTxnReferentialIds
            .Should()
            .Contain(r =>
                (Guid)r["ReferentialId"]! == newEdOrgRI && (long)r["DocumentId"]! == schoolDocumentId
            );
        inTxnReferentialIds
            .Should()
            .Contain(r =>
                (Guid)r["ReferentialId"]! == newEdOrgDepRI && (long)r["DocumentId"]! == edOrgDepDocumentId
            );
        inTxnReferentialIds
            .Should()
            .Contain(r =>
                (Guid)r["ReferentialId"]! == newEdOrgDepChildRI
                && (long)r["DocumentId"]! == edOrgDepChildDocumentId
            );

        // Assert (pre-commit) — IdentityVersion / IdentityLastModifiedAt strictly bumped on every impacted document.
        var inTxnSchool = await GetDocumentStampStateAsync(connection, transaction, schoolDocumentId);
        var inTxnEdOrgDep = await GetDocumentStampStateAsync(connection, transaction, edOrgDepDocumentId);
        var inTxnEdOrgDepChild = await GetDocumentStampStateAsync(
            connection,
            transaction,
            edOrgDepChildDocumentId
        );

        inTxnSchool.IdentityVersion.Should().BeGreaterThan(beforeSchool.IdentityVersion);
        inTxnSchool.IdentityLastModifiedAt.Should().BeAfter(beforeSchool.IdentityLastModifiedAt);
        inTxnEdOrgDep.IdentityVersion.Should().BeGreaterThan(beforeEdOrgDep.IdentityVersion);
        inTxnEdOrgDep.IdentityLastModifiedAt.Should().BeAfter(beforeEdOrgDep.IdentityLastModifiedAt);
        inTxnEdOrgDepChild.IdentityVersion.Should().BeGreaterThan(beforeEdOrgDepChild.IdentityVersion);
        inTxnEdOrgDepChild
            .IdentityLastModifiedAt.Should()
            .BeAfter(beforeEdOrgDepChild.IdentityLastModifiedAt);

        await transaction.CommitAsync();

        var postCommitReferentialIds = await QueryReferentialIdentityRowsAsync();
        postCommitReferentialIds.Should().HaveCount(4);
        postCommitReferentialIds
            .Should()
            .Contain(r =>
                (Guid)r["ReferentialId"]! == newSchoolRI && (long)r["DocumentId"]! == schoolDocumentId
            );
        postCommitReferentialIds
            .Should()
            .Contain(r =>
                (Guid)r["ReferentialId"]! == newEdOrgRI && (long)r["DocumentId"]! == schoolDocumentId
            );
        postCommitReferentialIds
            .Should()
            .Contain(r =>
                (Guid)r["ReferentialId"]! == newEdOrgDepRI && (long)r["DocumentId"]! == edOrgDepDocumentId
            );
        postCommitReferentialIds
            .Should()
            .Contain(r =>
                (Guid)r["ReferentialId"]! == newEdOrgDepChildRI
                && (long)r["DocumentId"]! == edOrgDepChildDocumentId
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
            INSERT INTO [dms].[ReferentialIdentity] ([ReferentialId], [DocumentId], [ResourceKeyId])
            VALUES (@referentialId, @documentId, @resourceKeyId);
            """,
            new SqlParameter("@referentialId", duplicateReferentialId),
            new SqlParameter("@documentId", documentIdA),
            new SqlParameter("@resourceKeyId", studentResourceKeyId)
        );

        // Act & Assert — same ReferentialId violates PK
        var act = () =>
            _database.ExecuteNonQueryAsync(
                """
                INSERT INTO [dms].[ReferentialIdentity] ([ReferentialId], [DocumentId], [ResourceKeyId])
                VALUES (@referentialId, @documentId, @resourceKeyId);
                """,
                new SqlParameter("@referentialId", duplicateReferentialId),
                new SqlParameter("@documentId", documentIdB),
                new SqlParameter("@resourceKeyId", studentResourceKeyId)
            );

        var ex = (await act.Should().ThrowAsync<SqlException>()).Which;
        ex.Number.Should().Be(2627);
        ex.Message.Should().Contain("PK_ReferentialIdentity");
    }

    [Test]
    public async Task Direct_insert_duplicate_document_resource_key_is_rejected()
    {
        // Arrange
        var documentId = await InsertDocumentAsync(Guid.NewGuid(), "Ed-Fi", "Student");
        var studentResourceKeyId = await GetResourceKeyIdAsync("Ed-Fi", "Student");

        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO [dms].[ReferentialIdentity] ([ReferentialId], [DocumentId], [ResourceKeyId])
            VALUES (@referentialId, @documentId, @resourceKeyId);
            """,
            new SqlParameter("@referentialId", Guid.NewGuid()),
            new SqlParameter("@documentId", documentId),
            new SqlParameter("@resourceKeyId", studentResourceKeyId)
        );

        // Act & Assert — same (DocumentId, ResourceKeyId) violates unique constraint
        var act = () =>
            _database.ExecuteNonQueryAsync(
                """
                INSERT INTO [dms].[ReferentialIdentity] ([ReferentialId], [DocumentId], [ResourceKeyId])
                VALUES (@referentialId, @documentId, @resourceKeyId);
                """,
                new SqlParameter("@referentialId", Guid.NewGuid()),
                new SqlParameter("@documentId", documentId),
                new SqlParameter("@resourceKeyId", studentResourceKeyId)
            );

        var ex = (await act.Should().ThrowAsync<SqlException>()).Which;
        ex.Number.Should().Be(2627);
        ex.Message.Should().Contain("UX_ReferentialIdentity_DocumentId_ResourceKeyId");
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
                UPDATE [edfi].[Student]
                SET [StudentUniqueId] = @newId
                WHERE [DocumentId] = @documentId;
                """,
                new SqlParameter("@newId", "STU-A"),
                new SqlParameter("@documentId", documentIdB)
            );

        var ex = (await act.Should().ThrowAsync<SqlException>()).Which;
        ex.Number.Should().Be(2627);

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

        // Act — multi-row INSERT; C's key collides with A's
        var act = () =>
            _database.ExecuteNonQueryAsync(
                """
                INSERT INTO [edfi].[Student] ([DocumentId], [StudentUniqueId], [FirstName])
                VALUES (@documentIdB, @studentUniqueIdB, @firstNameB),
                       (@documentIdC, @studentUniqueIdC, @firstNameC);
                """,
                new SqlParameter("@documentIdB", documentIdB),
                new SqlParameter("@studentUniqueIdB", "STU-BULK-1"),
                new SqlParameter("@firstNameB", "BulkB"),
                new SqlParameter("@documentIdC", documentIdC),
                new SqlParameter("@studentUniqueIdC", "STU-EXISTING"),
                new SqlParameter("@firstNameC", "BulkC")
            );

        var ex = (await act.Should().ThrowAsync<SqlException>()).Which;
        ex.Number.Should().Be(2627);

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
            INSERT INTO [edfi].[Student] ([DocumentId], [StudentUniqueId], [FirstName])
            VALUES (@documentIdA, @studentUniqueIdA, @firstNameA),
                   (@documentIdB, @studentUniqueIdB, @firstNameB),
                   (@documentIdC, @studentUniqueIdC, @firstNameC);
            """,
            new SqlParameter("@documentIdA", documentIdA),
            new SqlParameter("@studentUniqueIdA", "STU-BULK-A"),
            new SqlParameter("@firstNameA", "BulkA"),
            new SqlParameter("@documentIdB", documentIdB),
            new SqlParameter("@studentUniqueIdB", "STU-BULK-B"),
            new SqlParameter("@firstNameB", "BulkB"),
            new SqlParameter("@documentIdC", documentIdC),
            new SqlParameter("@studentUniqueIdC", "STU-BULK-C"),
            new SqlParameter("@firstNameC", "BulkC")
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
            UPDATE [edfi].[Student]
            SET [StudentUniqueId] = CASE
                WHEN [DocumentId] = @docA THEN @newIdA
                WHEN [DocumentId] = @docB THEN @newIdB
            END
            WHERE [DocumentId] IN (@docA, @docB);
            """,
            new SqlParameter("@docA", documentIdA),
            new SqlParameter("@newIdA", "STU-UPD-A2"),
            new SqlParameter("@docB", documentIdB),
            new SqlParameter("@newIdB", "STU-UPD-B2")
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
            DELETE FROM [dms].[Document] WHERE [DocumentId] = @documentId;
            """,
            new SqlParameter("@documentId", documentId)
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
            DELETE FROM [dms].[Document] WHERE [DocumentId] = @documentId;
            """,
            new SqlParameter("@documentId", documentId)
        );

        // Assert
        referentialIds = await QueryReferentialIdentityRowsAsync();
        referentialIds.Should().BeEmpty();
    }

    private async Task<short> GetResourceKeyIdAsync(string projectName, string resourceName)
    {
        return await _database.ExecuteScalarAsync<short>(
            """
            SELECT [ResourceKeyId]
            FROM [dms].[ResourceKey]
            WHERE [ProjectName] = @projectName
              AND [ResourceName] = @resourceName;
            """,
            new SqlParameter("@projectName", projectName),
            new SqlParameter("@resourceName", resourceName)
        );
    }

    private async Task<long> InsertDocumentAsync(Guid documentUuid, string projectName, string resourceName)
    {
        var resourceKeyId = await GetResourceKeyIdAsync(projectName, resourceName);

        return await _database.ExecuteScalarAsync<long>(
            """
            DECLARE @Inserted TABLE ([DocumentId] bigint);
            INSERT INTO [dms].[Document] ([DocumentUuid], [ResourceKeyId])
            OUTPUT INSERTED.[DocumentId] INTO @Inserted ([DocumentId])
            VALUES (@documentUuid, @resourceKeyId);
            SELECT TOP (1) [DocumentId] FROM @Inserted;
            """,
            new SqlParameter("@documentUuid", documentUuid),
            new SqlParameter("@resourceKeyId", resourceKeyId)
        );
    }

    private async Task<
        IReadOnlyList<IReadOnlyDictionary<string, object?>>
    > QueryReferentialIdentityRowsAsync()
    {
        return await _database.QueryRowsAsync(
            """
            SELECT [ReferentialId], [DocumentId], [ResourceKeyId]
            FROM [dms].[ReferentialIdentity]
            ORDER BY [ResourceKeyId];
            """
        );
    }

    private async Task InsertStudentAsync(long documentId, string studentUniqueId, string firstName = "Test")
    {
        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO [edfi].[Student] ([DocumentId], [StudentUniqueId], [FirstName])
            VALUES (@documentId, @studentUniqueId, @firstName);
            """,
            new SqlParameter("@documentId", documentId),
            new SqlParameter("@studentUniqueId", studentUniqueId),
            new SqlParameter("@firstName", firstName)
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
            INSERT INTO [edfi].[School] ([DocumentId], [EducationOrganizationId], [NameOfInstitution], [SchoolId])
            VALUES (@documentId, @educationOrganizationId, @nameOfInstitution, @schoolId);
            """,
            new SqlParameter("@documentId", documentId),
            new SqlParameter("@educationOrganizationId", schoolId),
            new SqlParameter("@nameOfInstitution", nameOfInstitution),
            new SqlParameter("@schoolId", schoolId)
        );
    }

    [Test]
    public async Task DateTime_trigger_emits_utc_z_suffix()
    {
        // Arrange — plain UTC wall-clock literal; datetime2 is timezone-naive (no offset suffix)
        var documentId = await InsertDocumentAsync(Guid.NewGuid(), "Ed-Fi", "DateTimeKeyResource");

        // Act
        await InsertDateTimeKeyResourceAsync(documentId, "2025-01-01T12:00:00");

        // Assert — trigger must append Z so the ReferentialId matches the Core canonical form
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
    [TestCase("1.5", "1.5", TestName = "Single trailing zero stripped (1.50 stored as 1.5)")]
    [TestCase("2.00", "2", TestName = "Fractional zeros and decimal point stripped (2.00 -> 2)")]
    [TestCase("100.00", "100", TestName = "Integer trailing zeros preserved (100.00 -> 100)")]
    public async Task Decimal_top_level_identity_trims_trailing_zeros(
        string insertedValue,
        string expectedCanonical
    )
    {
        // Arrange
        var documentId = await InsertDocumentAsync(Guid.NewGuid(), "Ed-Fi", "DecimalKeyResource");

        // Act
        await InsertDecimalKeyResourceAsync(documentId, insertedValue);

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
    public async Task Decimal_reference_identity_trims_trailing_zeros()
    {
        // Arrange — insert DecimalKeyResource with decimalKey=1.50 (stored as decimal(9,2))
        var decimalKeyDocumentId = await InsertDocumentAsync(Guid.NewGuid(), "Ed-Fi", "DecimalKeyResource");
        await InsertDecimalKeyResourceAsync(decimalKeyDocumentId, "1.50");

        var refResourceId = "ref-dec-1";
        var decimalRefDocumentId = await InsertDocumentAsync(Guid.NewGuid(), "Ed-Fi", "DecimalRefResource");

        // Act — insert DecimalRefResource referencing the DecimalKeyResource row
        await InsertDecimalRefResourceAsync(
            decimalRefDocumentId,
            refResourceId,
            decimalKeyDocumentId,
            "1.50"
        );

        // Assert — DecimalRefResource RI: reference path $.decimalKeyReference.decimalKey canonical form
        var referentialIds = await QueryReferentialIdentityRowsAsync();
        referentialIds.Should().HaveCount(2);

        var expectedDecimalRefReferentialId = ComputeReferentialId(
            "Ed-Fi",
            "DecimalRefResource",
            ("$.refResourceId", refResourceId),
            ("$.decimalKeyReference.decimalKey", "1.5")
        );

        var decimalRefRow = referentialIds.Single(r => (long)r["DocumentId"]! == decimalRefDocumentId);
        ((Guid)decimalRefRow["ReferentialId"]!).Should().Be(expectedDecimalRefReferentialId);
    }

    private async Task InsertDateTimeKeyResourceAsync(long documentId, string eventTimestamp)
    {
        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO [edfi].[DateTimeKeyResource] ([DocumentId], [EventTimestamp])
            VALUES (@documentId, @eventTimestamp);
            """,
            new SqlParameter("@documentId", documentId),
            new SqlParameter("@eventTimestamp", eventTimestamp)
        );
    }

    private async Task InsertDecimalKeyResourceAsync(long documentId, string decimalKey)
    {
        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO [edfi].[DecimalKeyResource] ([DocumentId], [DecimalKey])
            VALUES (@documentId, @decimalKey);
            """,
            new SqlParameter("@documentId", documentId),
            new SqlParameter(
                "@decimalKey",
                decimal.Parse(decimalKey, System.Globalization.CultureInfo.InvariantCulture)
            )
        );
    }

    private async Task InsertDecimalRefResourceAsync(
        long documentId,
        string refResourceId,
        long decimalKeyReferenceDocumentId,
        string decimalKeyReferenceDecimalKey
    )
    {
        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO [edfi].[DecimalRefResource] ([DocumentId], [RefResourceId], [DecimalKeyReference_DocumentId], [DecimalKeyReference_DecimalKey])
            VALUES (@documentId, @refResourceId, @decimalKeyReferenceDocumentId, @decimalKeyReferenceDecimalKey);
            """,
            new SqlParameter("@documentId", documentId),
            new SqlParameter("@refResourceId", refResourceId),
            new SqlParameter("@decimalKeyReferenceDocumentId", decimalKeyReferenceDocumentId),
            new SqlParameter(
                "@decimalKeyReferenceDecimalKey",
                decimal.Parse(
                    decimalKeyReferenceDecimalKey,
                    System.Globalization.CultureInfo.InvariantCulture
                )
            )
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
        SqlConnection connection,
        SqlTransaction? transaction,
        long documentId
    )
    {
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            SELECT [IdentityVersion], [IdentityLastModifiedAt]
            FROM [dms].[Document]
            WHERE [DocumentId] = @documentId;
            """;
        cmd.Parameters.Add(new SqlParameter("@documentId", documentId));

        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new InvalidOperationException($"No dms.Document row for DocumentId={documentId}.");
        }

        return new DocumentStampState(reader.GetInt64(0), ReadDateTimeOffset(reader.GetValue(1)));
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

    private static async Task<
        IReadOnlyList<IReadOnlyDictionary<string, object?>>
    > QueryReferentialIdentityRowsAsync(SqlConnection connection, SqlTransaction? transaction)
    {
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            SELECT [ReferentialId], [DocumentId], [ResourceKeyId]
            FROM [dms].[ReferentialIdentity]
            ORDER BY [ResourceKeyId];
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
        await _database.ExecuteNonQueryAsync("""WAITFOR DELAY '00:00:00.050';""");
    }
}
