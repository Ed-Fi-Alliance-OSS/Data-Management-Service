// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Backend;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Postgresql.Tests.Integration;

public class StudentEducationOrganizationResponsibilityAssociationAuthorizationTests
    : DatabaseIntegrationTestHelper
{
    protected const long SEA_ID = 9;
    protected const long LEA_ID = 99;
    protected const long SCHOOL_ID = 999;

    [TestFixture]
    public class Given_An_Upsert_Of_One_School_One_StudentEducationOrganizationResponsibilityAssociation
        : StudentEducationOrganizationResponsibilityAssociationAuthorizationTests
    {
        [SetUp]
        public async Task SetUp()
        {
            var edOrgResult = await UpsertEducationOrganization("School", SCHOOL_ID, null);
            var seoraResult = await UpsertStudentEducationOrganizationResponsibilityAssociation(
                SCHOOL_ID,
                "0123"
            );

            edOrgResult.Should().BeOfType<UpsertResult.InsertSuccess>();
            seoraResult.Should().BeOfType<UpsertResult.InsertSuccess>();
        }

        [Test]
        public async Task Then_StudentEducationOrganizationResponsibilityAssociationAuthorization_Should_Be_Populated()
        {
            // Act
            var authorizations = await GetAllStudentEducationOrganizationResponsibilityAuthorizations();

            // Assert
            authorizations.Should().NotBeNull();
            authorizations.Should().HaveCount(1);

            var authorization = authorizations[0];
            authorization.StudentUniqueId.Should().Be("0123");
            authorization.HierarchyEducationOrganizationId.Should().Be(SCHOOL_ID);
            ParseEducationOrganizationIds(authorization.StudentEdOrgResponsibilityAuthorizationEdOrgIds)
                .Should()
                .BeEquivalentTo([SCHOOL_ID]);
            authorization.StudentEducationOrganizationResponsibilityAssociationId.Should().BeGreaterThan(0);
            authorization
                .StudentEducationOrganizationResponsibilityAssociationPartitionKey.Should()
                .BeGreaterThanOrEqualTo(0);
        }
    }

    [TestFixture]
    public class Given_An_Upsert_Of_One_School_One_StudentEducationOrganizationResponsibilityAssociation_Followed_By_Delete
        : StudentEducationOrganizationResponsibilityAssociationAuthorizationTests
    {
        [SetUp]
        public async Task SetUp()
        {
            var edOrgResult = await UpsertEducationOrganization("School", SCHOOL_ID, null);
            var seoraResult = await UpsertStudentEducationOrganizationResponsibilityAssociation(
                SCHOOL_ID,
                "0123"
            );

            edOrgResult.Should().BeOfType<UpsertResult.InsertSuccess>();
            seoraResult.Should().BeOfType<UpsertResult.InsertSuccess>();

            await DeleteStudentEducationOrganizationResponsibilityAssociation(
                ((UpsertResult.InsertSuccess)seoraResult).NewDocumentUuid.Value
            );
        }

        [Test]
        public async Task Then_StudentEducationOrganizationResponsibilityAssociationAuthorization_Should_Be_Empty()
        {
            // Act
            var authorizations = await GetAllStudentEducationOrganizationResponsibilityAuthorizations();

            // Assert
            authorizations.Should().NotBeNull();
            authorizations.Should().HaveCount(0);
        }
    }

    [TestFixture]
    public class Given_An_Upsert_Of_School_Hierarchy_One_StudentEducationOrganizationResponsibilityAssociation
        : StudentEducationOrganizationResponsibilityAssociationAuthorizationTests
    {
        [SetUp]
        public async Task SetUp()
        {
            await UpsertEducationOrganization("StateEducationAgency", SEA_ID, null);
            await UpsertEducationOrganization("LocalEducationAgency", LEA_ID, SEA_ID);
            await UpsertEducationOrganization("School", SCHOOL_ID, LEA_ID);
            await UpsertStudentEducationOrganizationResponsibilityAssociation(SCHOOL_ID, "0123");
        }

        [Test]
        public async Task Then_StudentEducationOrganizationResponsibilityAssociationAuthorization_Should_Be_Populated()
        {
            // Act
            var authorizations = await GetAllStudentEducationOrganizationResponsibilityAuthorizations();

            // Assert
            authorizations.Should().NotBeNull();
            authorizations.Should().HaveCount(1);

            var authorization = authorizations[0];
            authorization.StudentUniqueId.Should().Be("0123");
            authorization.HierarchyEducationOrganizationId.Should().Be(SCHOOL_ID);
            ParseEducationOrganizationIds(authorization.StudentEdOrgResponsibilityAuthorizationEdOrgIds)
                .Should()
                .BeEquivalentTo([SEA_ID, LEA_ID, SCHOOL_ID]);
            authorization.StudentEducationOrganizationResponsibilityAssociationId.Should().BeGreaterThan(0);
            authorization
                .StudentEducationOrganizationResponsibilityAssociationPartitionKey.Should()
                .BeGreaterThanOrEqualTo(0);
        }
    }

    [TestFixture]
    public class Given_An_Upsert_Of_School_Hierarchy_With_Update_One_StudentEducationOrganizationResponsibilityAssociation
        : StudentEducationOrganizationResponsibilityAssociationAuthorizationTests
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
            await UpsertStudentEducationOrganizationResponsibilityAssociation(SCHOOL_ID, "0123");
        }

        [Test, Order(1)]
        public async Task Then_StudentEducationOrganizationResponsibilityAssociationAuthorization_Should_Have_One_EdOrg()
        {
            var authorizations = await GetAllStudentEducationOrganizationResponsibilityAuthorizations();
            authorizations.Should().NotBeNull();
            authorizations.Should().HaveCount(1);

            var authorization = authorizations[0];
            authorization.HierarchyEducationOrganizationId.Should().Be(SCHOOL_ID);
            ParseEducationOrganizationIds(authorization.StudentEdOrgResponsibilityAuthorizationEdOrgIds)
                .Should()
                .BeEquivalentTo([SCHOOL_ID]);
        }

        [Test, Order(2)]
        public async Task Then_StudentEducationOrganizationResponsibilityAssociationAuthorization_Should_Have_Three_EdOrg()
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

            var authorizations = await GetAllStudentEducationOrganizationResponsibilityAuthorizations();
            authorizations.Should().NotBeNull();
            authorizations.Should().HaveCount(1);

            var authorization = authorizations[0];
            authorization.HierarchyEducationOrganizationId.Should().Be(SCHOOL_ID);
            ParseEducationOrganizationIds(authorization.StudentEdOrgResponsibilityAuthorizationEdOrgIds)
                .Should()
                .BeEquivalentTo([SEA_ID, LEA_ID, SCHOOL_ID]);
        }
    }

    [TestFixture]
    public class Given_An_Upsert_Of_School_Hierarchy_With_Move_One_StudentEducationOrganizationResponsibilityAssociation
        : StudentEducationOrganizationResponsibilityAssociationAuthorizationTests
    {
        private readonly Guid _documentUuid = Guid.NewGuid();
        private readonly Guid _referentialId = Guid.NewGuid();

        [SetUp]
        public async Task SetUp()
        {
            await UpsertEducationOrganization("StateEducationAgency", SEA_ID, null);
            await UpsertEducationOrganization("LocalEducationAgency", LEA_ID, SEA_ID);
            await UpsertEducationOrganization("School", SCHOOL_ID, LEA_ID);

            await UpsertStudentEducationOrganizationResponsibilityAssociation(
                _documentUuid,
                _referentialId,
                SCHOOL_ID,
                "0123"
            );
        }

        [Test, Order(1)]
        public async Task Then_StudentEducationOrganizationResponsibilityAssociationAuthorization_Should_Have_One_EdOrg()
        {
            var authorizations = await GetAllStudentEducationOrganizationResponsibilityAuthorizations();
            authorizations.Should().NotBeNull();
            authorizations.Should().HaveCount(1);

            var authorization = authorizations[0];
            authorization.HierarchyEducationOrganizationId.Should().Be(SCHOOL_ID);
            ParseEducationOrganizationIds(authorization.StudentEdOrgResponsibilityAuthorizationEdOrgIds)
                .Should()
                .BeEquivalentTo([SCHOOL_ID, LEA_ID, SEA_ID]);
        }

        [Test, Order(2)]
        public async Task Then_Moving_SEORA_StudentEducationOrganizationResponsibilityAssociationAuthorization_Should_Have_Two_EdOrg()
        {
            await UpdateStudentEducationOrganizationResponsibilityAssociation(
                _documentUuid,
                _referentialId,
                LEA_ID,
                "0123"
            );
            var authorizations = await GetAllStudentEducationOrganizationResponsibilityAuthorizations();
            authorizations.Should().NotBeNull();
            authorizations.Should().HaveCount(1);

            var authorization = authorizations[0];
            authorization.HierarchyEducationOrganizationId.Should().Be(LEA_ID);
            ParseEducationOrganizationIds(authorization.StudentEdOrgResponsibilityAuthorizationEdOrgIds)
                .Should()
                .BeEquivalentTo([SEA_ID, LEA_ID]);
        }
    }

    [TestFixture]
    public class Given_An_Upsert_Of_One_StudentSecurableDocument
        : StudentEducationOrganizationResponsibilityAssociationAuthorizationTests
    {
        private UpsertResult studentSecurableDocumentResult;

        [SetUp]
        public async Task SetUp()
        {
            var edOrgResult = await UpsertEducationOrganization("School", SCHOOL_ID, null);
            var seoraResult = await UpsertStudentEducationOrganizationResponsibilityAssociation(
                SCHOOL_ID,
                "0123"
            );
            studentSecurableDocumentResult = await UpsertStudentSecurableDocument("0123");

            edOrgResult.Should().BeOfType<UpsertResult.InsertSuccess>();
            seoraResult.Should().BeOfType<UpsertResult.InsertSuccess>();
            studentSecurableDocumentResult.Should().BeOfType<UpsertResult.InsertSuccess>();
        }

        [Test, Order(1)]
        public async Task Then_StudentEducationOrganizationResponsibilityAssociationAuthorization_Should_Be_Populated()
        {
            // Act
            var authorizations = await GetAllStudentEducationOrganizationResponsibilityAuthorizations();

            // Assert
            authorizations.Should().NotBeNull();
            authorizations.Should().HaveCount(1);

            var authorization = authorizations[0];
            authorization.StudentUniqueId.Should().Be("0123");
            authorization.HierarchyEducationOrganizationId.Should().Be(SCHOOL_ID);
            ParseEducationOrganizationIds(authorization.StudentEdOrgResponsibilityAuthorizationEdOrgIds)
                .Should()
                .BeEquivalentTo([SCHOOL_ID]);
            authorization.StudentEducationOrganizationResponsibilityAssociationId.Should().BeGreaterThan(0);
            authorization
                .StudentEducationOrganizationResponsibilityAssociationPartitionKey.Should()
                .BeGreaterThanOrEqualTo(0);
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
        public async Task Then_Document_StudentEducationOrganizationResponsibilityAuthorizationEdOrgIds_Should_Be_Populated()
        {
            string idsString = await GetDocumentStudentEdOrgResponsibilityAuthorizationIds(
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

    [TestFixture]
    public class Given_An_Upsert_Of_School_Hierarchy_With_StudentSecurableDocument_With_Move_One_StudentEducationOrganizationResponsibilityAssociation
        : StudentEducationOrganizationResponsibilityAssociationAuthorizationTests
    {
        private readonly Guid _documentUuid = Guid.NewGuid();
        private readonly Guid _referentialId = Guid.NewGuid();
        private Guid _studentSecurableDocumentUuid;

        [SetUp]
        public async Task SetUp()
        {
            await UpsertEducationOrganization("StateEducationAgency", SEA_ID, null);
            await UpsertEducationOrganization("LocalEducationAgency", LEA_ID, SEA_ID);
            await UpsertEducationOrganization("School", SCHOOL_ID, LEA_ID);
            await UpsertEducationOrganization("School", 77, null);

            UpsertResult.InsertSuccess ssdResult = (UpsertResult.InsertSuccess)
                await UpsertStudentSecurableDocument("0123");
            _studentSecurableDocumentUuid = ssdResult.NewDocumentUuid.Value;

            await UpsertStudentEducationOrganizationResponsibilityAssociation(
                _documentUuid,
                _referentialId,
                SCHOOL_ID,
                "0123"
            );
        }

        [Test, Order(1)]
        public async Task Then_SEORA_DocumentStudentEducationOrganizationResponsibilityAuthorizationEdOrgIds_Should_Have_EdOrg_Hierarchy()
        {
            string documentEdOrgIds = await GetDocumentStudentEdOrgResponsibilityAuthorizationIds(
                _studentSecurableDocumentUuid
            );

            ParseEducationOrganizationIds(documentEdOrgIds)
                .Should()
                .BeEquivalentTo([SEA_ID, LEA_ID, SCHOOL_ID]);
        }

        [Test, Order(2)]
        public async Task Then_Moving_SEORA_DocumentStudentEducationOrganizationResponsibilityAuthorizationEdOrgIds_Should_Have_New_EdOrg()
        {
            await UpdateStudentEducationOrganizationResponsibilityAssociation(
                _documentUuid,
                _referentialId,
                77,
                "0123"
            );
            string documentEdOrgIds = await GetDocumentStudentEdOrgResponsibilityAuthorizationIds(
                _studentSecurableDocumentUuid
            );

            ParseEducationOrganizationIds(documentEdOrgIds).Should().BeEquivalentTo([77]);
        }

        [Test, Order(3)]
        public async Task Then_Moving_SEORA_To_New_Student_DocumentStudentEducationOrganizationResponsibilityAuthorizationEdOrgIds_Should_Have_No_EdOrg()
        {
            await UpdateStudentEducationOrganizationResponsibilityAssociation(
                _documentUuid,
                _referentialId,
                77,
                "9999"
            );
            string documentEdOrgIds = await GetDocumentStudentEdOrgResponsibilityAuthorizationIds(
                _studentSecurableDocumentUuid
            );

            ParseEducationOrganizationIds(documentEdOrgIds).Should().BeEmpty();
        }
    }

    [TestFixture]
    public class Given_An_Update_Of_StudentSecurableDocument
        : StudentEducationOrganizationResponsibilityAssociationAuthorizationTests
    {
        private Guid _studentSecurableDocumentUuid = Guid.NewGuid();
        private Guid _studentSecurableRefId = Guid.NewGuid();

        [SetUp]
        public async Task SetUp()
        {
            await UpsertEducationOrganization("StateEducationAgency", SEA_ID, null);
            await UpsertEducationOrganization("LocalEducationAgency", LEA_ID, SEA_ID);
            await UpsertEducationOrganization("School", SCHOOL_ID, LEA_ID);
            await UpsertEducationOrganization("School", 111, null);

            await UpsertStudentEducationOrganizationResponsibilityAssociation(SCHOOL_ID, "0123");
            await UpsertStudentEducationOrganizationResponsibilityAssociation(111, "ABCD");

            await UpsertStudentSecurableDocument(
                _studentSecurableDocumentUuid,
                _studentSecurableRefId,
                "0123"
            );

            string documentEdOrgIds = await GetDocumentStudentEdOrgResponsibilityAuthorizationIds(
                _studentSecurableDocumentUuid
            );

            ParseEducationOrganizationIds(documentEdOrgIds)
                .Should()
                .BeEquivalentTo([SCHOOL_ID, LEA_ID, SEA_ID]);
        }

        [Test]
        public async Task Then_Updated_StudentSecurableDocument_Should_Have_Updated_DocumentStudentEducationOrganizationResponsibilityAuthorizationEdOrgIds()
        {
            await UpdateStudentSecurableDocument(
                _studentSecurableDocumentUuid,
                _studentSecurableRefId,
                "ABCD"
            );

            string documentEdOrgIds = await GetDocumentStudentEdOrgResponsibilityAuthorizationIds(
                _studentSecurableDocumentUuid
            );
            ParseEducationOrganizationIds(documentEdOrgIds).Should().BeEquivalentTo([111]);
        }
    }
}
