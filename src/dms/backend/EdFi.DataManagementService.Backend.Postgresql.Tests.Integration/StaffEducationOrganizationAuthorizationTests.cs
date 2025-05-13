// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Backend;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Postgresql.Tests.Integration;

public class StaffEducationOrganizationAuthorizationTests : DatabaseIntegrationTestHelper
{
    protected const long SEA_ID = 9;
    protected const long LEA_ID = 99;
    protected const long ED_ORG_ID = 999;

    [TestFixture]
    public class Given_An_Upsert_Of_One_EdOrg_One_StaffEducationOrganization
        : StaffEducationOrganizationAuthorizationTests
    {
        [SetUp]
        public async Task SetUp()
        {
            var edOrgResult = await UpsertEducationOrganization("EducationOrganization", ED_ORG_ID, null);
            var seoResult = await UpsertStaffEducationOrganizationEmploymentAssociation(ED_ORG_ID, "5678");

            edOrgResult.Should().BeOfType<UpsertResult.InsertSuccess>();
            seoResult.Should().BeOfType<UpsertResult.InsertSuccess>();
        }

        [Test]
        public async Task Then_StaffEducationOrganizationAuthorization_Should_Be_Populated()
        {
            // Act
            var authorizations = await GetAllStaffEducationOrganizationAuthorizations();

            // Assert
            authorizations.Should().NotBeNull();
            authorizations.Should().HaveCount(1);

            var authorization = authorizations[0];
            authorization.StaffUniqueId.Should().Be("5678");
            authorization.HierarchyEdOrgId.Should().Be(ED_ORG_ID);
            ParseEducationOrganizationIds(authorization.StaffEducationOrganizationAuthorizationEdOrgIds)
                .Should()
                .BeEquivalentTo([ED_ORG_ID]);
            authorization.StaffEducationOrganizationId.Should().BeGreaterThan(0);
            authorization.StaffEducationOrganizationPartitionKey.Should().BeGreaterThanOrEqualTo(0);
        }
    }

    [TestFixture]
    public class Given_An_Upsert_Of_EdOrg_Hierarchy_One_StaffEducationOrganization
        : StaffEducationOrganizationAuthorizationTests
    {
        [SetUp]
        public async Task SetUp()
        {
            await UpsertEducationOrganization("StateEducationAgency", SEA_ID, null);
            await UpsertEducationOrganization("LocalEducationAgency", LEA_ID, SEA_ID);
            await UpsertEducationOrganization("EducationOrganization", ED_ORG_ID, LEA_ID);
            await UpsertStaffEducationOrganizationEmploymentAssociation(ED_ORG_ID, "5678");
        }

        [Test]
        public async Task Then_StaffEducationOrganizationAuthorization_Should_Be_Populated()
        {
            // Act
            var authorizations = await GetAllStaffEducationOrganizationAuthorizations();

            // Assert
            authorizations.Should().NotBeNull();
            authorizations.Should().HaveCount(1);

            var authorization = authorizations[0];
            authorization.StaffUniqueId.Should().Be("5678");
            authorization.HierarchyEdOrgId.Should().Be(ED_ORG_ID);
            ParseEducationOrganizationIds(authorization.StaffEducationOrganizationAuthorizationEdOrgIds)
                .Should()
                .BeEquivalentTo([SEA_ID, LEA_ID, ED_ORG_ID]);
            authorization.StaffEducationOrganizationId.Should().BeGreaterThan(0);
            authorization.StaffEducationOrganizationPartitionKey.Should().BeGreaterThanOrEqualTo(0);
        }
    }

