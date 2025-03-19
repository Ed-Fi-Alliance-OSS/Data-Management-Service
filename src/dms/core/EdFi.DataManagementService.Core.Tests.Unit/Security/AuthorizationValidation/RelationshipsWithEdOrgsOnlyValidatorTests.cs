// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Security;
using EdFi.DataManagementService.Core.Security.AuthorizationValidation;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Security.AuthorizationValidation;

public class RelationshipsWithEdOrgsOnlyValidatorTests
{
    [TestFixture]
    public class Given_Request_Has_No_EducationOrganizations
    {
        private AuthorizationResult? _expectedResult;

        [SetUp]
        public void Setup()
        {
            var validator = new RelationshipsWithEdOrgsOnlyValidator();
            _expectedResult = validator.ValidateAuthorization(
                new DocumentSecurityElements([], []),
                new ClientAuthorizations(
                    "",
                    "",
                    [new EducationOrganizationId(299501)],
                    [new NamespacePrefix("uri://namespace")]
                )
            );
        }

        [Test]
        public void Should_Return_Expected_AuthorizationResult()
        {
            _expectedResult.Should().NotBeNull();
            _expectedResult!.IsAuthorized.Should().BeFalse();
            _expectedResult!.ErrorMessage.Should().NotBeEmpty();
            _expectedResult!
                .ErrorMessage.Should()
                .Be(
                    "No 'EducationOrganizationIds' property could be found on the resource in order to perform authorization. Should a different authorization strategy be used?"
                );
        }
    }

    [TestFixture]
    public class Given_Claim_Has_No_EducationOrganizations
    {
        private AuthorizationResult? _expectedResult;

        [SetUp]
        public void Setup()
        {
            var validator = new RelationshipsWithEdOrgsOnlyValidator();
            _expectedResult = validator.ValidateAuthorization(
                new DocumentSecurityElements(
                    [],
                    [
                        new EducationOrganizationSecurityElement(
                            new ResourceName("School"),
                            new EducationOrganizationId(255901)
                        ),
                    ]
                ),
                new ClientAuthorizations("", "", [], [])
            );
        }

        [Test]
        public void Should_Return_Expected_AuthorizationResult()
        {
            _expectedResult.Should().NotBeNull();
            _expectedResult!.IsAuthorized.Should().BeFalse();
            _expectedResult!.ErrorMessage.Should().NotBeEmpty();
            _expectedResult!
                .ErrorMessage.Should()
                .Be(
                    $"The API client has been given permissions on a resource that uses the 'RelationshipsWithEdOrgsOnly' authorization strategy but the client doesn't have any education organizations assigned."
                );
        }
    }

    [TestFixture]
    public class Given_Matching_EducationOrganizations_Between_Request_And_Claim
    {
        private AuthorizationResult? _expectedResult;

        [SetUp]
        public void Setup()
        {
            var validator = new RelationshipsWithEdOrgsOnlyValidator();
            _expectedResult = validator.ValidateAuthorization(
                new DocumentSecurityElements(
                    [],
                    [
                        new EducationOrganizationSecurityElement(
                            new ResourceName("School"),
                            new EducationOrganizationId(255901)
                        ),
                    ]
                ),
                new ClientAuthorizations("", "", [new EducationOrganizationId(255901)], [])
            );
        }

        [Test]
        public void Should_Return_Success_AuthorizationResult()
        {
            _expectedResult.Should().NotBeNull();
            _expectedResult!.IsAuthorized.Should().BeTrue();
            _expectedResult!.ErrorMessage.Should().BeEmpty();
        }
    }

    [TestFixture]
    public class Given_Non_Matching_EducationOrganization_Between_Request_And_Claim
    {
        private AuthorizationResult? _expectedResult;

        [SetUp]
        public void Setup()
        {
            var validator = new RelationshipsWithEdOrgsOnlyValidator();
            _expectedResult = validator.ValidateAuthorization(
                new DocumentSecurityElements(
                    ["uri://not-matching/resource"],
                    [
                        new EducationOrganizationSecurityElement(
                            new ResourceName("School"),
                            new EducationOrganizationId(289766)
                        ),
                    ]
                ),
                new ClientAuthorizations(
                    "",
                    "",
                    [new EducationOrganizationId(2455)],
                    [new NamespacePrefix("uri://namespace")]
                )
            );
        }

        [Test]
        public void Should_Return_Expected_AuthorizationResult()
        {
            _expectedResult.Should().NotBeNull();
            _expectedResult!.IsAuthorized.Should().BeTrue();
            _expectedResult!.ErrorMessage.Should().BeEmpty();
        }
    }

    [TestFixture]
    public class Given_Non_Matching_EducationOrganizations_Between_Request_And_Claim
    {
        private AuthorizationResult? _expectedResult;

        [SetUp]
        public void Setup()
        {
            var validator = new RelationshipsWithEdOrgsOnlyValidator();
            _expectedResult = validator.ValidateAuthorization(
                new DocumentSecurityElements(
                    [],
                    [
                        new EducationOrganizationSecurityElement(
                            new ResourceName("School"),
                            new EducationOrganizationId(233)
                        ),
                        new EducationOrganizationSecurityElement(
                            new ResourceName("School"),
                            new EducationOrganizationId(244)
                        ),
                    ]
                ),
                new ClientAuthorizations(
                    "",
                    "",
                    [new EducationOrganizationId(566), new EducationOrganizationId(567)],
                    []
                )
            );
        }

        [Test]
        public void Should_Return_Expected_AuthorizationResult()
        {
            _expectedResult.Should().NotBeNull();
            //We do not compare EdOrgIds at this point. 
            _expectedResult!.IsAuthorized.Should().BeTrue();
            _expectedResult!.ErrorMessage.Should().BeEmpty();
        }
    }
}
