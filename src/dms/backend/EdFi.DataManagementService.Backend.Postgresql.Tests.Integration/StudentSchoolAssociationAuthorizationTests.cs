// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Postgresql.Test.Integration;
using EdFi.DataManagementService.Core.External.Backend;
using FluentAssertions;
using Npgsql;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Postgresql.Tests.Integration;

public class StudentSchoolAssociationAuthorizationTests : DatabaseTest
{
    protected const long SEA_ID = 9;
    protected const long LEA_ID = 99;
    protected const long SCHOOL_ID = 999;

    [TestFixture]
    public class Given_An_Upsert_Of_One_School_One_StudentSchoolAssociation
        : StudentSchoolAssociationAuthorizationTests
    {
        [SetUp]
        public async Task SetUp()
        {
            var edOrgResult = await UpsertEducationOrganization("School", SCHOOL_ID, null);
            var ssaResult = await UpsertStudentSchoolAssociation(SCHOOL_ID, "0123");

            edOrgResult.Should().BeOfType<UpsertResult.InsertSuccess>();
            ssaResult.Should().BeOfType<UpsertResult.InsertSuccess>();
        }

        [Test]
        public async Task Then_StudentSchoolAssociationAuthorization_Should_Be_Populated()
        {
            // Act
            var authorizations = await GetAllStudentSchoolAssociationAuthorizations();

            // Assert
            authorizations.Should().NotBeNull();
            authorizations.Should().HaveCount(1);

            var authorization = authorizations[0];
            authorization.StudentUniqueId.Should().Be("0123");
            authorization.HierarchySchoolId.Should().Be(SCHOOL_ID);
            ParseEducationOrganizationIds(authorization.StudentSchoolAuthorizationEducationOrganizationIds)
                .Should()
                .BeEquivalentTo([SCHOOL_ID]);
            authorization.StudentSchoolAssociationId.Should().BeGreaterThan(0);
            authorization.StudentSchoolAssociationPartitionKey.Should().BeGreaterThanOrEqualTo(0);
        }
    }

    [TestFixture]
    public class Given_An_Upsert_Of_One_School_One_StudentSchoolAssociation_Followed_By_Delete
        : StudentSchoolAssociationAuthorizationTests
    {
        [SetUp]
        public async Task SetUp()
        {
            var edOrgResult = await UpsertEducationOrganization("School", SCHOOL_ID, null);
            var ssaResult = await UpsertStudentSchoolAssociation(SCHOOL_ID, "0123");

            edOrgResult.Should().BeOfType<UpsertResult.InsertSuccess>();
            ssaResult.Should().BeOfType<UpsertResult.InsertSuccess>();

            await DeleteStudentSchoolAssociation(
                ((UpsertResult.InsertSuccess)ssaResult).NewDocumentUuid.Value
            );
        }

        [Test]
        public async Task Then_StudentSchoolAssociationAuthorization_Should_Be_Empty()
        {
            // Act
            var authorizations = await GetAllStudentSchoolAssociationAuthorizations();

            // Assert
            authorizations.Should().NotBeNull();
            authorizations.Should().HaveCount(0);
        }
    }

    [TestFixture]
    public class Given_An_Upsert_Of_School_Hierarchy_One_StudentSchoolAssociation
        : StudentSchoolAssociationAuthorizationTests
    {
        [SetUp]
        public async Task SetUp()
        {
            await UpsertEducationOrganization("StateEducationAgency", SEA_ID, null);
            await UpsertEducationOrganization("LocalEducationAgency", LEA_ID, SEA_ID);
            await UpsertEducationOrganization("School", SCHOOL_ID, LEA_ID);
            await UpsertStudentSchoolAssociation(SCHOOL_ID, "0123");
        }

        [Test]
        public async Task Then_StudentSchoolAssociationAuthorization_Should_Be_Populated()
        {
            // Act
            var authorizations = await GetAllStudentSchoolAssociationAuthorizations();

            // Assert
            authorizations.Should().NotBeNull();
            authorizations.Should().HaveCount(1);

            var authorization = authorizations[0];
            authorization.StudentUniqueId.Should().Be("0123");
            authorization.HierarchySchoolId.Should().Be(SCHOOL_ID);
            ParseEducationOrganizationIds(authorization.StudentSchoolAuthorizationEducationOrganizationIds)
                .Should()
                .BeEquivalentTo([SEA_ID, LEA_ID, SCHOOL_ID]);
            authorization.StudentSchoolAssociationId.Should().BeGreaterThan(0);
            authorization.StudentSchoolAssociationPartitionKey.Should().BeGreaterThanOrEqualTo(0);
        }
    }

