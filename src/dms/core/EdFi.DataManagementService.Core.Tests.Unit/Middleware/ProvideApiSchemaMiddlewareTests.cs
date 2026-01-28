// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.Middleware;
using EdFi.DataManagementService.Core.Pipeline;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using static EdFi.DataManagementService.Core.Tests.Unit.TestHelper;
using No = EdFi.DataManagementService.Core.Model.No;

namespace EdFi.DataManagementService.Core.Tests.Unit.Middleware;

[TestFixture]
[Parallelizable]
public class ProvideApiSchemaMiddlewareTests
{
    [TestFixture]
    [Parallelizable]
    public class Given_A_Configured_Effective_Schema : ProvideApiSchemaMiddlewareTests
    {
        private IEffectiveApiSchemaProvider _mockEffectiveProvider = null!;
        private ProvideApiSchemaMiddleware _middleware = null!;
        private ApiSchemaDocuments _mockDocuments = null!;
        private Guid _schemaId;

        [SetUp]
        public void Setup()
        {
            _mockEffectiveProvider = A.Fake<IEffectiveApiSchemaProvider>();
            _schemaId = Guid.NewGuid();

            // Create mock documents
            var schemaNode = JsonNode.Parse(
                """
                {
                    "projectSchemas": {
                        "ed-fi": {
                            "projectName": "ed-fi",
                            "projectVersion": "5.0.0",
                            "description": "Test",
                            "isExtensionProject": false,
                            "resourceSchemas": {}
                        }
                    }
                }
                """
            )!;
            _mockDocuments = new ApiSchemaDocuments(
                new ApiSchemaDocumentNodes(schemaNode, []),
                NullLogger.Instance
            );

            A.CallTo(() => _mockEffectiveProvider.Documents).Returns(_mockDocuments);
            A.CallTo(() => _mockEffectiveProvider.SchemaId).Returns(_schemaId);
            A.CallTo(() => _mockEffectiveProvider.IsInitialized).Returns(true);

            _middleware = new ProvideApiSchemaMiddleware(
                _mockEffectiveProvider,
                NullLogger<ProvideApiSchemaMiddleware>.Instance
            );
        }

        [Test]
        public async Task It_attaches_documents_to_request_info()
        {
            // Arrange
            var requestInfo = No.RequestInfo();

            // Act
            await _middleware.Execute(requestInfo, NullNext);

            // Assert
            requestInfo.ApiSchemaDocuments.Should().BeSameAs(_mockDocuments);
        }

        [Test]
        public async Task It_attaches_schema_id_to_request_info()
        {
            // Arrange
            var requestInfo = No.RequestInfo();

            // Act
            await _middleware.Execute(requestInfo, NullNext);

            // Assert
            requestInfo.ApiSchemaReloadId.Should().Be(_schemaId);
        }

        [Test]
        public async Task It_calls_next_middleware()
        {
            // Arrange
            var requestInfo = No.RequestInfo();
            var nextWasCalled = false;

            // Act
            await _middleware.Execute(
                requestInfo,
                () =>
                {
                    nextWasCalled = true;
                    return Task.CompletedTask;
                }
            );

            // Assert
            nextWasCalled.Should().BeTrue();
        }

        [Test]
        public async Task Multiple_requests_get_same_documents()
        {
            // Arrange
            var requestInfos = Enumerable.Range(0, 10).Select(_ => No.RequestInfo()).ToList();

            // Act
            var tasks = requestInfos.Select(ri => _middleware.Execute(ri, NullNext)).ToArray();
            await Task.WhenAll(tasks);

            // Assert
            requestInfos.Should().OnlyContain(ri => ri.ApiSchemaDocuments == _mockDocuments);
            requestInfos.Should().OnlyContain(ri => ri.ApiSchemaReloadId == _schemaId);
        }
    }
}
