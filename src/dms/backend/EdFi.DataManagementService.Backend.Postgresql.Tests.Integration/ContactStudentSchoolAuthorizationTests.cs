// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Backend;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Postgresql.Tests.Integration;

public class ContactStudentSchoolAuthorizationTests : DatabaseIntegrationTestHelper
{
    protected const long SEA_ID = 9;
    protected const long LEA_ID = 99;
    protected const long SCHOOL_ID = 999;

    [TestFixture]
    public class Given_An_Upsert_Of_A_School_Student_Contact_StudentContactAssociation
        : ContactStudentSchoolAuthorizationTests
    {
        [SetUp]
        public async Task SetUp()
        {
            var docId = Guid.NewGuid();
            var referentialId = Guid.NewGuid();
            var edOrgResult = await UpsertEducationOrganization("School", SCHOOL_ID, null);
            var ssaResult = await UpsertStudentSchoolAssociation(SCHOOL_ID, "0123");
            var studentContactResult = await UpsertStudentContactAssociation(
                docId,
                referentialId,
                "0123",
                "0456"
            );

            edOrgResult.Should().BeOfType<UpsertResult.InsertSuccess>();
            ssaResult.Should().BeOfType<UpsertResult.InsertSuccess>();
            studentContactResult.Should().BeOfType<UpsertResult.InsertSuccess>();
        }

        [Test]
        public async Task Then_ContactStudentSchoolAuthorization_Should_Be_Populated()
        {
            // Act
            var authorizations = await GetAllContactStudentSchoolAuthorizations();

            // Assert
            authorizations.Should().NotBeNull();
            authorizations.Should().HaveCount(1);

            var authorization = authorizations[0];
            authorization.StudentUniqueId.Should().Be("0123");
            authorization.ContactUniqueId.Should().Be("0456");
            ParseEducationOrganizationIds(
                    authorization.ContactStudentSchoolAuthorizationEducationOrganizationIds
                )
                .Should()
                .BeEquivalentTo([SCHOOL_ID]);
            authorization.StudentContactAssociationId.Should().BeGreaterThan(0);
            authorization.StudentContactAssociationPartitionKey.Should().BeGreaterThanOrEqualTo(0);
            authorization.StudentSchoolAssociationId.Should().BeGreaterThan(0);
            authorization.StudentSchoolAssociationPartitionKey.Should().BeGreaterThanOrEqualTo(0);
        }
    }

    [TestFixture]
    public class Given_An_Upsert_Of_A_School_And_StudentContactAssociation_With_No_StudentSchoolAssociation
        : ContactStudentSchoolAuthorizationTests
    {
        [SetUp]
        public async Task SetUp()
        {
            var docId = Guid.NewGuid();
            var referentialId = Guid.NewGuid();
            var edOrgResult = await UpsertEducationOrganization("School", SCHOOL_ID, null);
            var studentContactResult = await UpsertStudentContactAssociation(
                docId,
                referentialId,
                "0123",
                "0456"
            );

            edOrgResult.Should().BeOfType<UpsertResult.InsertSuccess>();
            studentContactResult.Should().BeOfType<UpsertResult.InsertSuccess>();
        }

        [Test]
        public async Task Then_StudentSchoolAssociationAuthorization_Should_Be_Empty()
        {
            // Act
            var authorizations = await GetAllContactStudentSchoolAuthorizations();

            // Assert
            authorizations.Should().NotBeNull();
            authorizations.Should().HaveCount(0);
        }
    }

    [TestFixture]
    public class Given_An_Upsert_Of_A_School_Student_Multiple_Contact_StudentContactAssociation
        : ContactStudentSchoolAuthorizationTests
    {
        [SetUp]
        public async Task SetUp()
        {
            var edOrgResult = await UpsertEducationOrganization("School", SCHOOL_ID, null);
            var ssaResult = await UpsertStudentSchoolAssociation(SCHOOL_ID, "0123");
            var studentContact1Result = await UpsertStudentContactAssociation(
                Guid.NewGuid(),
                Guid.NewGuid(),
                "0123",
                "0456"
            );

            var studentContact2Result = await UpsertStudentContactAssociation(
                Guid.NewGuid(),
                Guid.NewGuid(),
                "0123",
                "0789"
            );

            edOrgResult.Should().BeOfType<UpsertResult.InsertSuccess>();
            ssaResult.Should().BeOfType<UpsertResult.InsertSuccess>();
            studentContact1Result.Should().BeOfType<UpsertResult.InsertSuccess>();
            studentContact2Result.Should().BeOfType<UpsertResult.InsertSuccess>();
        }

        [Test]
        public async Task Then_ContactStudentSchoolAuthorizations_Should_Be_Populated()
        {
            // Act
            var authorizations = await GetAllContactStudentSchoolAuthorizations();

            // Assert
            authorizations.Should().NotBeNull();
            authorizations.Should().HaveCount(2);

            var authorization1 = authorizations[0];
            authorization1.StudentUniqueId.Should().Be("0123");
            authorization1.ContactUniqueId.Should().Be("0456");
            ParseEducationOrganizationIds(
                    authorization1.ContactStudentSchoolAuthorizationEducationOrganizationIds
                )
                .Should()
                .BeEquivalentTo([SCHOOL_ID]);
            authorization1.StudentContactAssociationId.Should().BeGreaterThan(0);
            authorization1.StudentContactAssociationPartitionKey.Should().BeGreaterThanOrEqualTo(0);
            authorization1.StudentSchoolAssociationId.Should().BeGreaterThan(0);
            authorization1.StudentSchoolAssociationPartitionKey.Should().BeGreaterThanOrEqualTo(0);

            var authorization2 = authorizations[1];
            authorization2.StudentUniqueId.Should().Be("0123");
            authorization2.ContactUniqueId.Should().Be("0789");
            ParseEducationOrganizationIds(
                    authorization2.ContactStudentSchoolAuthorizationEducationOrganizationIds
                )
                .Should()
                .BeEquivalentTo([SCHOOL_ID]);
            authorization2.StudentContactAssociationId.Should().BeGreaterThan(0);
            authorization2.StudentContactAssociationPartitionKey.Should().BeGreaterThanOrEqualTo(0);
            authorization2.StudentSchoolAssociationId.Should().BeGreaterThan(0);
            authorization2.StudentSchoolAssociationPartitionKey.Should().BeGreaterThanOrEqualTo(0);

            // Comparing the StudentSchoolAssociationIds between the two authorizations
            authorization1.StudentSchoolAssociationId.Should().Be(authorization2.StudentSchoolAssociationId);
        }
    }