    [TestFixture]
    public class Given_An_Upsert_Of_School_Hierarchy_With_Update_One_StudentSchoolAssociation
        : StudentSchoolAssociationAuthorizationTests
    {
        private readonly Guid _schoolUuid = Guid.NewGuid();
        private readonly Guid _schoolRefId = Guid.NewGuid();

        [SetUp]
        public async Task SetUp()
        {
            await UpsertEducationOrganization("StateEducationAgency", SEA_ID, null);
            await UpsertEducationOrganization("LocalEducationAgency", LEA_ID, SEA_ID);

            // insert without parentid
            await UpsertEducationOrganization(_schoolUuid, _schoolRefId, "School", SCHOOL_ID, null);
            await UpsertStudentSchoolAssociation(SCHOOL_ID, "0123");
        }

        [Test, Order(1)]
        public async Task Then_StudentSchoolAssociationAuthorization_Should_Have_One_EdOrg()
        {
            var authorizations = await GetAllStudentSchoolAssociationAuthorizations();
            authorizations.Should().NotBeNull();
            authorizations.Should().HaveCount(1);

            var authorization = authorizations[0];
            authorization.HierarchySchoolId.Should().Be(SCHOOL_ID);
            ParseEducationOrganizationIds(authorization.StudentSchoolAuthorizationEducationOrganizationIds)
                .Should()
                .BeEquivalentTo([SCHOOL_ID]);
        }

        [Test, Order(2)]
        public async Task Then_StudentSchoolAssociationAuthorization_Should_Have_Three_EdOrg()
        {
            // Move School under LEA
            var updateResult = await UpdateEducationOrganization(
                _schoolUuid,
                _schoolRefId,
                "School",
                SCHOOL_ID,
                LEA_ID
            );
            updateResult.Should().BeOfType<UpdateResult.UpdateSuccess>();

            var authorizations = await GetAllStudentSchoolAssociationAuthorizations();
            authorizations.Should().NotBeNull();
            authorizations.Should().HaveCount(1);

            var authorization = authorizations[0];
            authorization.HierarchySchoolId.Should().Be(SCHOOL_ID);
            ParseEducationOrganizationIds(authorization.StudentSchoolAuthorizationEducationOrganizationIds)
                .Should()
                .BeEquivalentTo([SEA_ID, LEA_ID, SCHOOL_ID]);
        }
    }

    [TestFixture]
    public class Given_An_Upsert_Of_School_Hierarchy_With_Move_One_StudentSchoolAssociation
        : StudentSchoolAssociationAuthorizationTests
    {
        private readonly Guid _documentUuid = Guid.NewGuid();
        private readonly Guid _referentialId = Guid.NewGuid();

        [SetUp]
        public async Task SetUp()
        {
            await UpsertEducationOrganization("StateEducationAgency", SEA_ID, null);
            await UpsertEducationOrganization("LocalEducationAgency", LEA_ID, SEA_ID);
            await UpsertEducationOrganization("School", SCHOOL_ID, LEA_ID);

            await UpsertStudentSchoolAssociation(_documentUuid, _referentialId, SCHOOL_ID, "0123");
        }

        [Test, Order(1)]
        public async Task Then_StudentSchoolAssociationAuthorization_Should_Have_One_EdOrg()
        {
            var authorizations = await GetAllStudentSchoolAssociationAuthorizations();
            authorizations.Should().NotBeNull();
            authorizations.Should().HaveCount(1);

            var authorization = authorizations[0];
            authorization.HierarchySchoolId.Should().Be(SCHOOL_ID);
            ParseEducationOrganizationIds(authorization.StudentSchoolAuthorizationEducationOrganizationIds)
                .Should()
                .BeEquivalentTo([SCHOOL_ID, LEA_ID, SEA_ID]);
        }

        [Test, Order(2)]
        public async Task Then_Moving_SSA_StudentSchoolAssociationAuthorization_Should_Have_Two_EdOrg()
        {
            await UpdateStudentSchoolAssociation(_documentUuid, _referentialId, LEA_ID, "0123");
            var authorizations = await GetAllStudentSchoolAssociationAuthorizations();
            authorizations.Should().NotBeNull();
            authorizations.Should().HaveCount(1);

            var authorization = authorizations[0];
            authorization.HierarchySchoolId.Should().Be(LEA_ID);
            ParseEducationOrganizationIds(authorization.StudentSchoolAuthorizationEducationOrganizationIds)
                .Should()
                .BeEquivalentTo([SEA_ID, LEA_ID]);
        }
    }

