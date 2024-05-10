// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Api.Core.ApiSchema;
using EdFi.DataManagementService.Api.Core.Middleware;
using EdFi.DataManagementService.Api.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using static EdFi.DataManagementService.Api.Tests.Unit.TestHelper;

namespace EdFi.DataManagementService.Api.Tests.Unit.Core.Middleware;

[TestFixture]
public class ApiSchemaValidationMiddlewareTests
{
    public static IPipelineStep ProvideMiddleware(IApiSchemaProvider provider)
    {
        var logger = A.Fake<ILogger<ApiSchemaSchemaProvider>>();
        var apiValidator = new ApiSchemaValidator(new ApiSchemaSchemaProvider(logger));
        return new ApiSchemaValidationMiddleware(provider, apiValidator, NullLogger.Instance);
    }

    [TestFixture]
    public class Given_an_api_schema_with_validation_errors : ApiSchemaValidationMiddlewareTests
    {
        private readonly PipelineContext _context = No.PipelineContext();
        private static readonly JsonNode _apiSchemaRootNode =
            JsonNode.Parse(
                "{ \"projectSchemas\": { \"ed-fi\": {\"abstractResources\":{},\"caseInsensitiveEndpointNameMapping\":{},\"description\":\"The Ed-Fi Data Standard v5.0\",\"isExtensionProject\":false,\"projectName\":\"ed-fi\",\"projectVersion\":\"5.0.0\",\"resourceNameMapping\":{},\"resourceSchemas\":{}} } }"
            ) ?? new JsonObject();

        public class Provider : IApiSchemaProvider
        {
            public JsonNode ApiSchemaRootNode => _apiSchemaRootNode;
        }

        [SetUp]
        public async Task Setup()
        {
            await ProvideMiddleware(new Provider()).Execute(_context, NullNext);
        }

        [Test]
        public void It_has_a_response()
        {
            _context?.FrontendResponse.Should().NotBe(No.FrontendResponse);
        }

        [Test]
        public void It_returns_status_500()
        {
            _context?.FrontendResponse.StatusCode.Should().Be(500);
        }

        [Test]
        public void It_returns_empty_body()
        {
            _context?.FrontendResponse.Body.Should().BeEmpty();
        }
    }
}