    [TestFixture]
    public class Given_An_Upsert_Of_Two_School_Student_StudentSchoolAssociation_One_Contact
        : ContactStudentSchoolAuthorizationTests
    {
        private readonly long school1Id = 888;
        private readonly long school2Id = 777;

        private readonly string student1Id = "0123";
        private readonly string student2Id = "0987";

        private readonly string contactUniqueId = "1111";
        private readonly Guid contactDocumentId = Guid.NewGuid();
        private readonly Guid sca1DocumentId = Guid.NewGuid();
        private readonly Guid sca2DocumentId = Guid.NewGuid();

        [SetUp]
        public async Task SetUp()
        {
            var edOrg1Result = await UpsertEducationOrganization("School", school1Id, null);
            var edOrg2Result = await UpsertEducationOrganization("School", school2Id, null);

            var ssa1Result = await UpsertStudentSchoolAssociation(school1Id, student1Id);
            var ssa2Result = await UpsertStudentSchoolAssociation(school2Id, student2Id);

            // Upsert a contact
            var contactReferentialId = Guid.NewGuid();
            var contactResult = await UpsertContact(contactDocumentId, contactReferentialId, contactUniqueId);

            var student1ContactResult = await UpsertStudentContactAssociation(
                sca1DocumentId,
                Guid.NewGuid(),
                student1Id,
                contactUniqueId
            );

            var student2ContactResult = await UpsertStudentContactAssociation(
                sca2DocumentId,
                Guid.NewGuid(),
                student2Id,
                contactUniqueId
            );

            edOrg1Result.Should().BeOfType<UpsertResult.InsertSuccess>();
            edOrg2Result.Should().BeOfType<UpsertResult.InsertSuccess>();

            ssa1Result.Should().BeOfType<UpsertResult.InsertSuccess>();
            ssa2Result.Should().BeOfType<UpsertResult.InsertSuccess>();

            contactResult.Should().BeOfType<UpsertResult.InsertSuccess>();

            student1ContactResult.Should().BeOfType<UpsertResult.InsertSuccess>();
            student2ContactResult.Should().BeOfType<UpsertResult.InsertSuccess>();
        }

        [Test]
        public async Task Then_ContactSecurables_And_EdOrgIds_Should_Be_Populated()
        {
            // Act
            var authorizations = await GetAllContactStudentSchoolAuthorizations();
            var securables = await GetAllContactSecurableDocuments();
            var edOrgIdsForContactSecurable = await GetDocumentContactStudentSchoolAuthorizationEdOrgIds(
                contactDocumentId
            );
            var sca1EdOrgIdForContactAndStudentSecurable =
                await GetDocumentContactStudentSchoolAuthorizationEdOrgIds(sca1DocumentId);

            var sca2EdOrgIdForContactAndStudentSecurable =
                await GetDocumentContactStudentSchoolAuthorizationEdOrgIds(sca2DocumentId);

            // Assert
            authorizations.Should().NotBeNull();
            authorizations.Should().HaveCount(2);

            var authorization1 = authorizations[0];
            authorization1.StudentUniqueId.Should().Be(student1Id);
            authorization1.ContactUniqueId.Should().Be(contactUniqueId);
            ParseEducationOrganizationIds(
                    authorization1.ContactStudentSchoolAuthorizationEducationOrganizationIds
                )
                .Should()
                .BeEquivalentTo([school1Id]);
            authorization1.StudentContactAssociationId.Should().BeGreaterThan(0);
            authorization1.StudentContactAssociationPartitionKey.Should().BeGreaterThanOrEqualTo(0);
            authorization1.StudentSchoolAssociationId.Should().BeGreaterThan(0);
            authorization1.StudentSchoolAssociationPartitionKey.Should().BeGreaterThanOrEqualTo(0);

            var authorization2 = authorizations[1];
            authorization2.StudentUniqueId.Should().Be(student2Id);
            authorization2.ContactUniqueId.Should().Be(contactUniqueId);
            ParseEducationOrganizationIds(
                    authorization2.ContactStudentSchoolAuthorizationEducationOrganizationIds
                )
                .Should()
                .BeEquivalentTo([school2Id]);
            authorization2.StudentContactAssociationId.Should().BeGreaterThan(0);
            authorization2.StudentContactAssociationPartitionKey.Should().BeGreaterThanOrEqualTo(0);
            authorization2.StudentSchoolAssociationId.Should().BeGreaterThan(0);
            authorization2.StudentSchoolAssociationPartitionKey.Should().BeGreaterThanOrEqualTo(0);

            // Comparing the StudentSchoolAssociationIds between the two authorizations
            authorization1
                .StudentSchoolAssociationId.Should()
                .NotBe(authorization2.StudentSchoolAssociationId);

            // Securables should 3, one for each contact and student contact association
            securables.Count.Should().Be(3);

            // EdOrgIds for the contact securable should be 2, one for each student school association
            edOrgIdsForContactSecurable.Should().NotBeNull();
            ParseEducationOrganizationIds(edOrgIdsForContactSecurable)
                .Should()
                .BeEquivalentTo([school1Id, school2Id]);

            // EdOrgIds for the contact and student securable should be 1, one for the student school association
            sca1EdOrgIdForContactAndStudentSecurable.Should().NotBeNull();
            ParseEducationOrganizationIds(sca1EdOrgIdForContactAndStudentSecurable)
                .Should()
                .BeEquivalentTo([school1Id, school2Id]);

            sca2EdOrgIdForContactAndStudentSecurable.Should().NotBeNull();
            ParseEducationOrganizationIds(sca2EdOrgIdForContactAndStudentSecurable)
                .Should()
                .BeEquivalentTo([school1Id, school2Id]);
        }
    }