    [TestFixture]
    public class Given_An_Upsert_Of_One_StudentSecurableDocument : StudentSchoolAssociationAuthorizationTests
    {
        private UpsertResult studentSecurableDocumentResult;

        [SetUp]
        public async Task SetUp()
        {
            var edOrgResult = await UpsertEducationOrganization("School", SCHOOL_ID, null);
            var ssaResult = await UpsertStudentSchoolAssociation(SCHOOL_ID, "0123");
            studentSecurableDocumentResult = await UpsertStudentSecurableDocument("0123");

            edOrgResult.Should().BeOfType<UpsertResult.InsertSuccess>();
            ssaResult.Should().BeOfType<UpsertResult.InsertSuccess>();
            studentSecurableDocumentResult.Should().BeOfType<UpsertResult.InsertSuccess>();
        }

        [Test, Order(1)]
        public async Task Then_StudentSchoolAssociationAuthorization_Should_Be_Populated()
        {
            // Act
            var authorizations = await GetAllStudentSchoolAssociationAuthorizations();

            // Assert
            authorizations.Should().NotBeNull();
            authorizations.Should().HaveCount(1);

            var authorization = authorizations[0];
            authorization.StudentUniqueId.Should().Be("0123");
            authorization.HierarchySchoolId.Should().Be(SCHOOL_ID);
            ParseEducationOrganizationIds(authorization.StudentSchoolAuthorizationEducationOrganizationIds)
                .Should()
                .BeEquivalentTo([SCHOOL_ID]);
            authorization.StudentSchoolAssociationId.Should().BeGreaterThan(0);
            authorization.StudentSchoolAssociationPartitionKey.Should().BeGreaterThanOrEqualTo(0);
        }

        [Test, Order(2)]
        public async Task Then_StudentSecurableDocuments_Should_Be_Populated()
        {
            var documents = await GetAllStudentSecurableDocuments();

            // Assert
            documents.Should().NotBeNull();
            documents.Should().HaveCount(2);

            documents.TrueForAll(d => d.StudentUniqueId == "0123").Should().BeTrue();
        }

        [Test, Order(3)]
        public async Task Then_Document_StudentSchoolAuthorizationEdOrgIds_Should_Be_Populated()
        {
            string idsString = await GetDocumentStudentSchoolAuthorizationEdOrgIds(
                ((UpsertResult.InsertSuccess)studentSecurableDocumentResult).NewDocumentUuid.Value
            );
            ParseEducationOrganizationIds(idsString).Should().BeEquivalentTo([SCHOOL_ID]);
        }

        [Test, Order(4)]
        public async Task Then_Deleting_StudentSecurableDocument_Should_Cascade()
        {
            Guid studentSecurableDocumentId = ((UpsertResult.InsertSuccess)studentSecurableDocumentResult)
                .NewDocumentUuid
                .Value;
            IDeleteRequest deleteRequest = CreateDeleteRequest(
                "CourseTranscript",
                studentSecurableDocumentId
            );
            await CreateDeleteById().DeleteById(deleteRequest, Connection!, Transaction!);

            var documents = await GetAllStudentSecurableDocuments();

            // Assert
            documents.Should().NotBeNull();
            documents.Should().HaveCount(1);
        }
    }

