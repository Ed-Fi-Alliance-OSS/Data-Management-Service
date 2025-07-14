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

public class RelationshipsWithStudentsOnlyValidatorTests
{
    private readonly IAuthorizationRepository _authorizationRepository = A.Fake<IAuthorizationRepository>();

    [TestFixture]
    [Parallelizable]
    public class Given_Request_Has_No_Student_Info : RelationshipsWithStudentsOnlyValidatorTests
    {
        private ResourceAuthorizationResult? _expectedResult;

        [SetUp]
        public async Task Setup()
        {
            var validator = new RelationshipsWithStudentsOnlyValidator(_authorizationRepository);
            _expectedResult = await validator.ValidateAuthorization(
                new DocumentSecurityElements([], [], [], [], []),
                [new AuthorizationFilter.EducationOrganization("255901")],
                [new AuthorizationSecurableInfo("StudentUniqueId")],
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
                        "No 'StudentUniqueId' property could be found on the resource in order to perform authorization. Should a different authorization strategy be used?"
                    );
            }
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Matching_Student_Between_Request_And_Claim
        : RelationshipsWithStudentsOnlyValidatorTests
    {
        private ResourceAuthorizationResult? _expectedResult;

        [SetUp]
        public async Task Setup()
        {
            A.CallTo(() => _authorizationRepository.GetEducationOrganizationsForStudent("12345"))
                .Returns([255901L]);
            var validator = new RelationshipsWithStudentsOnlyValidator(_authorizationRepository);
            _expectedResult = await validator.ValidateAuthorization(
                new DocumentSecurityElements([], [], [new StudentUniqueId("12345")], [], []),
                [new AuthorizationFilter.EducationOrganization("255901")],
                [new AuthorizationSecurableInfo("StudentUniqueId")],
                OperationType.Get
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
    [Parallelizable]
    public class Given_Non_Matching_Student_Between_Request_And_Claim
        : RelationshipsWithStudentsOnlyValidatorTests
    {
        private ResourceAuthorizationResult? _expectedResult;

        [SetUp]
        public async Task Setup()
        {
            A.CallTo(() => _authorizationRepository.GetEducationOrganizationsForStudent("12345"))
                .Returns([255902L]);
            var validator = new RelationshipsWithStudentsOnlyValidator(_authorizationRepository);
            _expectedResult = await validator.ValidateAuthorization(
                new DocumentSecurityElements([], [], [new StudentUniqueId("12345")], [], []),
                [new AuthorizationFilter.EducationOrganization("255901")],
                [new AuthorizationSecurableInfo("StudentUniqueId")],
                OperationType.Get
            );
        }

        [Test]
        public void Should_Return_Expected_AuthorizationResult()
        {
            _expectedResult!
                .GetType()
                .Should()
                .Be(typeof(ResourceAuthorizationResult.NotAuthorized.WithHint));
            if (_expectedResult is ResourceAuthorizationResult.NotAuthorized.WithHint notAuthorized)
            {
                notAuthorized!.ErrorMessages.Should().HaveCount(1);
                notAuthorized!
                    .ErrorMessages[0]
                    .Should()
                    .Be(
                        "No relationships have been established between the caller's education organization id claims ('255901') and the resource item's StudentUniqueId value."
                    );
                notAuthorized!
                    .Hints[0]
                    .Should()
                    .Be("Hint: You may need to create a corresponding 'StudentSchoolAssociation' item.");
            }
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_No_Student_Securable_Info : RelationshipsWithStudentsOnlyValidatorTests
    {
        private ResourceAuthorizationResult? _expectedResult;

        [SetUp]
        public async Task Setup()
        {
            var validator = new RelationshipsWithStudentsOnlyValidator(_authorizationRepository);
            _expectedResult = await validator.ValidateAuthorization(
                new DocumentSecurityElements([], [], [new StudentUniqueId("12345")], [], []),
                [new AuthorizationFilter.EducationOrganization("255901")],
                [], // No securable info
                OperationType.Get
            );
        }

        [Test]
        public void Should_Return_Authorized_Result()
        {
            _expectedResult.Should().NotBeNull();
            _expectedResult!.GetType().Should().Be(typeof(ResourceAuthorizationResult.Authorized));
        }
    }
}