    [TestFixture]
    public class Given_An_Upsert_Of_Two_School_Student_StudentSchoolAssociation_One_Contact_Delete_StudentSchoolAssociation
        : ContactStudentSchoolAuthorizationTests
    {
        private readonly long school1Id = 888;
        private readonly long school2Id = 777;

        private readonly string student1Id = "0123";
        private readonly string student2Id = "0987";

        private readonly string contactUniqueId = "1111";
        private readonly Guid contactDocumentId = Guid.NewGuid();
        private readonly Guid sca1DocumentId = Guid.NewGuid();
        private readonly Guid sca2DocumentId = Guid.NewGuid();

        [SetUp]
        public async Task SetUp()
        {
            var edOrg1Result = await UpsertEducationOrganization("School", school1Id, null);
            var edOrg2Result = await UpsertEducationOrganization("School", school2Id, null);

            var ssa1Result = await UpsertStudentSchoolAssociation(school1Id, student1Id);
            UpsertResult.InsertSuccess ssa2Result = (UpsertResult.InsertSuccess)
                await UpsertStudentSchoolAssociation(school2Id, student2Id);

            // Upsert a contact
            var contactReferentialId = Guid.NewGuid();
            var contactResult = await UpsertContact(contactDocumentId, contactReferentialId, contactUniqueId);

            var student1ContactResult = await UpsertStudentContactAssociation(
                sca1DocumentId,
                Guid.NewGuid(),
                student1Id,
                contactUniqueId
            );

            var student2ContactResult = await UpsertStudentContactAssociation(
                sca2DocumentId,
                Guid.NewGuid(),
                student2Id,
                contactUniqueId
            );

            edOrg1Result.Should().BeOfType<UpsertResult.InsertSuccess>();
            edOrg2Result.Should().BeOfType<UpsertResult.InsertSuccess>();
            ssa1Result.Should().BeOfType<UpsertResult.InsertSuccess>();
            ssa2Result.Should().BeOfType<UpsertResult.InsertSuccess>();
            contactResult.Should().BeOfType<UpsertResult.InsertSuccess>();

            student1ContactResult.Should().BeOfType<UpsertResult.InsertSuccess>();
            student2ContactResult.Should().BeOfType<UpsertResult.InsertSuccess>();

            var deleteResult = await DeleteStudentSchoolAssociation(ssa2Result.NewDocumentUuid.Value);
            deleteResult.Should().BeOfType<DeleteResult.DeleteSuccess>();
        }

        [Test]
        public async Task Then_Correct_EdOrgIds_Should_Be_Populated()
        {
            // Act
            var authorizations = await GetAllContactStudentSchoolAuthorizations();
            var securables = await GetAllContactSecurableDocuments();
            var edOrgIdsForContactSecurable = await GetDocumentContactStudentSchoolAuthorizationEdOrgIds(
                contactDocumentId
            );
            var sca1EdOrgIdForContactAndStudentSecurable =
                await GetDocumentContactStudentSchoolAuthorizationEdOrgIds(sca1DocumentId);

            var sca2EdOrgIdForContactAndStudentSecurable =
                await GetDocumentContactStudentSchoolAuthorizationEdOrgIds(sca2DocumentId);

            // Assert
            authorizations.Should().NotBeNull();
            authorizations.Should().HaveCount(1);

            var authorization1 = authorizations[0];
            authorization1.StudentUniqueId.Should().Be(student1Id);
            authorization1.ContactUniqueId.Should().Be(contactUniqueId);
            ParseEducationOrganizationIds(
                    authorization1.ContactStudentSchoolAuthorizationEducationOrganizationIds
                )
                .Should()
                .BeEquivalentTo([school1Id]);
            authorization1.StudentContactAssociationId.Should().BeGreaterThan(0);
            authorization1.StudentContactAssociationPartitionKey.Should().BeGreaterThanOrEqualTo(0);
            authorization1.StudentSchoolAssociationId.Should().BeGreaterThan(0);
            authorization1.StudentSchoolAssociationPartitionKey.Should().BeGreaterThanOrEqualTo(0);

            // Securables should 3, one for each contact and student contact association
            securables.Count.Should().Be(3);

            // EdOrgIds for the contact securable should be 1, one for each student school association
            edOrgIdsForContactSecurable.Should().NotBeNull();
            ParseEducationOrganizationIds(edOrgIdsForContactSecurable).Should().BeEquivalentTo([school1Id]);

            sca1EdOrgIdForContactAndStudentSecurable.Should().NotBeNull();
            ParseEducationOrganizationIds(sca1EdOrgIdForContactAndStudentSecurable)
                .Should()
                .BeEquivalentTo([school1Id]);

            sca2EdOrgIdForContactAndStudentSecurable.Should().NotBeNull();
            ParseEducationOrganizationIds(sca2EdOrgIdForContactAndStudentSecurable)
                .Should()
                .BeEquivalentTo([school1Id]);
        }
    }

