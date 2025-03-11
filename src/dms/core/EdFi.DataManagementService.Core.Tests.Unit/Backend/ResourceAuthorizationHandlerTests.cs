// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Backend;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Backend;

[TestFixture]
public class ResourceAuthorizationHandlerTests
{
    [TestFixture("uri://ed-fi.org")]
    [TestFixture("uri://ed-fi.org,uri://test.org")]
    [TestFixture("uri://test.org,uri://ed-fi.org")]
    public class Given_An_EdFi_Doc_With_Matching_ClientAuthorization_Namespace(string clientNamespacePrefixes)
        : ResourceAuthorizationHandlerTests
    {
        private ResourceAuthorizationResult? _resourceAuthorizationResult;

        [SetUp]
        public void Setup()
        {
            string[] namespaceSecurityElements = ["uri://ed-fi.org"];

            var authStrategyEvaluators = clientNamespacePrefixes
                .Split(',')
                .Select(namespacePrefix => new AuthorizationStrategyEvaluator(
                    [new AuthorizationFilter("Namespace", namespacePrefix, "", FilterComparison.StartsWith)],
                    FilterOperator.Or
                ))
                .ToArray();

            var handler = new ResourceAuthorizationHandler(authStrategyEvaluators, NullLogger.Instance);
            _resourceAuthorizationResult = handler.Authorize(namespaceSecurityElements, []);
        }

        [Test]
        public void Result_should_be_authorized()
        {
            _resourceAuthorizationResult.Should().BeOfType<ResourceAuthorizationResult.Authorized>();
        }
    }

    [TestFixture("uri://ed-fi.org")]
    [TestFixture("uri://ed-fi.org,uri://test.org")]
    [TestFixture("uri://test.org,uri://ed-fi.org")]
    public class Given_An_EdFi_Doc_With_No_Matching_ClientAuthorization_Namespace(
        string clientNamespacePrefixes
    ) : ResourceAuthorizationHandlerTests
    {
        private ResourceAuthorizationResult? _resourceAuthorizationResult;

        [SetUp]
        public void Setup()
        {
            string[] namespaceSecurityElements = ["uri://i-match-nothing.org"];

            var authStrategyEvaluators = clientNamespacePrefixes
                .Split(',')
                .Select(namespacePrefix => new AuthorizationStrategyEvaluator(
                    [new AuthorizationFilter("Namespace", namespacePrefix, "", FilterComparison.StartsWith)],
                    FilterOperator.Or
                ))
                .ToArray();

            var handler = new ResourceAuthorizationHandler(authStrategyEvaluators, NullLogger.Instance);
            _resourceAuthorizationResult = handler.Authorize(namespaceSecurityElements, []);
        }

        [Test]
        public void Result_should_be_authorized()
        {
            _resourceAuthorizationResult.Should().BeOfType<ResourceAuthorizationResult.NotAuthorized>();
        }
    }

    [TestFixture]
    public class Given_An_EdFi_Doc_With_No_ClientAuthorization_Namespace() : ResourceAuthorizationHandlerTests
    {
        private ResourceAuthorizationResult? _resourceAuthorizationResult;

        [SetUp]
        public void Setup()
        {
            string[] namespaceSecurityElements = ["uri://i-match-nothing.org"];

            var authStrategyEvaluators = ""
                .Split(',')
                .Select(namespacePrefix => new AuthorizationStrategyEvaluator(
                    [new AuthorizationFilter("Namespace", namespacePrefix, "", FilterComparison.StartsWith)],
                    FilterOperator.Or
                ))
                .ToArray();

            var handler = new ResourceAuthorizationHandler(authStrategyEvaluators, NullLogger.Instance);
            _resourceAuthorizationResult = handler.Authorize(namespaceSecurityElements, []);
        }

        [Test]
        public void Result_should_be_authorized()
        {
            _resourceAuthorizationResult.Should().BeOfType<ResourceAuthorizationResult.Authorized>();
        }
    }

