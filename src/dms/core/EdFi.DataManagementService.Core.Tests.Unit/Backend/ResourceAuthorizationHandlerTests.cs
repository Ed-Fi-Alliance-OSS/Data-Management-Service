// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
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
            JsonNode? securityElements = JsonNode.Parse(
                """
                {
                  "Namespace": ["uri://ed-fi.org"]
                }
                """
            )!;

            var authStrategyEvaluators = clientNamespacePrefixes
                .Split(',')
                .Select(namespacePrefix => new AuthorizationStrategyEvaluator(
                    [new AuthorizationFilter("Namespace", namespacePrefix, "", FilterComparison.StartsWith)],
                    FilterOperator.Or
                ))
                .ToArray();

            var handler = new ResourceAuthorizationHandler(authStrategyEvaluators, NullLogger.Instance);
            _resourceAuthorizationResult = handler.Authorize(securityElements);
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
            JsonNode? securityElements = JsonNode.Parse(
                """
                {
                  "Namespace": ["uri://i-match-nothing.org"]
                }
                """
            )!;

            var authStrategyEvaluators = clientNamespacePrefixes
                .Split(',')
                .Select(namespacePrefix => new AuthorizationStrategyEvaluator(
                    [new AuthorizationFilter("Namespace", namespacePrefix, "", FilterComparison.StartsWith)],
                    FilterOperator.Or
                ))
                .ToArray();

            var handler = new ResourceAuthorizationHandler(authStrategyEvaluators, NullLogger.Instance);
            _resourceAuthorizationResult = handler.Authorize(securityElements);
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
            JsonNode? edFiDoc = JsonNode.Parse(
                """
                {
                  "Namespace": ["uri://i-match-nothing.org"]
                }
                """
            )!;

            var authStrategyEvaluators = ""
                .Split(',')
                .Select(namespacePrefix => new AuthorizationStrategyEvaluator(
                    [new AuthorizationFilter("Namespace", namespacePrefix, "", FilterComparison.StartsWith)],
                    FilterOperator.Or
                ))
                .ToArray();

            var handler = new ResourceAuthorizationHandler(authStrategyEvaluators, NullLogger.Instance);
            _resourceAuthorizationResult = handler.Authorize(edFiDoc);
        }

        [Test]
        public void Result_should_be_authorized()
        {
            _resourceAuthorizationResult.Should().BeOfType<ResourceAuthorizationResult.Authorized>();
        }
    }
}