    [TestFixture]
    public class Given_An_Upsert_Of_Two_School_Student_StudentSchoolAssociation_One_Contact_Delete_StudentContactAssociation
        : ContactStudentSchoolAuthorizationTests
    {
        private readonly long school1Id = 888;
        private readonly long school2Id = 777;

        private readonly string student1Id = "0123";
        private readonly string student2Id = "0987";

        private readonly string contactUniqueId = "1111";
        private readonly Guid contactDocumentId = Guid.NewGuid();
        private readonly Guid sca1DocumentId = Guid.NewGuid();
        private readonly Guid sca2DocumentId = Guid.NewGuid();

        [SetUp]
        public async Task SetUp()
        {
            var edOrg1Result = await UpsertEducationOrganization("School", school1Id, null);
            var edOrg2Result = await UpsertEducationOrganization("School", school2Id, null);

            var ssa1Result = await UpsertStudentSchoolAssociation(school1Id, student1Id);
            UpsertResult.InsertSuccess ssa2Result = (UpsertResult.InsertSuccess)
                await UpsertStudentSchoolAssociation(school2Id, student2Id);

            // Upsert a contact
            var contactReferentialId = Guid.NewGuid();
            var contactResult = await UpsertContact(contactDocumentId, contactReferentialId, contactUniqueId);

            var student1ContactResult = await UpsertStudentContactAssociation(
                sca1DocumentId,
                Guid.NewGuid(),
                student1Id,
                contactUniqueId
            );

            UpsertResult.InsertSuccess student2ContactResult = (UpsertResult.InsertSuccess)
                await UpsertStudentContactAssociation(
                    sca2DocumentId,
                    Guid.NewGuid(),
                    student2Id,
                    contactUniqueId
                );

            edOrg1Result.Should().BeOfType<UpsertResult.InsertSuccess>();
            edOrg2Result.Should().BeOfType<UpsertResult.InsertSuccess>();
            ssa1Result.Should().BeOfType<UpsertResult.InsertSuccess>();
            ssa2Result.Should().BeOfType<UpsertResult.InsertSuccess>();
            contactResult.Should().BeOfType<UpsertResult.InsertSuccess>();

            student1ContactResult.Should().BeOfType<UpsertResult.InsertSuccess>();
            student2ContactResult.Should().BeOfType<UpsertResult.InsertSuccess>();

            var deleteResult = await DeleteStudentContactAssociation(
                student2ContactResult.NewDocumentUuid.Value
            );
            deleteResult.Should().BeOfType<DeleteResult.DeleteSuccess>();
        }

        [Test]
        public async Task Then_Correct_EdOrgIds_Should_Be_Populated()
        {
            // Act
            var authorizations = await GetAllContactStudentSchoolAuthorizations();
            var securables = await GetAllContactSecurableDocuments();
            var edOrgIdsForContactSecurable = await GetDocumentContactStudentSchoolAuthorizationEdOrgIds(
                contactDocumentId
            );
            var sca1EdOrgIdForContactAndStudentSecurable =
                await GetDocumentContactStudentSchoolAuthorizationEdOrgIds(sca1DocumentId);

            // Assert
            authorizations.Should().NotBeNull();
            authorizations.Should().HaveCount(1);

            // Only one authorization should be present since the second student school association was deleted
            var authorization1 = authorizations[0];
            authorization1.StudentUniqueId.Should().Be(student1Id);
            authorization1.ContactUniqueId.Should().Be(contactUniqueId);
            ParseEducationOrganizationIds(
                    authorization1.ContactStudentSchoolAuthorizationEducationOrganizationIds
                )
                .Should()
                .BeEquivalentTo([school1Id]);
            authorization1.StudentContactAssociationId.Should().BeGreaterThan(0);
            authorization1.StudentContactAssociationPartitionKey.Should().BeGreaterThanOrEqualTo(0);
            authorization1.StudentSchoolAssociationId.Should().BeGreaterThan(0);
            authorization1.StudentSchoolAssociationPartitionKey.Should().BeGreaterThanOrEqualTo(0);

            // Securables should 2, one for each contact and student contact association
            securables.Count.Should().Be(2);

            // EdOrgIds for the contact securable should be 1
            edOrgIdsForContactSecurable.Should().NotBeNull();
            ParseEducationOrganizationIds(edOrgIdsForContactSecurable).Should().BeEquivalentTo([school1Id]);

            sca1EdOrgIdForContactAndStudentSecurable.Should().NotBeNull();
            ParseEducationOrganizationIds(sca1EdOrgIdForContactAndStudentSecurable)
                .Should()
                .BeEquivalentTo([school1Id]);
        }
    }

