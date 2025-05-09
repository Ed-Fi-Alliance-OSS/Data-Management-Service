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
}
