// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Middleware;
using EdFi.DataManagementService.Core.Model;
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
public class ApiSchemaValidationMiddlewareTests
{
    internal static IPipelineStep ProvideMiddleware(IApiSchemaProvider provider)
    {
        return new ApiSchemaValidationMiddleware(provider, NullLogger.Instance);
    }

    [TestFixture]
    [Parallelizable]
    public class Given_An_Api_Schema_With_Validation_Errors : ApiSchemaValidationMiddlewareTests
    {
        private readonly RequestInfo _requestInfo = No.RequestInfo();

        public class Provider : IApiSchemaProvider
        {
            private static readonly JsonNode _apiSchemaRootNode = JsonNode.Parse(
                "{ \"projectSchemas\": { \"ed-fi\": {\"abstractResources\":{},\"caseInsensitiveEndpointNameMapping\":{},\"description\":\"The Ed-Fi Data Standard v5.0\",\"isExtensionProject\":false,\"projectName\":\"ed-fi\",\"projectVersion\":\"5.0.0\",\"resourceNameMapping\":{},\"resourceSchemas\":{}} } }"
            )!;

            public ApiSchemaDocumentNodes GetApiSchemaNodes()
            {
                return new(_apiSchemaRootNode, []);
            }

            public Guid ReloadId => Guid.Empty;

            public bool IsSchemaValid => false; // Simulating invalid schema

            public List<ApiSchemaFailure> ApiSchemaFailures =>
                [new ApiSchemaFailure("Validation", "Invalid schema", new JsonPath("$.projectSchemas"))];

            public Task<ApiSchemaLoadStatus> ReloadApiSchemaAsync() =>
                Task.FromResult(new ApiSchemaLoadStatus(true, []));

            public Task<ApiSchemaLoadStatus> LoadApiSchemaFromAsync(
                JsonNode coreSchema,
                JsonNode[] extensionSchemas
            ) => Task.FromResult(new ApiSchemaLoadStatus(true, []));
        }

        [SetUp]
        public async Task Setup()
        {
            await ProvideMiddleware(new Provider()).Execute(_requestInfo, NullNext);
        }

        [Test]
        public void It_has_a_response()
        {
            _requestInfo?.FrontendResponse.Should().NotBe(No.FrontendResponse);
        }

        [Test]
        public void It_returns_status_500()
        {
            _requestInfo?.FrontendResponse.StatusCode.Should().Be(500);
        }

        [Test]
        public void It_returns_empty_body()
        {
            _requestInfo?.FrontendResponse.Body?.AsValue().ToString().Should().Be(string.Empty);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class HotReloadScenarios : ApiSchemaValidationMiddlewareTests
    {
        private IApiSchemaProvider _mockProvider = null!;
        private ApiSchemaValidationMiddleware _middleware = null!;

        [SetUp]
        public void Setup()
        {
            _mockProvider = A.Fake<IApiSchemaProvider>();
            _middleware = new ApiSchemaValidationMiddleware(_mockProvider, NullLogger.Instance);
        }

        [Test]
        public async Task Process_WhenSchemaIsValid_CallsNext()
        {
            // Arrange
            var requestInfo = No.RequestInfo();
            var nextWasCalled = false;

            A.CallTo(() => _mockProvider.IsSchemaValid).Returns(true);

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
            requestInfo.FrontendResponse.Should().Be(No.FrontendResponse);
        }

        [Test]
        public async Task Process_WhenSchemaIsInvalid_Returns500()
        {
            // Arrange
            var requestInfo = No.RequestInfo();
            var nextWasCalled = false;

            A.CallTo(() => _mockProvider.IsSchemaValid).Returns(false);

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
            nextWasCalled.Should().BeFalse();
            requestInfo.FrontendResponse.Should().NotBe(No.FrontendResponse);
            requestInfo.FrontendResponse.StatusCode.Should().Be(500);
            requestInfo.FrontendResponse.Body?.AsValue().ToString().Should().Be(string.Empty);
        }
    }
}
