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
public class ProvideApiSchemaMiddlewareTests
{
    internal static IPipelineStep ProvideMiddleware(IApiSchemaProvider provider)
    {
        return new ProvideApiSchemaMiddleware(provider, NullLogger.Instance);
    }

    [TestFixture]
    public class Given_An_Api_Schema_Provider_Is_Injected : ParsePathMiddlewareTests
    {
        private readonly PipelineContext _context = No.PipelineContext();
        private static readonly JsonNode _apiSchemaRootNode =
            JsonNode.Parse(
                "{\"projectNameMapping\":{}, \"projectSchemas\": { \"ed-fi\": {\"abstractResources\":{},\"caseInsensitiveEndpointNameMapping\":{},\"description\":\"The Ed-Fi Data Standard v5.0\",\"isExtensionProject\":false,\"projectName\":\"ed-fi\",\"projectVersion\":\"5.0.0\",\"resourceNameMapping\":{},\"resourceSchemas\":{}} } }"
            ) ?? new JsonObject();

        public class Provider : IApiSchemaProvider
        {
            public JsonNode CoreApiSchemaRootNode => _apiSchemaRootNode;
            public JsonNode[] ExtensionApiSchemaRootNodes => [];
        }

        [SetUp]
        public async Task Setup()
        {
            await ProvideMiddleware(new Provider()).Execute(_context, NullNext);
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
}