    [TestFixture]
    public class Given_An_Upsert_Of_EdOrg_Hierarchy_With_Update_One_StaffEducationOrganization
        : StaffEducationOrganizationAuthorizationTests
    {
        private readonly Guid _edOrgUuid = Guid.NewGuid();
        private readonly Guid _edOrgRefId = Guid.NewGuid();

        [SetUp]
        public async Task SetUp()
        {
            await UpsertEducationOrganization("StateEducationAgency", SEA_ID, null);
            await UpsertEducationOrganization("LocalEducationAgency", LEA_ID, SEA_ID);

            // Insert without parentId
            await UpsertEducationOrganization(
                _edOrgUuid,
                _edOrgRefId,
                "EducationOrganization",
                ED_ORG_ID,
                null
            );
            await UpsertStaffEducationOrganizationEmploymentAssociation(ED_ORG_ID, "5678");
        }

        [Test, Order(1)]
        public async Task Then_StaffEducationOrganizationAuthorization_Should_Have_One_EdOrg()
        {
            var authorizations = await GetAllStaffEducationOrganizationAuthorizations();
            authorizations.Should().NotBeNull();
            authorizations.Should().HaveCount(1);

            var authorization = authorizations[0];
            authorization.HierarchyEdOrgId.Should().Be(ED_ORG_ID);
            ParseEducationOrganizationIds(authorization.StaffEducationOrganizationAuthorizationEdOrgIds)
                .Should()
                .BeEquivalentTo([ED_ORG_ID]);
        }

        [Test, Order(2)]
        public async Task Then_StaffEducationOrganizationAuthorization_Should_Have_Three_EdOrg()
        {
            // Move EdOrg under LEA
            var updateResult = await UpdateEducationOrganization(
                _edOrgUuid,
                _edOrgRefId,
                "EducationOrganization",
                ED_ORG_ID,
                LEA_ID
            );
            updateResult.Should().BeOfType<UpdateResult.UpdateSuccess>();

            var authorizations = await GetAllStaffEducationOrganizationAuthorizations();
            authorizations.Should().NotBeNull();
            authorizations.Should().HaveCount(1);

            var authorization = authorizations[0];
            authorization.HierarchyEdOrgId.Should().Be(ED_ORG_ID);
            ParseEducationOrganizationIds(authorization.StaffEducationOrganizationAuthorizationEdOrgIds)
                .Should()
                .BeEquivalentTo([SEA_ID, LEA_ID, ED_ORG_ID]);
        }
    }

    [TestFixture]
    public class Given_An_Upsert_Of_EdOrg_Hierarchy_With_Move_One_StaffEducationOrganization
        : StaffEducationOrganizationAuthorizationTests
    {
        private readonly Guid _documentUuid = Guid.NewGuid();
        private readonly Guid _referentialId = Guid.NewGuid();

        [SetUp]
        public async Task SetUp()
        {
            await UpsertEducationOrganization("StateEducationAgency", SEA_ID, null);
            await UpsertEducationOrganization("LocalEducationAgency", LEA_ID, SEA_ID);
            await UpsertEducationOrganization("EducationOrganization", ED_ORG_ID, LEA_ID);

            await UpsertStaffEducationOrganizationEmploymentAssociation(
                _documentUuid,
                _referentialId,
                ED_ORG_ID,
                "5678"
            );
        }

        [Test, Order(1)]
        public async Task Then_StaffEducationOrganizationAuthorization_Should_Have_EdOrg_Hierarchy()
        {
            var authorizations = await GetAllStaffEducationOrganizationAuthorizations();
            authorizations.Should().NotBeNull();
            authorizations.Should().HaveCount(1);

            var authorization = authorizations[0];
            authorization.HierarchyEdOrgId.Should().Be(ED_ORG_ID);
            ParseEducationOrganizationIds(authorization.StaffEducationOrganizationAuthorizationEdOrgIds)
                .Should()
                .BeEquivalentTo([SEA_ID, LEA_ID, ED_ORG_ID]);
        }

        [Test, Order(2)]
        public async Task Then_Moving_StaffEducationOrganizationAuthorization_Should_Have_New_EdOrg()
        {
            await UpdateStaffEducationOrganizationEmploymentAssociation(
                _documentUuid,
                _referentialId,
                "5678",
                LEA_ID
            );

            var authorizations = await GetAllStaffEducationOrganizationAuthorizations();
            authorizations.Should().NotBeNull();
            authorizations.Should().HaveCount(1);

            var authorization = authorizations[0];
            authorization.HierarchyEdOrgId.Should().Be(LEA_ID);
            ParseEducationOrganizationIds(authorization.StaffEducationOrganizationAuthorizationEdOrgIds)
                .Should()
                .BeEquivalentTo([SEA_ID, LEA_ID]);
        }
    }

    [TestFixture]
    public class Given_An_Upsert_Of_One_StaffSecurableDocument : StaffEducationOrganizationAuthorizationTests
    {
        private UpsertResult staffSecurableDocumentResult;

        [SetUp]
        public async Task SetUp()
        {
            var edOrgResult = await UpsertEducationOrganization("EducationOrganization", ED_ORG_ID, null);
            var seoResult = await UpsertStaffEducationOrganizationEmploymentAssociation(ED_ORG_ID, "5678");
            staffSecurableDocumentResult = await UpsertStaffSecurableDocument("5678");

            edOrgResult.Should().BeOfType<UpsertResult.InsertSuccess>();
            seoResult.Should().BeOfType<UpsertResult.InsertSuccess>();
            staffSecurableDocumentResult.Should().BeOfType<UpsertResult.InsertSuccess>();
        }

