// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Security.AuthorizationValidation;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Security.AuthorizationValidation;

public class NamespaceBasedValidatorTests
{
    [TestFixture]
    public class Given_Request_Has_No_Namespaces
    {
        private ResourceAuthorizationResult? _expectedResult;

        [SetUp]
        public async Task Setup()
        {
            var validator = new NamespaceBasedValidator();
            _expectedResult = await validator.ValidateAuthorization(
                new DocumentSecurityElements([], [], [], [], []),
                [new AuthorizationFilter(SecurityElementNameConstants.Namespace, "uri://namespace")],
                [],
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
                        "No 'Namespace' (or Namespace-suffixed) property could be found on the resource in order to perform authorization. Should a different authorization strategy be used?"
                    );
            }
        }
    }

    [TestFixture]
    public class Given_Matching_NamespacePrefix_And_Namespace
    {
        private ResourceAuthorizationResult? _expectedResult;

        [SetUp]
        public async Task Setup()
        {
            var validator = new NamespaceBasedValidator();
            _expectedResult = await validator.ValidateAuthorization(
                new DocumentSecurityElements(["uri://namespace/resource"], [], [], [], []),
                [new AuthorizationFilter(SecurityElementNameConstants.Namespace, "uri://namespace")],
                [],
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
    public class Given_Non_Matching_NamespacePrefix_And_Namespace
    {
        private ResourceAuthorizationResult? _expectedResult;

        [SetUp]
        public async Task Setup()
        {
            var validator = new NamespaceBasedValidator();
            _expectedResult = await validator.ValidateAuthorization(
                new DocumentSecurityElements(["uri://not-matching/resource"], [], [], [], []),
                [new AuthorizationFilter(SecurityElementNameConstants.Namespace, "uri://namespace")],
                [],
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
                        "Access to the resource item could not be authorized based on the caller's NamespacePrefix claims: 'uri://namespace'."
                    );
            }
        }
    }

    [TestFixture]
    public class Given_Non_Matching_NamespacePrefixes_And_Namespaces
    {
        private ResourceAuthorizationResult? _expectedResult;

        [SetUp]
        public async Task Setup()
        {
            var validator = new NamespaceBasedValidator();
            _expectedResult = await validator.ValidateAuthorization(
                new DocumentSecurityElements(
                    ["uri://matching/resource", "uri://not-matching1/resource"],
                    [],
                    [],
                    [],
                    []
                ),
                [
                    new AuthorizationFilter(SecurityElementNameConstants.Namespace, "uri://matching"),
                    new AuthorizationFilter(SecurityElementNameConstants.Namespace, "uri://matching1"),
                ],
                [],
                OperationType.Get
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
                        "Access to the resource item could not be authorized based on the caller's NamespacePrefix claims: 'uri://matching', 'uri://matching1'."
                    );
            }
        }
    }
}
