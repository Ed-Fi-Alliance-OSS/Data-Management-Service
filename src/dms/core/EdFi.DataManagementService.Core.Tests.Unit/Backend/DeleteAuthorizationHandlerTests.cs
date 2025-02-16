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
public class DeleteAuthorizationHandlerTests
{
    [TestFixture("uri://ed-fi.org")]
    [TestFixture("uri://ed-fi.org uri://test.org")]
    [TestFixture("uri://test.org uri://ed-fi.org")]
    public class Given_An_EdFi_Doc_With_Matching_ClientAuthorization_Namespace(string clientNamespacePrefixes) : DeleteAuthorizationHandlerTests
    {
        private DeleteAuthorizationResult? _deleteAuthorizationResult;

        [SetUp]
        public void Setup()
        {
            JsonNode? edFiDoc = JsonNode.Parse("""
                                              {
                                                "id": "f97aa4ca-2c2c-4b04-bb63-3a2d45d46e56",
                                                "_etag": "-8584628262134775808",
                                                "codeValue": "School",
                                                "namespace": "uri://ed-fi.org",
                                                "shortDescription": "School",
                                                "_lastModifiedDate": "2025-02-05T18:37:52Z"
                                              }
                                              """)!;

            var authStrategyEvaluators = clientNamespacePrefixes.Split(' ').Select(namespacePrefix =>
                new AuthorizationStrategyEvaluator([
                    new AuthorizationFilter("Namespace", namespacePrefix, FilterComparison.StartsWith)
                ], FilterOperator.Or)).ToArray();

            var handler = new DeleteAuthorizationHandler(authStrategyEvaluators, NullLogger.Instance);
            _deleteAuthorizationResult = handler.Authorize(edFiDoc);
        }

        [Test]
        public void Result_should_be_authorized()
        {
            _deleteAuthorizationResult.Should().BeOfType<DeleteAuthorizationResult.Authorized>();
        }
    }

    [TestFixture("uri://ed-fi.org")]
    [TestFixture("uri://ed-fi.org uri://test.org")]
    [TestFixture("uri://test.org uri://ed-fi.org")]
    public class Given_An_EdFi_Doc_With_No_Matching_ClientAuthorization_Namespace(string clientNamespacePrefixes) : DeleteAuthorizationHandlerTests
    {
        private DeleteAuthorizationResult? _deleteAuthorizationResult;

        [SetUp]
        public void Setup()
        {
            JsonNode? edFiDoc = JsonNode.Parse("""
                                               {
                                                 "id": "f97aa4ca-2c2c-4b04-bb63-3a2d45d46e56",
                                                 "_etag": "-8584628262134775808",
                                                 "codeValue": "School",
                                                 "namespace": "uri://i-match-nothing.org",
                                                 "shortDescription": "School",
                                                 "_lastModifiedDate": "2025-02-05T18:37:52Z"
                                               }
                                               """)!;

            var authStrategyEvaluators = clientNamespacePrefixes.Split(' ').Select(namespacePrefix =>
                new AuthorizationStrategyEvaluator([
                    new AuthorizationFilter("Namespace", namespacePrefix, FilterComparison.StartsWith)
                ], FilterOperator.Or)).ToArray();

            var handler = new DeleteAuthorizationHandler(authStrategyEvaluators, NullLogger.Instance);
            _deleteAuthorizationResult = handler.Authorize(edFiDoc);
        }

        [Test]
        [Ignore("Temporary")]
        public void Result_should_be_authorized()
        {
            _deleteAuthorizationResult.Should().BeOfType<DeleteAuthorizationResult.NotAuthorizedNamespace>();
        }
    }

    [TestFixture]
    public class Given_An_EdFi_Doc_With_No_ClientAuthorization_Namespace() : DeleteAuthorizationHandlerTests
    {
        private DeleteAuthorizationResult? _deleteAuthorizationResult;

        [SetUp]
        public void Setup()
        {
            JsonNode? edFiDoc = JsonNode.Parse("""
                                               {
                                                 "id": "f97aa4ca-2c2c-4b04-bb63-3a2d45d46e56",
                                                 "_etag": "-8584628262134775808",
                                                 "codeValue": "School",
                                                 "namespace": "uri://i-match-nothing.org",
                                                 "shortDescription": "School",
                                                 "_lastModifiedDate": "2025-02-05T18:37:52Z"
                                               }
                                               """)!;

            var authStrategyEvaluators = "".Split(' ').Select(namespacePrefix =>
                new AuthorizationStrategyEvaluator([
                    new AuthorizationFilter("Namespace", namespacePrefix, FilterComparison.StartsWith)
                ], FilterOperator.Or)).ToArray();

            var handler = new DeleteAuthorizationHandler(authStrategyEvaluators, NullLogger.Instance);
            _deleteAuthorizationResult = handler.Authorize(edFiDoc);
        }

        [Test]
        public void Result_should_be_authorized()
        {
            _deleteAuthorizationResult.Should().BeOfType<DeleteAuthorizationResult.Authorized>();
        }
    }
}
