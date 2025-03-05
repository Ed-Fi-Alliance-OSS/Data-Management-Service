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
                "{\"projectSchema\": { "
                    + "\"abstractResources\": {}, "
                    + "\"caseInsensitiveEndpointNameMapping\": {}, "
                    + "\"description\": \"The Ed-Fi Data Standard v5.0\", "
                    + "\"isExtensionProject\": false, "
                    + "\"projectName\": \"ed-fi\", "
                    + "\"projectEndpointName\": \"ed-fi\", "
                    + "\"projectVersion\": \"5.0.0\", "
                    + "\"resourceNameMapping\": {}, "
                    + "\"resourceSchemas\": {"
                    + "\"credentials\": {"
                    + "\"isResourceExtension\": true, "
                    + "\"dateTimeJsonPaths\": [\"/dateTime/path1\"], "
                    + "\"booleanJsonPaths\": [\"/boolean/path1\"]"
                    + "}}}}"
            ) ?? new JsonObject();

        private static readonly JsonNode _extensionApiSchemaRootNode =
            JsonNode.Parse(
                "{\"projectSchema\": { "
                    + "\"resourceSchemas\": { "
                    + "\"credentials\": { "
                    + "\"isResourceExtension\": true, "
                    + "\"dateTimeJsonPaths\": [\"/dateTime/path1\"], "
                    + "\"booleanJsonPaths\": [\"/boolean/path1\"] "
                    + "} "
                    + "} "
                    + "}}"
            ) ?? new JsonObject();

        internal class Provider : IApiSchemaProvider
        {
            public ApiSchemaNodes GetApiSchemaNodes()
            {
                return new ApiSchemaNodes(_apiSchemaRootNode, new[] { _extensionApiSchemaRootNode });
            }
        }

        [SetUp]
        public async Task Setup()
        {
            var provider = new Provider();
            var apiSchemaNodes = provider.GetApiSchemaNodes();

            _context.ApiSchemaDocuments = new ApiSchemaDocuments(apiSchemaNodes, NullLogger.Instance);

            var middleware = ProvideMiddleware(provider);
            await middleware.Execute(_context, NullNext);
        }

        [Test]
        public void It_has_the_root_node_from_the_provider()
        {
            _context.Should().NotBeNull();
            _context.ApiSchemaDocuments.Should().NotBeNull();

            _context
                .ApiSchemaDocuments.FindProjectSchemaForProjectNamespace(new("ed-fi"))
                .Should()
                .NotBeNull();
        }

        [Test]
        public void It_updates_core_resource_schemas_with_extension_json_paths()
        {
            var provider = new Provider();
            var apiSchemaNodes = provider.GetApiSchemaNodes();

            var coreResourceSchemas = apiSchemaNodes
                .CoreApiSchemaRootNode
                ?["projectSchema"]
                ?["resourceSchemas"];
            coreResourceSchemas.Should().NotBeNull();

            var extensionResourceSchema = apiSchemaNodes
                .ExtensionApiSchemaRootNodes[0]
                ?["projectSchema"]
                ?["resourceSchemas"]
                ?["credentials"];
            extensionResourceSchema.Should().NotBeNull();

            var dateTimeJsonPaths = extensionResourceSchema?["dateTimeJsonPaths"] as JsonArray;
            var booleanJsonPaths = extensionResourceSchema?["booleanJsonPaths"] as JsonArray;

            dateTimeJsonPaths.Should().NotBeNull();
            booleanJsonPaths.Should().NotBeNull();

            var dateTimePaths = dateTimeJsonPaths?.Select(p => p?.ToString()).ToList();
            var booleanPaths = booleanJsonPaths?.Select(p => p?.ToString()).ToList();

            dateTimePaths.Should().Contain("/dateTime/path1");
            booleanPaths.Should().Contain("/boolean/path1");
        }
    }
}