    private class StudentSchoolAssociationAuthorization
    {
        public required string StudentUniqueId { get; set; }
        public long HierarchySchoolId { get; set; }
        public required string StudentSchoolAuthorizationEducationOrganizationIds { get; set; }
        public long StudentSchoolAssociationId { get; set; }
        public short StudentSchoolAssociationPartitionKey { get; set; }
    }

    private class StudentSecurableDocument
    {
        public required string StudentUniqueId { get; set; }
        public long StudentSecurableDocumentId { get; set; }
        public short StudentSecurableDocumentPartitionKey { get; set; }
    }

    private async Task<UpsertResult> UpsertEducationOrganization(
        string resourceName,
        long schoolId,
        long? parentEducationOrganizationId
    )
    {
        return await UpsertEducationOrganization(
            Guid.NewGuid(),
            Guid.NewGuid(),
            resourceName,
            schoolId,
            parentEducationOrganizationId
        );
    }

    private async Task<UpsertResult> UpsertEducationOrganization(
        Guid documentUuid,
        Guid referentialId,
        string resourceName,
        long schoolId,
        long? parentEducationOrganizationId
    )
    {
        IUpsertRequest upsertRequest = CreateUpsertRequest(
            resourceName,
            documentUuid,
            referentialId,
            $$"""
            {
                "someProperty": "someValue"
            }
            """,
            isInEducationOrganizationHierarchy: true,
            educationOrganizationId: schoolId,
            parentEducationOrganizationId: parentEducationOrganizationId,
            projectName: "Ed-Fi"
        );

        return await CreateUpsert().Upsert(upsertRequest, Connection!, Transaction!);
    }

    private async Task<UpdateResult> UpdateEducationOrganization(
        Guid documentUuid,
        Guid referentialId,
        string resourceName,
        long schoolId,
        long? parentEducationOrganizationId
    )
    {
        IUpdateRequest updateRequest = CreateUpdateRequest(
            resourceName,
            documentUuid,
            referentialId,
            $$"""
            {
                "someProperty": "someValue"
            }
            """,
            isInEducationOrganizationHierarchy: true,
            educationOrganizationId: schoolId,
            parentEducationOrganizationId: parentEducationOrganizationId,
            projectName: "Ed-Fi"
        );

        return await CreateUpdate().UpdateById(updateRequest, Connection!, Transaction!);
    }

    private async Task<UpsertResult> UpsertStudentSchoolAssociation(long schoolId, string studentUniqueId)
    {
        return await UpsertStudentSchoolAssociation(
            Guid.NewGuid(),
            Guid.NewGuid(),
            schoolId,
            studentUniqueId
        );
    }

    private async Task<UpsertResult> UpsertStudentSchoolAssociation(
        Guid documentUuid,
        Guid referentialId,
        long schoolId,
        string studentUniqueId
    )
    {
        IUpsertRequest upsertRequest = CreateUpsertRequest(
            "StudentSchoolAssociation",
            documentUuid,
            referentialId,
            $$"""
            {
                "studentReference": {
                  "studentUniqueId": "{{studentUniqueId}}"
                },
                "schoolReference": {
                  "schoolId": {{schoolId}}
                }
            }
            """,
            isStudentAuthorizationSecurable: true,
            studentUniqueId: studentUniqueId,
            projectName: "Ed-Fi"
        );

        return await CreateUpsert().Upsert(upsertRequest, Connection!, Transaction!);
    }

    private async Task<UpsertResult> UpsertStudentSecurableDocument(string studentUniqueId)
    {
        return await UpsertStudentSecurableDocument(Guid.NewGuid(), Guid.NewGuid(), studentUniqueId);
    }