    [TestFixture]
    public class Given_An_Upsert_Of_Two_School_StudentSchoolAssociation_One_Student_Contact_Delete_StudentSchoolAssociation
        : ContactStudentSchoolAuthorizationTests
    {
        private readonly long school1Id = 888;
        private readonly long school2Id = 777;

        private readonly string student1Id = "0123";

        private readonly string contactUniqueId = "1111";
        private readonly Guid contactDocumentId = Guid.NewGuid();
        private readonly Guid sca1DocumentId = Guid.NewGuid();

        [SetUp]
        public async Task SetUp()
        {
            var edOrg1Result = await UpsertEducationOrganization("School", school1Id, null);
            var edOrg2Result = await UpsertEducationOrganization("School", school2Id, null);

            var ssa1Result = await UpsertStudentSchoolAssociation(school1Id, student1Id);
            var ssa2Result = await UpsertStudentSchoolAssociation(school2Id, student1Id);

            // Upsert a contact
            var contactReferentialId = Guid.NewGuid();
            var contactResult = await UpsertContact(contactDocumentId, contactReferentialId, contactUniqueId);

            var student1ContactResult = await UpsertStudentContactAssociation(
                sca1DocumentId,
                Guid.NewGuid(),
                student1Id,
                contactUniqueId
            );

            edOrg1Result.Should().BeOfType<UpsertResult.InsertSuccess>();
            edOrg2Result.Should().BeOfType<UpsertResult.InsertSuccess>();

            ssa1Result.Should().BeOfType<UpsertResult.InsertSuccess>();
            ssa2Result.Should().BeOfType<UpsertResult.InsertSuccess>();

            contactResult.Should().BeOfType<UpsertResult.InsertSuccess>();

            student1ContactResult.Should().BeOfType<UpsertResult.InsertSuccess>();

            var ssa2DeleteResult = await DeleteStudentSchoolAssociation(
                ((UpsertResult.InsertSuccess)ssa2Result).NewDocumentUuid.Value
            );
            ssa2DeleteResult.Should().BeOfType<DeleteResult.DeleteSuccess>();
        }

        [Test]
        public async Task Then_ContactSecurables_And_EdOrgIds_Should_Be_Populated()
        {
            // Act
            var authorizations = await GetAllContactStudentSchoolAuthorizations();
            var securables = await GetAllContactSecurableDocuments();
            var edOrgIdsForContactSecurable = await GetDocumentContactStudentSchoolAuthorizationEdOrgIds(
                contactDocumentId
            );
            var sca1EdOrgIdForContactAndStudentSecurable =
                await GetDocumentContactStudentSchoolAuthorizationEdOrgIds(sca1DocumentId);

            // Assert
            authorizations.Should().NotBeNull();
            authorizations.Should().HaveCount(1);

            var authorization1 = authorizations[0];
            authorization1.StudentUniqueId.Should().Be(student1Id);
            authorization1.ContactUniqueId.Should().Be(contactUniqueId);
            ParseEducationOrganizationIds(
                    authorization1.ContactStudentSchoolAuthorizationEducationOrganizationIds
                )
                .Should()
                .BeEquivalentTo([school1Id]);
            authorization1.StudentContactAssociationId.Should().BeGreaterThan(0);
            authorization1.StudentContactAssociationPartitionKey.Should().BeGreaterThanOrEqualTo(0);
            authorization1.StudentSchoolAssociationId.Should().BeGreaterThan(0);
            authorization1.StudentSchoolAssociationPartitionKey.Should().BeGreaterThanOrEqualTo(0);

            // Securables should be 2, one for the contact and one for the student contact association
            securables.Count.Should().Be(2);

            // EdOrgIds for the contact securable should be 1 (the student school association)
            edOrgIdsForContactSecurable.Should().NotBeNull();
            ParseEducationOrganizationIds(edOrgIdsForContactSecurable).Should().BeEquivalentTo([school1Id]);

            // EdOrgIds for the student contact association should be 1 (the student school association)
            sca1EdOrgIdForContactAndStudentSecurable.Should().NotBeNull();
            ParseEducationOrganizationIds(sca1EdOrgIdForContactAndStudentSecurable)
                .Should()
                .BeEquivalentTo([school1Id]);
        }
    }

