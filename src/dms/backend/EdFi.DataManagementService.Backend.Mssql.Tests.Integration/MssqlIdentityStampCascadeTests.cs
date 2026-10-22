// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using EdFi.DataManagementService.Backend.Tests.Integration.Common;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Mssql.Tests.Integration;

[TestFixture]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
[Category(MssqlCiShards.Shard3)]
public class MssqlIdentityStampCascadeTests
{
    private const string FixtureRelativePath =
        "src/dms/backend/EdFi.DataManagementService.Backend.Ddl.Tests.Unit/Fixtures/small/referential-identity";

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

        _fixture = MssqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(FixtureRelativePath);
        _databaseLease = await MssqlBackendBaselineCache.AcquireLeaseAsync(
            FixtureRelativePath,
            strict: false,
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
    public async Task Cascaded_identity_update_bumps_stamps_and_is_visible_in_same_transaction()
    {
        // Arrange — Student → ResourceA/B → KeyUnifiedResource chain
        const string OldStudentUniqueId = "STU-XACT-OLD";
        const string NewStudentUniqueId = "STU-XACT-NEW";

        var studentDocumentId = await InsertDocumentAsync(Guid.NewGuid(), "Ed-Fi", "Student");
        await InsertStudentAsync(studentDocumentId, OldStudentUniqueId);

        var resourceADocumentId = await InsertDocumentAsync(Guid.NewGuid(), "Ed-Fi", "ResourceA");
        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO [edfi].[ResourceA] ([DocumentId], [ResourceAId], [StudentReference_DocumentId], [StudentReference_StudentUniqueId])
            VALUES (@documentId, @resourceAId, @studentDocumentId, @studentUniqueId);
            """,
            new SqlParameter("@documentId", resourceADocumentId),
            new SqlParameter("@resourceAId", "resA-1"),
            new SqlParameter("@studentDocumentId", studentDocumentId),
            new SqlParameter("@studentUniqueId", OldStudentUniqueId)
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
            new SqlParameter("@studentUniqueId", OldStudentUniqueId)
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
            new SqlParameter("@studentUniqueId", OldStudentUniqueId)
        );

        // Capture before-snapshots of content stamps for all four documents
        await BackdateOwnerTimestampsAsync();
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
                studentDocumentId,
                "Student"
            );
            beforeResourceA = await GetDocumentStampStateAsync(
                snapshotConnection,
                transaction: null,
                resourceADocumentId,
                "ResourceA"
            );
            beforeResourceB = await GetDocumentStampStateAsync(
                snapshotConnection,
                transaction: null,
                resourceBDocumentId,
                "ResourceB"
            );
            beforeKeyUnified = await GetDocumentStampStateAsync(
                snapshotConnection,
                transaction: null,
                keyUnifiedDocumentId,
                "KeyUnifiedResource"
            );
        }

        // Act — open an explicit transaction, UPDATE the parent identity, then
        // observe the cascade (driven by the native ON UPDATE CASCADE reference
        // foreign keys on SQL Server) through the same connection BEFORE COMMIT.
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
            updateCmd.Parameters.Add(new SqlParameter("@newId", NewStudentUniqueId));
            updateCmd.Parameters.Add(new SqlParameter("@documentId", studentDocumentId));
            await updateCmd.ExecuteNonQueryAsync();
        }

        // Assert (pre-commit) — the new identity reached every dependent on the chain.
        (
            await ReadIdentityValueAsync(
                "ResourceA",
                "StudentReference_StudentUniqueId",
                resourceADocumentId,
                connection,
                transaction
            )
        )
            .Should()
            .Be(NewStudentUniqueId);
        (
            await ReadIdentityValueAsync(
                "ResourceB",
                "StudentReference_StudentUniqueId",
                resourceBDocumentId,
                connection,
                transaction
            )
        )
            .Should()
            .Be(NewStudentUniqueId);
        (
            await ReadIdentityValueAsync(
                "KeyUnifiedResource",
                "StudentUniqueId_Unified",
                keyUnifiedDocumentId,
                connection,
                transaction
            )
        )
            .Should()
            .Be(NewStudentUniqueId);

        // Assert (pre-commit) — ContentVersion / ContentLastModifiedAt strictly bumped
        // on the parent and on every cascaded dependent.
        var inTxnStudent = await GetDocumentStampStateAsync(
            connection,
            transaction,
            studentDocumentId,
            "Student"
        );
        var inTxnResourceA = await GetDocumentStampStateAsync(
            connection,
            transaction,
            resourceADocumentId,
            "ResourceA"
        );
        var inTxnResourceB = await GetDocumentStampStateAsync(
            connection,
            transaction,
            resourceBDocumentId,
            "ResourceB"
        );
        var inTxnKeyUnified = await GetDocumentStampStateAsync(
            connection,
            transaction,
            keyUnifiedDocumentId,
            "KeyUnifiedResource"
        );

        inTxnStudent.ContentVersion.Should().BeGreaterThan(beforeStudent.ContentVersion);
        inTxnStudent.ContentLastModifiedAt.Should().BeAfter(beforeStudent.ContentLastModifiedAt);
        inTxnResourceA.ContentVersion.Should().BeGreaterThan(beforeResourceA.ContentVersion);
        inTxnResourceA.ContentLastModifiedAt.Should().BeAfter(beforeResourceA.ContentLastModifiedAt);
        inTxnResourceB.ContentVersion.Should().BeGreaterThan(beforeResourceB.ContentVersion);
        inTxnResourceB.ContentLastModifiedAt.Should().BeAfter(beforeResourceB.ContentLastModifiedAt);
        inTxnKeyUnified.ContentVersion.Should().BeGreaterThan(beforeKeyUnified.ContentVersion);
        inTxnKeyUnified.ContentLastModifiedAt.Should().BeAfter(beforeKeyUnified.ContentLastModifiedAt);

        await transaction.CommitAsync();

        // Post-commit — the propagated identity survives commit on a fresh connection.
        (await ReadIdentityValueAsync("ResourceA", "StudentReference_StudentUniqueId", resourceADocumentId))
            .Should()
            .Be(NewStudentUniqueId);
        (await ReadIdentityValueAsync("ResourceB", "StudentReference_StudentUniqueId", resourceBDocumentId))
            .Should()
            .Be(NewStudentUniqueId);
        (await ReadIdentityValueAsync("KeyUnifiedResource", "StudentUniqueId_Unified", keyUnifiedDocumentId))
            .Should()
            .Be(NewStudentUniqueId);

        // Post-commit — stamp values survive commit on a connection that never saw the in-flight transaction.
        await using (var postCommitConnection = new SqlConnection(_database.ConnectionString))
        {
            await postCommitConnection.OpenAsync();
            var postCommitStudent = await GetDocumentStampStateAsync(
                postCommitConnection,
                transaction: null,
                studentDocumentId,
                "Student"
            );
            var postCommitResourceA = await GetDocumentStampStateAsync(
                postCommitConnection,
                transaction: null,
                resourceADocumentId,
                "ResourceA"
            );
            var postCommitResourceB = await GetDocumentStampStateAsync(
                postCommitConnection,
                transaction: null,
                resourceBDocumentId,
                "ResourceB"
            );
            var postCommitKeyUnified = await GetDocumentStampStateAsync(
                postCommitConnection,
                transaction: null,
                keyUnifiedDocumentId,
                "KeyUnifiedResource"
            );

            postCommitStudent.ContentVersion.Should().Be(inTxnStudent.ContentVersion);
            postCommitStudent.ContentLastModifiedAt.Should().Be(inTxnStudent.ContentLastModifiedAt);
            postCommitResourceA.ContentVersion.Should().Be(inTxnResourceA.ContentVersion);
            postCommitResourceA.ContentLastModifiedAt.Should().Be(inTxnResourceA.ContentLastModifiedAt);
            postCommitResourceB.ContentVersion.Should().Be(inTxnResourceB.ContentVersion);
            postCommitResourceB.ContentLastModifiedAt.Should().Be(inTxnResourceB.ContentLastModifiedAt);
            postCommitKeyUnified.ContentVersion.Should().Be(inTxnKeyUnified.ContentVersion);
            postCommitKeyUnified.ContentLastModifiedAt.Should().Be(inTxnKeyUnified.ContentLastModifiedAt);
        }
    }

    [Test]
    public async Task Cascaded_identity_update_rolls_back_with_parent_transaction()
    {
        // SQL Server cascade is driven by native ON UPDATE CASCADE reference foreign keys,
        // so explicitly assert the cascaded recompute reverts on parent rollback.
        const string OldStudentUniqueId = "STU-RBK-OLD";
        const string NewStudentUniqueId = "STU-RBK-NEW";

        var studentDocumentId = await InsertDocumentAsync(Guid.NewGuid(), "Ed-Fi", "Student");
        await InsertStudentAsync(studentDocumentId, OldStudentUniqueId);

        var resourceADocumentId = await InsertDocumentAsync(Guid.NewGuid(), "Ed-Fi", "ResourceA");
        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO [edfi].[ResourceA] ([DocumentId], [ResourceAId], [StudentReference_DocumentId], [StudentReference_StudentUniqueId])
            VALUES (@documentId, @resourceAId, @studentDocumentId, @studentUniqueId);
            """,
            new SqlParameter("@documentId", resourceADocumentId),
            new SqlParameter("@resourceAId", "resA-1"),
            new SqlParameter("@studentDocumentId", studentDocumentId),
            new SqlParameter("@studentUniqueId", OldStudentUniqueId)
        );

        await BackdateOwnerTimestampsAsync();
        DocumentStampState beforeStudent;
        DocumentStampState beforeResourceA;
        await using (var snapshotConnection = new SqlConnection(_database.ConnectionString))
        {
            await snapshotConnection.OpenAsync();
            beforeStudent = await GetDocumentStampStateAsync(
                snapshotConnection,
                transaction: null,
                studentDocumentId,
                "Student"
            );
            beforeResourceA = await GetDocumentStampStateAsync(
                snapshotConnection,
                transaction: null,
                resourceADocumentId,
                "ResourceA"
            );
        }

        await using (var connection = new SqlConnection(_database.ConnectionString))
        {
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
                updateCmd.Parameters.Add(new SqlParameter("@newId", NewStudentUniqueId));
                updateCmd.Parameters.Add(new SqlParameter("@documentId", studentDocumentId));
                await updateCmd.ExecuteNonQueryAsync();
            }

            // Confirm the cascade fired pre-rollback so the rollback assertion is meaningful.
            (
                await ReadIdentityValueAsync(
                    "ResourceA",
                    "StudentReference_StudentUniqueId",
                    resourceADocumentId,
                    connection,
                    transaction
                )
            )
                .Should()
                .Be(NewStudentUniqueId);

            await transaction.RollbackAsync();
        }

