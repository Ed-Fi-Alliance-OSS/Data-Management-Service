// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Api.ApiSchema;
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
    public static IPipelineStep ProvideMiddleware(IApiSchemaProvider provider)
    {
        return new ProvideApiSchemaMiddleware(provider, NullLogger.Instance);
    }

    [TestFixture]
    public class Given_an_api_schema_provider_is_injected : ParsePathMiddlewareTests
    {
        private readonly PipelineContext _context = No.PipelineContext();
        private static readonly JsonNode _apiSchemaRootNode =
            JsonNode.Parse("{ \"projectSchemas\": { \"ed-fi\": {} } }") ?? new JsonObject();

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
        public void It_has_the_root_node_from_the_provider()
        {
            _context.ApiSchemaDocument.FindProjectSchemaNode(new("ed-fi"))?.ToString().Should().Be("{}");
        }
    }
}
