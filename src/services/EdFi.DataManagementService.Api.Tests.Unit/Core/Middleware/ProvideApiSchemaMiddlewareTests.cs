// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Api.Core.ApiSchema;
using EdFi.DataManagementService.Api.Core.Middleware;
using EdFi.DataManagementService.Api.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using static EdFi.DataManagementService.Api.Tests.Unit.TestHelper;

namespace EdFi.DataManagementService.Api.Tests.Unit.Core.Middleware;

[TestFixture]
public class ProvideApiSchemaMiddlewareTests
{
    public static IPipelineStep ProvideMiddleware(
        IApiSchemaProvider provider,
        IApiSchemaValidator apiSchemaValidator
    )
    {
        return new ProvideApiSchemaMiddleware(provider, apiSchemaValidator, NullLogger.Instance);
    }

    [TestFixture]
    public class Given_an_api_schema_provider_is_injected : ParsePathMiddlewareTests
    {
        private readonly PipelineContext _context = No.PipelineContext();
        private static readonly JsonNode _apiSchemaRootNode =
            JsonNode.Parse(
                "{\"projectNameMapping\":{}, \"projectSchemas\": { \"ed-fi\": {\"abstractResources\":{},\"caseInsensitiveEndpointNameMapping\":{},\"description\":\"The Ed-Fi Data Standard v5.0\",\"isExtensionProject\":false,\"projectName\":\"ed-fi\",\"projectVersion\":\"5.0.0\",\"resourceNameMapping\":{},\"resourceSchemas\":{}} } }"
            ) ?? new JsonObject();

        public class Provider : IApiSchemaProvider
        {
            public JsonNode ApiSchemaRootNode => _apiSchemaRootNode;
        }

        [SetUp]
        public async Task Setup()
        {
            await ProvideMiddleware(new Provider(), new ApiSchemaValidator()).Execute(_context, NullNext);
        }

        [Test]
        public void It_has_the_root_node_from_the_provider()
        {
            _context
                .ApiSchemaDocument.FindProjectSchemaNode(new("ed-fi"))
                ?.ToString()
                .Should()
                .Contain("abstractResources");
        }
    }

    [TestFixture]
    public class Given_an_invalid_api_schema_content : ParsePathMiddlewareTests
    {
        private readonly PipelineContext _context = No.PipelineContext();
        private static readonly JsonNode _apiSchemaRootNode =
            JsonNode.Parse(
                "{\"projectSchemas\": { \"ed-fi\": {\"abstractResources\":{},\"caseInsensitiveEndpointNameMapping\":{},\"description\":\"The Ed-Fi Data Standard v5.0\",\"isExtensionProject\":false,\"projectName\":\"ed-fi\",\"projectVersion\":\"5.0.0\",\"resourceNameMapping\":{},\"resourceSchemas\":{}} } }"
            ) ?? new JsonObject();

        public class Provider : IApiSchemaProvider
        {
            public JsonNode ApiSchemaRootNode => _apiSchemaRootNode;
        }

        [SetUp]
        public async Task Setup()
        {
            await ProvideMiddleware(new Provider(), new ApiSchemaValidator()).Execute(_context, NullNext);
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
        public void It_returns_message_body()
        {
            _context?.FrontendResponse.Body.Should().Contain("Api Schema file has validation errors.");
        }
    }
}