    [TestFixture("6001")]
    [TestFixture("6001,7001")]
    [TestFixture("7001,6001")]
    public class Given_An_EdFi_Doc_With_Matching_ClientAuthorization_EdOrg(string clientEdOrgIds)
        : ResourceAuthorizationHandlerTests
    {
        private ResourceAuthorizationResult? _resourceAuthorizationResult;

        [SetUp]
        public void Setup()
        {
            long[] edOrgSecurityElements = [6001];

            var authStrategyEvaluators = clientEdOrgIds
                .Split(',')
                .Select(edOrgId => new AuthorizationStrategyEvaluator(
                    [new AuthorizationFilter("EducationOrganization", edOrgId, "", FilterComparison.Equals)],
                    FilterOperator.Or
                ))
                .ToArray();

            var handler = new ResourceAuthorizationHandler(authStrategyEvaluators, NullLogger.Instance);
            _resourceAuthorizationResult = handler.Authorize([], edOrgSecurityElements);
        }

        [Test]
        public void Result_should_be_authorized()
        {
            _resourceAuthorizationResult.Should().BeOfType<ResourceAuthorizationResult.Authorized>();
        }
    }

    [TestFixture("6001")]
    [TestFixture("6001,7001")]
    [TestFixture("7001,6001")]
    public class Given_An_EdFi_Doc_With_No_Matching_ClientAuthorization_EdOrg(string clientEdOrgIds)
        : ResourceAuthorizationHandlerTests
    {
        private ResourceAuthorizationResult? _resourceAuthorizationResult;

        [SetUp]
        public void Setup()
        {
            long[] edOrgSecurityElements = [9999];

            var authStrategyEvaluators = clientEdOrgIds
                .Split(',')
                .Select(edOrgId => new AuthorizationStrategyEvaluator(
                    [new AuthorizationFilter("EducationOrganization", edOrgId, "", FilterComparison.Equals)],
                    FilterOperator.Or
                ))
                .ToArray();

            var handler = new ResourceAuthorizationHandler(authStrategyEvaluators, NullLogger.Instance);
            _resourceAuthorizationResult = handler.Authorize([], edOrgSecurityElements);
        }

        [Test]
        public void Result_should_be_not_authorized()
        {
            _resourceAuthorizationResult.Should().BeOfType<ResourceAuthorizationResult.NotAuthorized>();
        }
    }

    [TestFixture]
    public class Given_An_EdFi_Doc_With_No_ClientAuthorization_EdOrg() : ResourceAuthorizationHandlerTests
    {
        private ResourceAuthorizationResult? _resourceAuthorizationResult;

        [SetUp]
        public void Setup()
        {
            long[] edOrgSecurityElements = [6001];

            var authStrategyEvaluators = new AuthorizationStrategyEvaluator[0];

            var handler = new ResourceAuthorizationHandler(authStrategyEvaluators, NullLogger.Instance);
            _resourceAuthorizationResult = handler.Authorize([], edOrgSecurityElements);
        }

        [Test]
        public void Result_should_be_authorized()
        {
            _resourceAuthorizationResult.Should().BeOfType<ResourceAuthorizationResult.Authorized>();
        }
    }