        // Post-rollback — the identity and stamps reflect the original (pre-update) state on a fresh connection.
        (await ReadIdentityValueAsync("Student", "StudentUniqueId", studentDocumentId))
            .Should()
            .Be(OldStudentUniqueId);
        (await ReadIdentityValueAsync("ResourceA", "StudentReference_StudentUniqueId", resourceADocumentId))
            .Should()
            .Be(OldStudentUniqueId);

        await using (var postRollbackConnection = new SqlConnection(_database.ConnectionString))
        {
            await postRollbackConnection.OpenAsync();
            var postRollbackStudent = await GetDocumentStampStateAsync(
                postRollbackConnection,
                transaction: null,
                studentDocumentId,
                "Student"
            );
            var postRollbackResourceA = await GetDocumentStampStateAsync(
                postRollbackConnection,
                transaction: null,
                resourceADocumentId,
                "ResourceA"
            );

            postRollbackStudent.ContentVersion.Should().Be(beforeStudent.ContentVersion);
            postRollbackStudent.ContentLastModifiedAt.Should().Be(beforeStudent.ContentLastModifiedAt);
            postRollbackResourceA.ContentVersion.Should().Be(beforeResourceA.ContentVersion);
            postRollbackResourceA.ContentLastModifiedAt.Should().Be(beforeResourceA.ContentLastModifiedAt);
        }
    }

    [Test]
    public async Task Cascaded_identity_update_via_abstract_reference_bumps_stamps_and_is_visible_in_same_transaction()
    {
        // Arrange — School (a concrete EducationOrganization) → EdOrgDependentResource → EdOrgDependentChildResource.
        // The cascade traverses the abstract reference, so the abstract identity row must update in-statement.
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

        await BackdateOwnerTimestampsAsync();
        DocumentStampState beforeSchool;
        DocumentStampState beforeEdOrgDep;
        DocumentStampState beforeEdOrgDepChild;
        await using (var snapshotConnection = new SqlConnection(_database.ConnectionString))
        {
            await snapshotConnection.OpenAsync();
            beforeSchool = await GetDocumentStampStateAsync(
                snapshotConnection,
                transaction: null,
                schoolDocumentId,
                "School"
            );
            beforeEdOrgDep = await GetDocumentStampStateAsync(
                snapshotConnection,
                transaction: null,
                edOrgDepDocumentId,
                "EdOrgDependentResource"
            );
            beforeEdOrgDepChild = await GetDocumentStampStateAsync(
                snapshotConnection,
                transaction: null,
                edOrgDepChildDocumentId,
                "EdOrgDependentChildResource"
            );
        }

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

        // Assert (pre-commit) — the new identity reached the dependent and the grand-dependent.
        (
            await ReadIdentityValueAsync(
                "EdOrgDependentResource",
                "EducationOrganization_EducationOrganizationId",
                edOrgDepDocumentId,
                connection,
                transaction
            )
        )
            .Should()
            .Be("200");
        (
            await ReadIdentityValueAsync(
                "EdOrgDependentChildResource",
                "EdOrgDependentResourceReference_EducationOrganizationId",
                edOrgDepChildDocumentId,
                connection,
                transaction
            )
        )
            .Should()
            .Be("200");

        // Assert (pre-commit) — ContentVersion / ContentLastModifiedAt strictly bumped on every impacted document.
        var inTxnSchool = await GetDocumentStampStateAsync(
            connection,
            transaction,
            schoolDocumentId,
            "School"
        );
        var inTxnEdOrgDep = await GetDocumentStampStateAsync(
            connection,
            transaction,
            edOrgDepDocumentId,
            "EdOrgDependentResource"
        );
        var inTxnEdOrgDepChild = await GetDocumentStampStateAsync(
            connection,
            transaction,
            edOrgDepChildDocumentId,
            "EdOrgDependentChildResource"
        );

        inTxnSchool.ContentVersion.Should().BeGreaterThan(beforeSchool.ContentVersion);
        inTxnSchool.ContentLastModifiedAt.Should().BeAfter(beforeSchool.ContentLastModifiedAt);
        inTxnEdOrgDep.ContentVersion.Should().BeGreaterThan(beforeEdOrgDep.ContentVersion);
        inTxnEdOrgDep.ContentLastModifiedAt.Should().BeAfter(beforeEdOrgDep.ContentLastModifiedAt);
        inTxnEdOrgDepChild.ContentVersion.Should().BeGreaterThan(beforeEdOrgDepChild.ContentVersion);
        inTxnEdOrgDepChild.ContentLastModifiedAt.Should().BeAfter(beforeEdOrgDepChild.ContentLastModifiedAt);

        await transaction.CommitAsync();

        (
            await ReadIdentityValueAsync(
                "EdOrgDependentResource",
                "EducationOrganization_EducationOrganizationId",
                edOrgDepDocumentId
            )
        )
            .Should()
            .Be("200");
        (
            await ReadIdentityValueAsync(
                "EdOrgDependentChildResource",
                "EdOrgDependentResourceReference_EducationOrganizationId",
                edOrgDepChildDocumentId
            )
        )
            .Should()
            .Be("200");

        // Post-commit — stamp values survive commit on a connection that never saw the in-flight transaction.
        await using (var postCommitConnection = new SqlConnection(_database.ConnectionString))
        {
            await postCommitConnection.OpenAsync();
            var postCommitSchool = await GetDocumentStampStateAsync(
                postCommitConnection,
                transaction: null,
                schoolDocumentId,
                "School"
            );
            var postCommitEdOrgDep = await GetDocumentStampStateAsync(
                postCommitConnection,
                transaction: null,
                edOrgDepDocumentId,
                "EdOrgDependentResource"
            );
            var postCommitEdOrgDepChild = await GetDocumentStampStateAsync(
                postCommitConnection,
                transaction: null,
                edOrgDepChildDocumentId,
                "EdOrgDependentChildResource"
            );

            postCommitSchool.ContentVersion.Should().Be(inTxnSchool.ContentVersion);
            postCommitSchool.ContentLastModifiedAt.Should().Be(inTxnSchool.ContentLastModifiedAt);
            postCommitEdOrgDep.ContentVersion.Should().Be(inTxnEdOrgDep.ContentVersion);
            postCommitEdOrgDep.ContentLastModifiedAt.Should().Be(inTxnEdOrgDep.ContentLastModifiedAt);
            postCommitEdOrgDepChild.ContentVersion.Should().Be(inTxnEdOrgDepChild.ContentVersion);
            postCommitEdOrgDepChild
                .ContentLastModifiedAt.Should()
                .Be(inTxnEdOrgDepChild.ContentLastModifiedAt);
        }
    }

    [Test]
    public async Task Update_to_duplicate_natural_key_is_rejected()
    {
        // Arrange — two students with different keys
        var documentIdA = await InsertDocumentAsync(Guid.NewGuid(), "Ed-Fi", "Student");
        await InsertStudentAsync(documentIdA, "STU-A");

        var documentIdB = await InsertDocumentAsync(Guid.NewGuid(), "Ed-Fi", "Student");
        await InsertStudentAsync(documentIdB, "STU-B");

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

        // Assert — the rejected statement left both original students unchanged
        (await ReadStudentKeysAsync())
            .Should()
            .Equal($"{documentIdA}|STU-A", $"{documentIdB}|STU-B");
    }

    [Test]
    public async Task Bulk_insert_with_duplicate_natural_key_is_rejected()
    {
        // Arrange — one existing student
        var documentIdA = await InsertDocumentAsync(Guid.NewGuid(), "Ed-Fi", "Student");
        await InsertStudentAsync(documentIdA, "STU-EXISTING");

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

        // Assert — the rejected bulk insert added no Student row; only A remains
        (await ReadStudentKeysAsync())
            .Should()
            .Equal($"{documentIdA}|STU-EXISTING");
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

    /// <summary>
    /// Reads one identity column of a root row. Given a connection and transaction it observes the uncommitted
    /// cascade inside that transaction; otherwise it reads committed state on a fresh connection.
    /// </summary>
    private async Task<string?> ReadIdentityValueAsync(
        string rootTable,
        string column,
        long documentId,
        SqlConnection? connection = null,
        SqlTransaction? transaction = null
    )
    {
        await using var ownConnection = connection is null
            ? new SqlConnection(_database.ConnectionString)
            : null;
        if (ownConnection is not null)
        {
            await ownConnection.OpenAsync();
        }

        await using var cmd = (connection ?? ownConnection!).CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = $"SELECT [{column}] FROM [edfi].[{rootTable}] WHERE [DocumentId] = @documentId;";
        cmd.Parameters.Add(new SqlParameter("@documentId", documentId));

        return Convert.ToString(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    private async Task<IReadOnlyList<string>> ReadStudentKeysAsync()
    {
        var rows = await _database.QueryRowsAsync(
            "SELECT [DocumentId], [StudentUniqueId] FROM [edfi].[Student] ORDER BY [DocumentId];"
        );

        return [.. rows.Select(row => $"{row["DocumentId"]}|{row["StudentUniqueId"]}")];
    }

    private sealed record DocumentStampState(long ContentVersion, DateTimeOffset ContentLastModifiedAt);

    private static async Task<DocumentStampState> GetDocumentStampStateAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        long documentId,
        string rootTable
    )
    {
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = $"""
            SELECT doc.[ContentVersion], owner.[ContentLastModifiedAt], owner.[ContentVersion]
            FROM [dms].[Document] doc
            JOIN [edfi].[{rootTable}] owner ON owner.[DocumentId] = doc.[DocumentId]
            WHERE doc.[DocumentId] = @documentId;
            """;
        cmd.Parameters.Add(new SqlParameter("@documentId", documentId));

        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new InvalidOperationException($"No dms.Document row for DocumentId={documentId}.");
        }

        reader.GetInt64(2).Should().Be(reader.GetInt64(0));
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

    private async Task BackdateOwnerTimestampsAsync()
    {
        foreach (
            var rootTable in new[]
            {
                "Student",
                "ResourceA",
                "ResourceB",
                "KeyUnifiedResource",
                "School",
                "EdOrgDependentResource",
                "EdOrgDependentChildResource",
            }
        )
        {
            await _database.ExecuteNonQueryAsync(
                $"UPDATE [edfi].[{rootTable}] SET [ContentLastModifiedAt] = '2000-01-01';"
            );
        }
    }
}
