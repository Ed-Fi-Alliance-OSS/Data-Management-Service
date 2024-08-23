// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.Middleware;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using static EdFi.DataManagementService.Core.Tests.Unit.TestHelper;

namespace EdFi.DataManagementService.Core.Tests.Unit.Middleware;

[TestFixture]
public class ApiSchemaValidationMiddlewareTests
{
    internal static IPipelineStep ProvideMiddleware(IApiSchemaProvider provider)
    {
        var apiValidator = new ApiSchemaValidator(new ApiSchemaSchemaProvider(NullLogger<ApiSchemaSchemaProvider>.Instance));
        return new ApiSchemaValidationMiddleware(provider, apiValidator, NullLogger.Instance);
    }

    [TestFixture]
    public class Given_An_Api_Schema_With_Validation_Errors : ApiSchemaValidationMiddlewareTests
    {
        private readonly PipelineContext _context = No.PipelineContext();

        public class Provider : IApiSchemaProvider
        {
            private static readonly JsonNode _apiSchemaRootNode = JsonNode.Parse(
                "{ \"projectSchemas\": { \"ed-fi\": {\"abstractResources\":{},\"caseInsensitiveEndpointNameMapping\":{},\"description\":\"The Ed-Fi Data Standard v5.0\",\"isExtensionProject\":false,\"projectName\":\"ed-fi\",\"projectVersion\":\"5.0.0\",\"resourceNameMapping\":{},\"resourceSchemas\":{}} } }"
            )!;
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
            _context?.FrontendResponse.Body?.AsValue().ToString().Should().Be(string.Empty);
        }
    }
}
