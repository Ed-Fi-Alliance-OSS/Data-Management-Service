// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Interface;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Security.AuthorizationValidation;
using FakeItEasy;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Security.AuthorizationValidation;

[TestFixture]
public class RelationshipsWithEdOrgsAndPeopleValidatorTests
{
    private readonly IAuthorizationRepository _authorizationRepository;
    private readonly RelationshipsWithEdOrgsAndPeopleValidator _validator;

    public RelationshipsWithEdOrgsAndPeopleValidatorTests()
    {
        _authorizationRepository = A.Fake<IAuthorizationRepository>();
        _validator = new RelationshipsWithEdOrgsAndPeopleValidator(_authorizationRepository);
    }

    [TestFixture]
    public class Given_Request_Has_No_Student_Info : RelationshipsWithEdOrgsAndPeopleValidatorTests
    {
        private ResourceAuthorizationResult? _expectedResult;

        [SetUp]
        public async Task Setup()
        {
            var securityElements = new DocumentSecurityElements([], [], []);
            var authorizationFilters = Array.Empty<AuthorizationFilter>();
            var authorizationSecurableInfo = new AuthorizationSecurableInfo(
                SecurityElementNameConstants.StudentUniqueId
            );
            _expectedResult = await _validator!.ValidateAuthorization(
                securityElements,
                authorizationFilters,
                [authorizationSecurableInfo],
                OperationType.Get
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
                        "No 'Student' property could be found on the resource in order to perform authorization. Should a different authorization strategy be used?"
                    );
            }
        }
    }

    [TestFixture]
    public class Given_Resource_With_Student_Reference_Has_No_Associated_EdOrgs
        : RelationshipsWithEdOrgsAndPeopleValidatorTests
    {
        private ResourceAuthorizationResult? _expectedResult;

        [SetUp]
        public async Task Setup()
        {
            var securityElements = new DocumentSecurityElements([], [], [new StudentUniqueId("12345")]);

            var authorizationFilters = new[] { new AuthorizationFilter("EducationOrganization", "255901") };

            var authorizationSecurableInfo = new AuthorizationSecurableInfo(
                SecurityElementNameConstants.StudentUniqueId
            );

            A.CallTo(() => _authorizationRepository.GetEducationOrganizationsForStudent("12345"))
                .Returns(JsonSerializer.SerializeToElement(Array.Empty<long>()));

            _expectedResult = await _validator.ValidateAuthorization(
                securityElements,
                authorizationFilters,
                [authorizationSecurableInfo],
                OperationType.Get
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
                        "No relationships have been established between the caller's education organization id claims ('255901') and the resource item's 'StudentUniqueId' value."
                    );
            }
        }
    }

    [TestFixture]
    public class Given_Resource_With_Student_Reference_Has_Matching_EdOrg
        : RelationshipsWithEdOrgsAndPeopleValidatorTests
    {
        private ResourceAuthorizationResult? _expectedResult;

        [SetUp]
        public async Task Setup()
        {
            var securityElements = new DocumentSecurityElements([], [], [new StudentUniqueId("12345")]);

            var authorizationFilters = new[] { new AuthorizationFilter("EducationOrganization", "255901") };

            var authorizationSecurableInfo = new AuthorizationSecurableInfo(
                SecurityElementNameConstants.StudentUniqueId
            );

            A.CallTo(() => _authorizationRepository.GetEducationOrganizationsForStudent("12345"))
                .Returns(JsonSerializer.SerializeToElement(new[] { 255901L }));

            _expectedResult = await _validator.ValidateAuthorization(
                securityElements,
                authorizationFilters,
                [authorizationSecurableInfo],
                OperationType.Get
            );
        }

        [Test]
        public void Should_Return_Expected_AuthorizationResult()
        {
            _expectedResult.Should().NotBeNull();
            _expectedResult!.GetType().Should().Be(typeof(ResourceAuthorizationResult.Authorized));
        }
    }

    [TestFixture]
    public class Given_Resource_Is_Not_Student_Securable : RelationshipsWithEdOrgsAndPeopleValidatorTests
    {
        private ResourceAuthorizationResult? _expectedResult;

        [SetUp]
        public async Task Setup()
        {
            var securityElements = new DocumentSecurityElements([], [], [new StudentUniqueId("12345")]);

            var authorizationFilters = new[] { new AuthorizationFilter("EducationOrganization", "255901") };

            A.CallTo(() => _authorizationRepository.GetEducationOrganizationsForStudent("12345"))
                .Returns(JsonSerializer.SerializeToElement(new[] { 255901L }));

            _expectedResult = await _validator.ValidateAuthorization(
                securityElements,
                authorizationFilters,
                [],
                OperationType.Get
            );
        }

        [Test]
        public void Should_Return_Expected_AuthorizationResult()
        {
            _expectedResult.Should().NotBeNull();
            _expectedResult!.GetType().Should().Be(typeof(ResourceAuthorizationResult.Authorized));
        }
    }

    [TestFixture]
    public class Given_Resource_With_Student_Reference_Has_No_Matching_EdOrg
        : RelationshipsWithEdOrgsAndPeopleValidatorTests
    {
        private ResourceAuthorizationResult? _expectedResult;

        [SetUp]
        public async Task Setup()
        {
            var securityElements = new DocumentSecurityElements([], [], [new StudentUniqueId("12345")]);

            var authorizationFilters = new[] { new AuthorizationFilter("EducationOrganization", "255903") };

            var authorizationSecurableInfo = new AuthorizationSecurableInfo(
                SecurityElementNameConstants.StudentUniqueId
            );

            A.CallTo(() => _authorizationRepository.GetEducationOrganizationsForStudent("12345"))
                .Returns(JsonSerializer.SerializeToElement(new[] { 255901L }));

            _expectedResult = await _validator.ValidateAuthorization(
                securityElements,
                authorizationFilters,
                [authorizationSecurableInfo],
                OperationType.Get
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
                        "No relationships have been established between the caller's education organization id claims ('255903') and the resource item's 'StudentUniqueId' value."
                    );
            }
        }
    }
}
