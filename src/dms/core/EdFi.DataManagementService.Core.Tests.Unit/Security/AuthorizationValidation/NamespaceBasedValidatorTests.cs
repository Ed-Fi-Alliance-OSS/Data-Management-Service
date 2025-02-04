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

public class NamespaceBasedValidatorTests
{
    [TestFixture]
    public class Given_Request_Has_No_Namespaces
    {
        private AuthorizationResult? _expectedResult;

        [SetUp]
        public void Setup()
        {
            var validator = new NamespaceBasedValidator();
            _expectedResult = validator.ValidateAuthorization(
                new DocumentSecurityElements([]),
                new ApiClientDetails("", "", [], ["uri://namespace"])
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
                    "No 'Namespace' (or Namespace-suffixed) property could be found on the resource in order to perform authorization. Should a different authorization strategy be used?"
                );
        }
    }

    [TestFixture]
    public class Given_Claim_Has_No_NamespacePrefixes
    {
        private AuthorizationResult? _expectedResult;

        [SetUp]
        public void Setup()
        {
            var validator = new NamespaceBasedValidator();
            _expectedResult = validator.ValidateAuthorization(
                new DocumentSecurityElements(["uri://namespace/resource"]),
                new ApiClientDetails("", "", [], [])
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
                    "The API client has been given permissions on a resource that uses the 'NamespaceBased' authorization strategy but the client doesn't have any namespace prefixes assigned."
                );
        }
    }

    [TestFixture]
    public class Given_Matching_NamespacePrefix_And_Namespace
    {
        private AuthorizationResult? _expectedResult;

        [SetUp]
        public void Setup()
        {
            var validator = new NamespaceBasedValidator();
            _expectedResult = validator.ValidateAuthorization(
                new DocumentSecurityElements(["uri://namespace/resource"]),
                new ApiClientDetails("", "", [], ["uri://namespace"])
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
    public class Given_Non_Matching_NamespacePrefix_And_Namespace
    {
        private AuthorizationResult? _expectedResult;

        [SetUp]
        public void Setup()
        {
            var validator = new NamespaceBasedValidator();
            _expectedResult = validator.ValidateAuthorization(
                new DocumentSecurityElements(["uri://not-matching/resource"]),
                new ApiClientDetails("", "", [], ["uri://namespace"])
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
                    "The 'Namespace' value of the data does not start with any of the caller's associated namespace prefixes ('uri://namespace')."
                );
        }
    }

    [TestFixture]
    public class Given_Non_Matching_NamespacePrefixes_And_Namespaces
    {
        private AuthorizationResult? _expectedResult;

        [SetUp]
        public void Setup()
        {
            var validator = new NamespaceBasedValidator();
            _expectedResult = validator.ValidateAuthorization(
                new DocumentSecurityElements(["uri://matching/resource", "uri://not-matching1/resource"]),
                new ApiClientDetails("", "", [], ["uri://matching", "uri://matching1"])
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
                    "The 'Namespace' value of the data does not start with any of the caller's associated namespace prefixes ('uri://matching', 'uri://matching1')."
                );
        }
    }
}
