// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Tests.Common;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using NUnit.Framework;
using static EdFi.DataManagementService.Backend.Tests.Common.ReferentialIdTestHelper;

namespace EdFi.DataManagementService.Backend.Mssql.Tests.Integration;

[TestFixture]
[NonParallelizable]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
public class MssqlReferentialIdentityTests
{
    private const string FixtureRelativePath =
        "src/dms/backend/EdFi.DataManagementService.Backend.Ddl.Tests.Unit/Fixtures/small/referential-identity";

    private MssqlGeneratedDdlFixture _fixture = null!;
    private MssqlGeneratedDdlTestDatabase _database = null!;

    [SetUp]
    public async Task Setup()
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
        var keyUnifiedDocumentId = await InsertDocumentAsync(
            Guid.NewGuid(),
            "Ed-Fi",
            "KeyUnifiedResource"
        );
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
                (Guid)r["ReferentialId"]! == expectedStudentOld
                && (long)r["DocumentId"]! == studentDocumentId
            );
        referentialIds
            .Should()
            .Contain(r =>
                (Guid)r["ReferentialId"]! == expectedResAOld
                && (long)r["DocumentId"]! == resourceADocumentId
            );
        referentialIds
            .Should()
            .Contain(r =>
                (Guid)r["ReferentialId"]! == expectedResBOld
                && (long)r["DocumentId"]! == resourceBDocumentId
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
                (Guid)r["ReferentialId"]! == expectedStudentNew
                && (long)r["DocumentId"]! == studentDocumentId
            );
        referentialIds
            .Should()
            .Contain(r =>
                (Guid)r["ReferentialId"]! == expectedResANew
                && (long)r["DocumentId"]! == resourceADocumentId
            );
        referentialIds
            .Should()
            .Contain(r =>
                (Guid)r["ReferentialId"]! == expectedResBNew
                && (long)r["DocumentId"]! == resourceBDocumentId
            );
        referentialIds
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

        return (studentReferentialId, resourceAReferentialId, resourceBReferentialId, keyUnifiedReferentialId);
    }
}