    [TestFixture]
    public class Given_An_Upsert_Of_One_School_StudentSchoolAssociation_One_Student_Contact_Recreate_StudentSchoolAssociation
        : ContactStudentSchoolAuthorizationTests
    {
        private readonly long school1Id = 888;

        private readonly string student1Id = "0123";

        private readonly string contactUniqueId = "1111";
        private readonly Guid contactDocumentId = Guid.NewGuid();
        private readonly Guid sca1DocumentId = Guid.NewGuid();

        [SetUp]
        public async Task SetUp()
        {
            var edOrg1Result = await UpsertEducationOrganization("School", school1Id, null);

            var ssa1Result = await UpsertStudentSchoolAssociation(school1Id, student1Id);

            // Upsert a contact
            var contactReferentialId = Guid.NewGuid();
            var contactResult = await UpsertContact(contactDocumentId, contactReferentialId, contactUniqueId);

            var student1ContactResult = await UpsertStudentContactAssociation(
                sca1DocumentId,
                Guid.NewGuid(),
                student1Id,
                contactUniqueId
            );

            edOrg1Result.Should().BeOfType<UpsertResult.InsertSuccess>();

            ssa1Result.Should().BeOfType<UpsertResult.InsertSuccess>();

            contactResult.Should().BeOfType<UpsertResult.InsertSuccess>();

            student1ContactResult.Should().BeOfType<UpsertResult.InsertSuccess>();

            var ssa2DeleteResult = await DeleteStudentSchoolAssociation(
                ((UpsertResult.InsertSuccess)ssa1Result).NewDocumentUuid.Value
            );
            ssa2DeleteResult.Should().BeOfType<DeleteResult.DeleteSuccess>();

            // Recreate SSA
            ssa1Result = await UpsertStudentSchoolAssociation(school1Id, student1Id);
            ssa1Result.Should().BeOfType<UpsertResult.InsertSuccess>();
        }

        [Test]
        public async Task Then_ContactSecurables_And_EdOrgIds_Should_Be_Populated()
        {
            // Act
            var authorizations = await GetAllContactStudentSchoolAuthorizations();
            var securables = await GetAllContactSecurableDocuments();
            var edOrgIdsForContactSecurable = await GetDocumentContactStudentSchoolAuthorizationEdOrgIds(
                contactDocumentId
            );
            var sca1EdOrgIdForContactAndStudentSecurable =
                await GetDocumentContactStudentSchoolAuthorizationEdOrgIds(sca1DocumentId);

            // Assert
            authorizations.Should().NotBeNull();
            authorizations.Should().HaveCount(1);

            var authorization1 = authorizations[0];
            authorization1.StudentUniqueId.Should().Be(student1Id);
            authorization1.ContactUniqueId.Should().Be(contactUniqueId);
            ParseEducationOrganizationIds(
                    authorization1.ContactStudentSchoolAuthorizationEducationOrganizationIds
                )
                .Should()
                .BeEquivalentTo([school1Id]);
            authorization1.StudentContactAssociationId.Should().BeGreaterThan(0);
            authorization1.StudentContactAssociationPartitionKey.Should().BeGreaterThanOrEqualTo(0);
            authorization1.StudentSchoolAssociationId.Should().BeGreaterThan(0);
            authorization1.StudentSchoolAssociationPartitionKey.Should().BeGreaterThanOrEqualTo(0);

            // Securables should be 2, one for the contact and one for the student contact association
            securables.Count.Should().Be(2);

            // EdOrgIds for the contact securable should be 1 (the student school association)
            edOrgIdsForContactSecurable.Should().NotBeNull();
            ParseEducationOrganizationIds(edOrgIdsForContactSecurable).Should().BeEquivalentTo([school1Id]);

            // EdOrgIds for the student contact association should be 1 (the student school association)
            sca1EdOrgIdForContactAndStudentSecurable.Should().NotBeNull();
            ParseEducationOrganizationIds(sca1EdOrgIdForContactAndStudentSecurable)
                .Should()
                .BeEquivalentTo([school1Id]);
        }
    }

