// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Interface;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Security.AuthorizationValidation;
using FakeItEasy;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Security.AuthorizationValidation;

public class RelationshipsWithEdOrgsOnlyValidatorTests
{
    private readonly IAuthorizationRepository _authorizationRepository = A.Fake<IAuthorizationRepository>();

    [TestFixture]
    public class Given_Request_Has_No_EducationOrganizations : RelationshipsWithEdOrgsOnlyValidatorTests
    {
        private ResourceAuthorizationResult? _expectedResult;

        [SetUp]
        public async Task Setup()
        {
            var validator = new RelationshipsWithEdOrgsOnlyValidator(_authorizationRepository);
            _expectedResult = await validator.ValidateAuthorization(
                new DocumentSecurityElements([], [], []),
                [new AuthorizationFilter(SecurityElementNameConstants.EducationOrganization, "299501")],
                [],
                OperationType.Get,
                new TraceId("trace-id")
            );
        }

        [Test]
        public void Should_Return_Expected_AuthorizationResult()
        {
            _expectedResult.Should().NotBeNull();
            _expectedResult!.GetType().Should().Be(typeof(ResourceAuthorizationResult.NotAuthorized));
            if (_expectedResult is ResourceAuthorizationResult.NotAuthorized notAuthorized)
            {
                notAuthorized!.ErrorMessages.Should().HaveCount(1);
                notAuthorized!
                    .ErrorMessages[0]
                    .Should()
                    .Be(
                        "No 'EducationOrganizationIds' property could be found on the resource in order to perform authorization. Should a different authorization strategy be used?"
                    );
            }
        }
    }

    [TestFixture]
    public class Given_Matching_EducationOrganizations_Between_Request_And_Claim
        : RelationshipsWithEdOrgsOnlyValidatorTests
    {
        private ResourceAuthorizationResult? _expectedResult;

        [SetUp]
        public async Task Setup()
        {
            A.CallTo(
                    () =>
                        _authorizationRepository.GetAncestorEducationOrganizationIds(
                            A<long[]>.Ignored,
                            A<TraceId>.Ignored
                        )
                )
                .Returns([255901L]);
            var validator = new RelationshipsWithEdOrgsOnlyValidator(_authorizationRepository);
            _expectedResult = await validator.ValidateAuthorization(
                new DocumentSecurityElements(
                    [],
                    [
                        new EducationOrganizationSecurityElement(
                            new ResourceName("School"),
                            new EducationOrganizationId(255901)
                        ),
                    ],
                    []
                ),
                [new AuthorizationFilter(SecurityElementNameConstants.EducationOrganization, "255901")],
                [],
                OperationType.Get,
                new TraceId("trace-id")
            );
        }

        [Test]
        public void Should_Return_Success_AuthorizationResult()
        {
            _expectedResult.Should().NotBeNull();
            _expectedResult!.GetType().Should().Be(typeof(ResourceAuthorizationResult.Authorized));
        }
    }

    [TestFixture]
    public class Given_Non_Matching_EducationOrganization_Between_Get_Request_And_Claim
        : RelationshipsWithEdOrgsOnlyValidatorTests
    {
        private ResourceAuthorizationResult? _expectedResult;

        [SetUp]
        public async Task Setup()
        {
            A.CallTo(
                    () =>
                        _authorizationRepository.GetAncestorEducationOrganizationIds(
                            A<long[]>.Ignored,
                            A<TraceId>.Ignored
                        )
                )
                .Returns([289766L]);
            var validator = new RelationshipsWithEdOrgsOnlyValidator(_authorizationRepository);
            _expectedResult = await validator.ValidateAuthorization(
                new DocumentSecurityElements(
                    ["uri://not-matching/resource"],
                    [
                        new EducationOrganizationSecurityElement(
                            new ResourceName("School"),
                            new EducationOrganizationId(289766)
                        ),
                    ],
                    []
                ),
                [new AuthorizationFilter(SecurityElementNameConstants.EducationOrganization, "2455")],
                [],
                OperationType.Get,
                new TraceId("trace-id")
            );
        }

        [Test]
        public void Should_Return_Expected_AuthorizationResult()
        {
            _expectedResult!.GetType().Should().Be(typeof(ResourceAuthorizationResult.NotAuthorized));
            if (_expectedResult is ResourceAuthorizationResult.NotAuthorized notAuthorized)
            {
                notAuthorized!.ErrorMessages.Should().HaveCount(1);
                notAuthorized!
                    .ErrorMessages[0]
                    .Should()
                    .Be(
                        "Access to the resource item could not be authorized based on the caller's EducationOrganizationIds claims: '2455'."
                    );
            }
        }
    }

    [TestFixture]
    public class Given_Non_Matching_EducationOrganizations_Between_Upsert_Request_And_Claim
        : RelationshipsWithEdOrgsOnlyValidatorTests
    {
        private ResourceAuthorizationResult? _expectedResult;

        [SetUp]
        public async Task Setup()
        {
            A.CallTo(
                    () =>
                        _authorizationRepository.GetAncestorEducationOrganizationIds(
                            A<long[]>.Ignored,
                            A<TraceId>.Ignored
                        )
                )
                .Returns([233L, 244L]);

            var validator = new RelationshipsWithEdOrgsOnlyValidator(_authorizationRepository);
            _expectedResult = await validator.ValidateAuthorization(
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
                    ],
                    []
                ),
                [new AuthorizationFilter(SecurityElementNameConstants.EducationOrganization, "567")],
                [],
                OperationType.Upsert,
                new TraceId("trace-id")
            );
        }

        [Test]
        public void Should_Return_Expected_AuthorizationResult()
        {
            _expectedResult!.GetType().Should().Be(typeof(ResourceAuthorizationResult.NotAuthorized));
            if (_expectedResult is ResourceAuthorizationResult.NotAuthorized notAuthorized)
            {
                notAuthorized!.ErrorMessages.Should().HaveCount(1);
                notAuthorized!
                    .ErrorMessages[0]
                    .Should()
                    .Be(
                        "No relationships have been established between the caller's education organization id claims ('567') and properties of the resource item."
                    );
            }
        }
    }

    [TestFixture]
    public class Given_EducationOrganizations_Have_Child_Relationship_With_EdOrgId_From_Claim
        : RelationshipsWithEdOrgsOnlyValidatorTests
    {
        private ResourceAuthorizationResult? _expectedResult;

        [SetUp]
        public async Task Setup()
        {
            A.CallTo(
                    () =>
                        _authorizationRepository.GetAncestorEducationOrganizationIds(
                            A<long[]>.Ignored,
                            A<TraceId>.Ignored
                        )
                )
                .Returns([299L, 255901L]);
            var validator = new RelationshipsWithEdOrgsOnlyValidator(_authorizationRepository);
            _expectedResult = await validator.ValidateAuthorization(
                new DocumentSecurityElements(
                    [],
                    [
                        new EducationOrganizationSecurityElement(
                            new ResourceName("School"),
                            new EducationOrganizationId(255901)
                        ),
                    ],
                    []
                ),
                [new AuthorizationFilter(SecurityElementNameConstants.EducationOrganization, "299")],
                [],
                OperationType.Get,
                new TraceId("trace-id")
            );
        }

        [Test]
        public void Should_Return_Success_AuthorizationResult()
        {
            _expectedResult.Should().NotBeNull();
            _expectedResult!.GetType().Should().Be(typeof(ResourceAuthorizationResult.Authorized));
        }
    }
}
