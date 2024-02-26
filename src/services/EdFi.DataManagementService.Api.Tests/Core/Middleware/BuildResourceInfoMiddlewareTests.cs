// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using EdFi.DataManagementService.Api.ApiSchema;
using EdFi.DataManagementService.Api.Core.Middleware;
using EdFi.DataManagementService.Api.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using static EdFi.DataManagementService.Api.Tests.TestHelper;

namespace EdFi.DataManagementService.Api.Tests.Core.Middleware;

[TestFixture]
public class BuildResourceInfoMiddlewareTests
{
    public static IPipelineStep BuildMiddleware()
    {
        return new BuildResourceInfoMiddleware(NullLogger.Instance);
    }

    public static ApiSchemaDocument SchemaDocument()
    {
        string apiSchemaString = """
{
  "projectNameMapping": {
    "Ed-Fi": "ed-fi"
  },
  "projectSchemas": {
    "ed-fi": {
      "description": "The Ed-Fi Data Standard v5.0",
      "isExtensionProject": false,
      "projectName": "Ed-Fi",
      "projectVersion": "5.0.0",
      "caseInsensitiveEndpointNameMapping": {
        "schools": "schools"
      },
      "resourceNameMapping": {
        "School": "schools"
      },
      "resourceSchemas": {
        "schools": {
          "resourceName": "School"
        }
      }
    }
  }
}
""";
        JsonNode _apiSchemaNode = JsonNode.Parse(apiSchemaString) ?? new JsonObject();
        return new ApiSchemaDocument(_apiSchemaNode, NullLogger.Instance);
    }

    [TestFixture]
    public class Given_pipeline_context_has_project_and_resource_schemas : ParsePathMiddlewareTests
    {
        private readonly PipelineContext context = No.PipelineContext();

        [SetUp]
        public async Task Setup()
        {
            context.ProjectSchema = new ProjectSchema(
                SchemaDocument().FindProjectSchemaNode(new("ed-fi")) ?? new JsonObject(),
                NullLogger.Instance
            );
            context.ResourceSchema = new ResourceSchema(
                context.ProjectSchema.FindResourceSchemaNode(new("schools")) ?? new JsonObject(),
                NullLogger.Instance
            );

            await BuildMiddleware().Execute(context, NullNext);
        }

        [Test]
        public void It_has_built_the_resource_info()
        {
            context.ResourceInfo.ProjectName.Value.Should().Be("Ed-Fi");
            context.ResourceInfo.ResourceName.Value.Should().Be("School");
            context.ResourceInfo.ResourceVersion.Value.Should().Be("5.0.0");
            context.ResourceInfo.IsDescriptor.Should().BeFalse();
            context.ResourceInfo.AllowIdentityUpdates.Should().BeFalse();
        }
    }
}