    [TestFixture]
    public class Given_An_EdFi_Doc_With_Both_Namespace_And_EdOrg_Authorizations()
        : ResourceAuthorizationHandlerTests
    {
        private ResourceAuthorizationResult? _namespaceAndEdOrgMatch;
        private ResourceAuthorizationResult? _namespaceMatchEdOrgNoMatch;
        private ResourceAuthorizationResult? _namespaceNoMatchEdOrgMatch;
        private ResourceAuthorizationResult? _neitherMatch;

        [SetUp]
        public void Setup()
        {
            string[] namespaceElements = ["uri://ed-fi.org"];
            long[] edOrgElements = [6001];
            string[] nonMatchingNamespaceElements = ["uri://i-match-nothing.org"];
            long[] nonMatchingEdOrgElements = [9999];

            // Create evaluators with both namespace and edorg filters
            var evaluators = new[]
            {
                new AuthorizationStrategyEvaluator(
                    [
                        new AuthorizationFilter(
                            "Namespace",
                            "uri://ed-fi.org",
                            "",
                            FilterComparison.StartsWith
                        ),
                        new AuthorizationFilter("EducationOrganization", "6001", "", FilterComparison.Equals),
                    ],
                    FilterOperator.And
                ),
            };

            var handler = new ResourceAuthorizationHandler(evaluators, NullLogger.Instance);
            _namespaceAndEdOrgMatch = handler.Authorize(namespaceElements, edOrgElements);
            _namespaceMatchEdOrgNoMatch = handler.Authorize(namespaceElements, nonMatchingEdOrgElements);
            _namespaceNoMatchEdOrgMatch = handler.Authorize(nonMatchingNamespaceElements, edOrgElements);
            _neitherMatch = handler.Authorize(nonMatchingNamespaceElements, nonMatchingEdOrgElements);
        }

        [Test]
        public void When_both_match_should_be_authorized()
        {
            _namespaceAndEdOrgMatch.Should().BeOfType<ResourceAuthorizationResult.Authorized>();
        }

        [Test]
        public void When_only_namespace_matches_should_be_not_authorized()
        {
            _namespaceMatchEdOrgNoMatch.Should().BeOfType<ResourceAuthorizationResult.NotAuthorized>();
        }

        [Test]
        public void When_only_edorg_matches_should_be_not_authorized()
        {
            _namespaceNoMatchEdOrgMatch.Should().BeOfType<ResourceAuthorizationResult.NotAuthorized>();
        }

        [Test]
        public void When_neither_matches_should_be_not_authorized()
        {
            _neitherMatch.Should().BeOfType<ResourceAuthorizationResult.NotAuthorized>();
        }
    }

    [TestFixture]
    public class Given_An_EdFi_Doc_With_EdOrg_Or_Namespace_Authorization() : ResourceAuthorizationHandlerTests
    {
        private ResourceAuthorizationResult? _namespaceAndEdOrgMatch;
        private ResourceAuthorizationResult? _namespaceMatchEdOrgNoMatch;
        private ResourceAuthorizationResult? _namespaceNoMatchEdOrgMatch;
        private ResourceAuthorizationResult? _neitherMatch;

        [SetUp]
        public void Setup()
        {
            string[] namespaceElements = ["uri://ed-fi.org"];
            long[] edOrgElements = [6001];
            string[] nonMatchingNamespaceElements = ["uri://i-match-nothing.org"];
            long[] nonMatchingEdOrgElements = [9999];

            // Create evaluators with namespace OR edorg filters
            var evaluators = new[]
            {
                new AuthorizationStrategyEvaluator(
                    [
                        new AuthorizationFilter(
                            "Namespace",
                            "uri://ed-fi.org",
                            "",
                            FilterComparison.StartsWith
                        ),
                        new AuthorizationFilter("EducationOrganization", "6001", "", FilterComparison.Equals),
                    ],
                    FilterOperator.Or
                ),
            };

            var handler = new ResourceAuthorizationHandler(evaluators, NullLogger.Instance);
            _namespaceAndEdOrgMatch = handler.Authorize(namespaceElements, edOrgElements);
            _namespaceMatchEdOrgNoMatch = handler.Authorize(namespaceElements, nonMatchingEdOrgElements);
            _namespaceNoMatchEdOrgMatch = handler.Authorize(nonMatchingNamespaceElements, edOrgElements);
            _neitherMatch = handler.Authorize(nonMatchingNamespaceElements, nonMatchingEdOrgElements);
        }

        [Test]
        public void When_both_match_should_be_authorized()
        {
            _namespaceAndEdOrgMatch.Should().BeOfType<ResourceAuthorizationResult.Authorized>();
        }

        [Test]
        public void When_only_namespace_matches_should_be_authorized()
        {
            _namespaceMatchEdOrgNoMatch.Should().BeOfType<ResourceAuthorizationResult.Authorized>();
        }

        [Test]
        public void When_only_edorg_matches_should_be_authorized()
        {
            _namespaceNoMatchEdOrgMatch.Should().BeOfType<ResourceAuthorizationResult.Authorized>();
        }

        [Test]
        public void When_neither_matches_should_be_not_authorized()
        {
            _neitherMatch.Should().BeOfType<ResourceAuthorizationResult.NotAuthorized>();
        }
    }
}