    [TestFixture]
    public class Given_An_Upsert_Of_Two_School_StudentSchoolAssociation_One_Student_Contact_Update_StudentSchoolAssociation_School
        : ContactStudentSchoolAuthorizationTests
    {
        private readonly long school1Id = 777;
        private readonly long school2Id = 888;
        private readonly long school3Id = 999;

        private readonly string student1Id = "0123";

        private readonly string contactUniqueId = "1111";
        private readonly Guid contactDocumentId = Guid.NewGuid();
        private readonly Guid sca1DocumentId = Guid.NewGuid();
        private readonly Guid ssa2DocumentId = Guid.NewGuid();
        private readonly Guid ssa2ReferentialId = Guid.NewGuid();

        [SetUp]
        public async Task SetUp()
        {
            var edOrg1Result = await UpsertEducationOrganization("School", school1Id, null);
            var edOrg2Result = await UpsertEducationOrganization("School", school2Id, null);
            var edOrg3Result = await UpsertEducationOrganization("School", school3Id, null);

            var ssa1Result = await UpsertStudentSchoolAssociation(school1Id, student1Id);

            var ssa2Result = await UpsertStudentSchoolAssociation(
                ssa2DocumentId,
                ssa2ReferentialId,
                school2Id,
                student1Id
            );

            // Upsert a contact
            var contactResult = await UpsertContact(contactDocumentId, Guid.NewGuid(), contactUniqueId);

            var student1ContactResult = await UpsertStudentContactAssociation(
                sca1DocumentId,
                Guid.NewGuid(),
                student1Id,
                contactUniqueId
            );

            edOrg1Result.Should().BeOfType<UpsertResult.InsertSuccess>();
            edOrg2Result.Should().BeOfType<UpsertResult.InsertSuccess>();
            edOrg3Result.Should().BeOfType<UpsertResult.InsertSuccess>();

            ssa1Result.Should().BeOfType<UpsertResult.InsertSuccess>();
            ssa2Result.Should().BeOfType<UpsertResult.InsertSuccess>();

            contactResult.Should().BeOfType<UpsertResult.InsertSuccess>();

            student1ContactResult.Should().BeOfType<UpsertResult.InsertSuccess>();

            var ssa2UpdateResult = await UpdateStudentSchoolAssociation(
                ssa2DocumentId,
                ssa2ReferentialId,
                school3Id,
                student1Id
            );
            ssa2UpdateResult.Should().BeOfType<UpdateResult.UpdateSuccess>();
        }

        [Test]
        public async Task Then_ContactSecurables_And_EdOrgIds_Should_Be_Populated()
        {
            // Act
            var authorizations = await GetAllContactStudentSchoolAuthorizations();
            var securables = await GetAllContactSecurableDocuments();
            var edOrgIdsForContactSecurable = await GetDocumentContactStudentSchoolAuthorizationEdOrgIds(
                contactDocumentId
            );
            var sca1EdOrgIdForContactAndStudentSecurable =
                await GetDocumentContactStudentSchoolAuthorizationEdOrgIds(sca1DocumentId);

            // Assert
            authorizations.Should().NotBeNull();
            authorizations.Should().HaveCount(2);

            var authorization1 = authorizations[0];
            authorization1.StudentUniqueId.Should().Be(student1Id);
            authorization1.ContactUniqueId.Should().Be(contactUniqueId);
            ParseEducationOrganizationIds(
                    authorization1.ContactStudentSchoolAuthorizationEducationOrganizationIds
                )
                .Should()
                .BeEquivalentTo([school1Id]);
            authorization1.StudentContactAssociationId.Should().BeGreaterThan(0);
            authorization1.StudentContactAssociationPartitionKey.Should().BeGreaterThanOrEqualTo(0);
            authorization1.StudentSchoolAssociationId.Should().BeGreaterThan(0);
            authorization1.StudentSchoolAssociationPartitionKey.Should().BeGreaterThanOrEqualTo(0);

            var authorization2 = authorizations[1];
            authorization2.StudentUniqueId.Should().Be(student1Id);
            authorization2.ContactUniqueId.Should().Be(contactUniqueId);
            ParseEducationOrganizationIds(
                    authorization2.ContactStudentSchoolAuthorizationEducationOrganizationIds
                )
                .Should()
                .BeEquivalentTo([school3Id]);
            authorization2.StudentContactAssociationId.Should().BeGreaterThan(0);
            authorization2.StudentContactAssociationPartitionKey.Should().BeGreaterThanOrEqualTo(0);
            authorization2.StudentSchoolAssociationId.Should().BeGreaterThan(0);
            authorization2.StudentSchoolAssociationPartitionKey.Should().BeGreaterThanOrEqualTo(0);

            // Securables should be 2, one for the contact and one for the student contact association
            securables.Count.Should().Be(2);

            // EdOrgIds for the contact securable should be 2
            edOrgIdsForContactSecurable.Should().NotBeNull();
            ParseEducationOrganizationIds(edOrgIdsForContactSecurable)
                .Should()
                .BeEquivalentTo([school1Id, school3Id]);

            // EdOrgIds for the student contact association should be 2
            sca1EdOrgIdForContactAndStudentSecurable.Should().NotBeNull();
            ParseEducationOrganizationIds(sca1EdOrgIdForContactAndStudentSecurable)
                .Should()
                .BeEquivalentTo([school1Id, school3Id]);
        }
    }