    private async Task<UpsertResult> UpsertStudentSecurableDocument(
        Guid documentUuid,
        Guid referentialId,
        string studentUniqueId
    )
    {
        IUpsertRequest upsertRequest = CreateUpsertRequest(
            "CourseTranscript",
            documentUuid,
            referentialId,
            $$"""
            {
                "studentReference": {
                  "studentUniqueId": "{{studentUniqueId}}"
                }
            }
            """,
            isStudentAuthorizationSecurable: true,
            studentUniqueId: studentUniqueId,
            projectName: "Ed-Fi"
        );

        try
        {
            return await CreateUpsert().Upsert(upsertRequest, Connection!, Transaction!);
        }
        catch (Exception e)
        {
            Console.WriteLine(e);
            throw;
        }
    }

    private async Task<UpdateResult> UpdateStudentSchoolAssociation(
        Guid documentUuid,
        Guid referentialId,
        long schoolId,
        string studentUniqueId
    )
    {
        IUpdateRequest updateRequest = CreateUpdateRequest(
            "StudentSchoolAssociation",
            documentUuid,
            referentialId,
            $$"""
            {
                "studentReference": {
                  "studentUniqueId": "{{studentUniqueId}}"
                },
                "schoolReference": {
                  "schoolId": {{schoolId}}
                }
            }
            """,
            projectName: "Ed-Fi"
        );

        return await CreateUpdate().UpdateById(updateRequest, Connection!, Transaction!);
    }

    private async Task<DeleteResult> DeleteStudentSchoolAssociation(Guid documentUuid)
    {
        IDeleteRequest deleteRequest = CreateDeleteRequest("StudentSchoolAssociation", documentUuid);
        return await CreateDeleteById().DeleteById(deleteRequest, Connection!, Transaction!);
    }

    private async Task<
        List<StudentSchoolAssociationAuthorization>
    > GetAllStudentSchoolAssociationAuthorizations()
    {
        var command = Connection!.CreateCommand();
        command.Transaction = Transaction;
        command.CommandText = "SELECT * FROM dms.StudentSchoolAssociationAuthorization";

        var results = new List<StudentSchoolAssociationAuthorization>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var authorization = new StudentSchoolAssociationAuthorization
            {
                StudentUniqueId = reader["StudentUniqueId"].ToString()!,
                HierarchySchoolId = (long)reader["HierarchySchoolId"],
                StudentSchoolAuthorizationEducationOrganizationIds = reader[
                    "StudentSchoolAuthorizationEducationOrganizationIds"
                ]
                    .ToString()!,
                StudentSchoolAssociationId = (long)reader["StudentSchoolAssociationId"],
                StudentSchoolAssociationPartitionKey = (short)reader["StudentSchoolAssociationPartitionKey"],
            };
            results.Add(authorization);
        }

        return results;
    }

    private async Task<string> GetDocumentStudentSchoolAuthorizationEdOrgIds(Guid documentUuid)
    {
        await using NpgsqlCommand command = new(
            "SELECT StudentSchoolAuthorizationEdOrgIds FROM dms.Document WHERE DocumentUuid = $1;",
            Connection!,
            Transaction!
        )
        {
            Parameters = { new() { Value = documentUuid } },
        };

        await using var reader = await command.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            return reader["StudentSchoolAuthorizationEdOrgIds"].ToString()!;
        }

        throw new InvalidOperationException("No matching document found.");
    }

    private async Task<List<StudentSecurableDocument>> GetAllStudentSecurableDocuments()
    {
        var command = Connection!.CreateCommand();
        command.Transaction = Transaction;
        command.CommandText = "SELECT * FROM dms.studentsecurabledocument";

        var results = new List<StudentSecurableDocument>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var authorization = new StudentSecurableDocument
            {
                StudentUniqueId = reader["StudentUniqueId"].ToString()!,
                StudentSecurableDocumentId = (long)reader["StudentSecurableDocumentId"],
                StudentSecurableDocumentPartitionKey = (short)reader["StudentSecurableDocumentPartitionKey"],
            };
            results.Add(authorization);
        }

        return results;
    }

    private static long[] ParseEducationOrganizationIds(string ids)
    {
        return ids.Trim('[', ']')
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(long.Parse)
            .ToArray();
    }
}