        [Test, Order(1)]
        public async Task Then_StaffEducationOrganizationAuthorization_Should_Be_Populated()
        {
            var authorizations = await GetAllStaffEducationOrganizationAuthorizations();

            authorizations.Should().NotBeNull();
            authorizations.Should().HaveCount(1);

            var authorization = authorizations[0];
            authorization.StaffUniqueId.Should().Be("5678");
            authorization.HierarchyEdOrgId.Should().Be(ED_ORG_ID);
            ParseEducationOrganizationIds(authorization.StaffEducationOrganizationAuthorizationEdOrgIds)
                .Should()
                .BeEquivalentTo([ED_ORG_ID]);
        }

        [Test, Order(2)]
        public async Task Then_StaffSecurableDocuments_Should_Be_Populated()
        {
            var documents = await GetAllStaffSecurableDocuments();

            documents.Should().NotBeNull();
            documents.Should().HaveCount(2);

            documents.TrueForAll(d => d.StaffUniqueId == "5678").Should().BeTrue();
        }

        [Test, Order(3)]
        public async Task Then_Document_StaffEducationOrganizationAuthorizationEdOrgIds_Should_Be_Populated()
        {
            string idsString = await GetDocumentStaffEducationOrganizationAuthorizationEdOrgIds(
                ((UpsertResult.InsertSuccess)staffSecurableDocumentResult).NewDocumentUuid.Value
            );
            ParseEducationOrganizationIds(idsString).Should().BeEquivalentTo([ED_ORG_ID]);
        }

        [Test, Order(4)]
        public async Task Then_Deleting_StaffSecurableDocument_Should_Cascade()
        {
            Guid staffSecurableDocumentId = ((UpsertResult.InsertSuccess)staffSecurableDocumentResult)
                .NewDocumentUuid
                .Value;
            IDeleteRequest deleteRequest = CreateDeleteRequest(
                "StaffSecurableDocument",
                staffSecurableDocumentId
            );
            await CreateDeleteById().DeleteById(deleteRequest, Connection!, Transaction!);

            var documents = await GetAllStaffSecurableDocuments();

            // Assert
            documents.Should().NotBeNull();
            documents.Should().HaveCount(1);
        }
    }

    [TestFixture]
    public class Given_An_Update_Of_StaffSecurableDocument : StaffEducationOrganizationAuthorizationTests
    {
        private Guid _staffSecurableDocumentUuid = Guid.NewGuid();
        private Guid _staffSecurableRefId = Guid.NewGuid();

        [SetUp]
        public async Task SetUp()
        {
            await UpsertEducationOrganization("StateEducationAgency", SEA_ID, null);
            await UpsertEducationOrganization("LocalEducationAgency", LEA_ID, SEA_ID);
            await UpsertEducationOrganization("EducationOrganization", ED_ORG_ID, LEA_ID);
            await UpsertEducationOrganization("EducationOrganization", 111, null);

            await UpsertStaffEducationOrganizationEmploymentAssociation(ED_ORG_ID, "5678");
            await UpsertStaffEducationOrganizationEmploymentAssociation(111, "ABCD");

            await UpsertStaffSecurableDocument(_staffSecurableDocumentUuid, _staffSecurableRefId, "5678");

            string documentEdOrgIds = await GetDocumentStaffEducationOrganizationAuthorizationEdOrgIds(
                _staffSecurableDocumentUuid
            );

            ParseEducationOrganizationIds(documentEdOrgIds)
                .Should()
                .BeEquivalentTo([ED_ORG_ID, LEA_ID, SEA_ID]);
        }

        [Test]
        public async Task Then_Updated_StaffSecurableDocument_Should_Have_Updated_DocumentStaffEducationOrganizationAuthorizationEdOrgIds()
        {
            await UpdateStaffSecurableDocument(_staffSecurableDocumentUuid, _staffSecurableRefId, "ABCD");

            string documentEdOrgIds = await GetDocumentStaffEducationOrganizationAuthorizationEdOrgIds(
                _staffSecurableDocumentUuid
            );
            ParseEducationOrganizationIds(documentEdOrgIds).Should().BeEquivalentTo([111]);
        }
    }
}