    [TestFixture]
    public class Given_An_Upsert_Of_Two_School_StudentSchoolAssociation_One_Student_Contact_Update_StudentSchoolAssociation_Student
        : ContactStudentSchoolAuthorizationTests
    {
        private readonly long school1Id = 777;

        private readonly string student1Id = "0123";
        private readonly string student2Id = "0124";

        private readonly string contact1UniqueId = "0123";
        private readonly string contact2UniqueId = "0124";
        private readonly Guid contact1DocumentId = Guid.NewGuid();
        private readonly Guid contact2DocumentId = Guid.NewGuid();
        private readonly Guid ssa1DocumentId = Guid.NewGuid();
        private readonly Guid ssa1ReferentialId = Guid.NewGuid();
        private readonly Guid ssa2DocumentId = Guid.NewGuid();

        [SetUp]
        public async Task SetUp()
        {
            var edOrg1Result = await UpsertEducationOrganization("School", school1Id, null);

            var ssa1Result = await UpsertStudentSchoolAssociation(
                ssa1DocumentId,
                ssa1ReferentialId,
                school1Id,
                student1Id
            );

            var ssa2Result = await UpsertStudentSchoolAssociation(
                ssa2DocumentId,
                Guid.NewGuid(),
                school1Id,
                student2Id
            );

            var contact1Result = await UpsertContact(contact1DocumentId, Guid.NewGuid(), contact1UniqueId);
            var contact2Result = await UpsertContact(contact2DocumentId, Guid.NewGuid(), contact2UniqueId);

            var student1Contact1Result = await UpsertStudentContactAssociation(
                Guid.NewGuid(),
                Guid.NewGuid(),
                student1Id,
                contact1UniqueId
            );

            var student2Contact2Result = await UpsertStudentContactAssociation(
                Guid.NewGuid(),
                Guid.NewGuid(),
                student2Id,
                contact2UniqueId
            );

            edOrg1Result.Should().BeOfType<UpsertResult.InsertSuccess>();

            ssa1Result.Should().BeOfType<UpsertResult.InsertSuccess>();
            ssa2Result.Should().BeOfType<UpsertResult.InsertSuccess>();

            contact1Result.Should().BeOfType<UpsertResult.InsertSuccess>();
            contact2Result.Should().BeOfType<UpsertResult.InsertSuccess>();

            student1Contact1Result.Should().BeOfType<UpsertResult.InsertSuccess>();
            student2Contact2Result.Should().BeOfType<UpsertResult.InsertSuccess>();

            var ssa1UpdateResult = await UpdateStudentSchoolAssociation(
                ssa1DocumentId,
                ssa1ReferentialId,
                school1Id,
                student2Id
            );
            ssa1UpdateResult.Should().BeOfType<UpdateResult.UpdateSuccess>();
        }

        [Test]
        public async Task Then_ContactSecurables_And_EdOrgIds_Should_Be_Populated()
        {
            // Act
            var authorizations = await GetAllContactStudentSchoolAuthorizations();
            var edOrgIdsForContact1Securable = await GetDocumentContactStudentSchoolAuthorizationEdOrgIds(
                contact1DocumentId
            );
            var edOrgIdsForContact2Securable = await GetDocumentContactStudentSchoolAuthorizationEdOrgIds(
                contact2DocumentId
            );

            // Assert
            authorizations.Should().NotBeNull();
            authorizations.Should().HaveCount(3); // Expecting 3 authorizations in total

            // First authorization: contact2 linked to student2 and school1
            var authorization1 = authorizations[0];
            authorization1.StudentUniqueId.Should().Be(student2Id);
            authorization1.ContactUniqueId.Should().Be(contact2UniqueId);
            ParseEducationOrganizationIds(
                    authorization1.ContactStudentSchoolAuthorizationEducationOrganizationIds
                )
                .Should()
                .BeEquivalentTo([school1Id]);

            // Second authorization: contact1 linked to student1, but should have no EdOrg access
            var authorization2 = authorizations[1];
            authorization2.StudentUniqueId.Should().Be(student1Id);
            authorization2.ContactUniqueId.Should().Be(contact1UniqueId);
            ParseEducationOrganizationIds(
                    authorization2.ContactStudentSchoolAuthorizationEducationOrganizationIds
                )
                .Should()
                .BeEmpty();

            // Third authorization: duplicate of first, should match student2/contact2/school1
            var authorization3 = authorizations[2];
            authorization3.StudentUniqueId.Should().Be(student2Id);
            authorization3.ContactUniqueId.Should().Be(contact2UniqueId);
            ParseEducationOrganizationIds(
                    authorization3.ContactStudentSchoolAuthorizationEducationOrganizationIds
                )
                .Should()
                .BeEquivalentTo([school1Id]);

            // Both first and third authorizations should relate to the same student contact association
            authorization1
                .StudentContactAssociationId.Should()
                .Be(authorization3.StudentContactAssociationId);

            // Contact1 securables should be empty
            edOrgIdsForContact1Securable.Should().NotBeNull();
            ParseEducationOrganizationIds(edOrgIdsForContact1Securable).Should().BeEmpty();

            // Contact2 securables should include school1
            edOrgIdsForContact2Securable.Should().NotBeNull();
            ParseEducationOrganizationIds(edOrgIdsForContact2Securable).Should().BeEquivalentTo([school1Id]);
        }

        [Test]
        public async Task When_SSA_Is_Deleted_Then_ContactSecurables_And_EdOrgIds_Should_Be_Populated()
        {
            // Act
            var ssa2DeleteResult = await DeleteStudentSchoolAssociation(ssa2DocumentId);
            ssa2DeleteResult.Should().BeOfType<DeleteResult.DeleteSuccess>();

            var authorizations = await GetAllContactStudentSchoolAuthorizations();
            var edOrgIdsForContact1Securable = await GetDocumentContactStudentSchoolAuthorizationEdOrgIds(
                contact1DocumentId
            );
            var edOrgIdsForContact2Securable = await GetDocumentContactStudentSchoolAuthorizationEdOrgIds(
                contact2DocumentId
            );

            // Assert
            authorizations.Should().NotBeNull();
            authorizations.Should().HaveCount(2);

            // First authorization: contact1/student1 - still valid, no EdOrg access
            var authorization1 = authorizations[0];
            authorization1.StudentUniqueId.Should().Be(student1Id);
            authorization1.ContactUniqueId.Should().Be(contact1UniqueId);
            ParseEducationOrganizationIds(
                    authorization1.ContactStudentSchoolAuthorizationEducationOrganizationIds
                )
                .Should()
                .BeEmpty();

            // Second authorization: contact2/student2 - still linked and has access to school1
            var authorization2 = authorizations[1];
            authorization2.StudentUniqueId.Should().Be(student2Id);
            authorization2.ContactUniqueId.Should().Be(contact2UniqueId);
            ParseEducationOrganizationIds(
                    authorization2.ContactStudentSchoolAuthorizationEducationOrganizationIds
                )
                .Should()
                .BeEquivalentTo([school1Id]);

            // Contact1 still has no access
            edOrgIdsForContact1Securable.Should().NotBeNull();
            ParseEducationOrganizationIds(edOrgIdsForContact1Securable).Should().BeEmpty();

            // Contact2 retains access to school1 through one active authorization
            edOrgIdsForContact2Securable.Should().NotBeNull();
            ParseEducationOrganizationIds(edOrgIdsForContact2Securable).Should().BeEquivalentTo([school1Id]);
        }
    }
}
