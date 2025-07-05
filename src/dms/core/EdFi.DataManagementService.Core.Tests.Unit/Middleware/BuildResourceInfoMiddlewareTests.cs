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
[Parallelizable]
public class BuildResourceInfoMiddlewareTests
{
    internal static IPipelineStep BuildMiddleware(List<string> allowIdentityUpdateOverrides)
    {
        return new BuildResourceInfoMiddleware(NullLogger.Instance, allowIdentityUpdateOverrides);
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Pipeline_Context_Has_Project_And_Resource_Schemas : BuildResourceInfoMiddlewareTests
    {
        private readonly RequestInfo requestInfo = No.RequestInfo();

        [SetUp]
        public async Task Setup()
        {
            ApiSchemaDocuments apiSchemaDocument = new ApiSchemaBuilder()
                .WithStartProject("Ed-Fi", "5.0.0")
                .WithStartResource("School")
                .WithEndResource()
                .WithEndProject()
                .ToApiSchemaDocuments();

            requestInfo.ProjectSchema = apiSchemaDocument.FindProjectSchemaForProjectNamespace(new("ed-fi"))!;
            requestInfo.ResourceSchema = new ResourceSchema(
                requestInfo.ProjectSchema.FindResourceSchemaNodeByEndpointName(new("schools"))
                    ?? new JsonObject()
            );

            await BuildMiddleware([]).Execute(requestInfo, NullNext);
        }

        [Test]
        public void It_has_built_the_resource_info()
        {
            requestInfo.ResourceInfo.ProjectName.Value.Should().Be("Ed-Fi");
            requestInfo.ResourceInfo.ResourceName.Value.Should().Be("School");
            requestInfo.ResourceInfo.ResourceVersion.Value.Should().Be("5.0.0");
            requestInfo.ResourceInfo.IsDescriptor.Should().BeFalse();
            requestInfo.ResourceInfo.AllowIdentityUpdates.Should().BeFalse();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Pipeline_Context_Has_Project_And_Resource_Schemas_And_Overrides_Allow_Identity_Updates
        : BuildResourceInfoMiddlewareTests
    {
        private readonly RequestInfo requestInfo = No.RequestInfo();

        [SetUp]
        public async Task Setup()
        {
            ApiSchemaDocuments apiSchemaDocument = new ApiSchemaBuilder()
                .WithStartProject("Ed-Fi", "5.0.0")
                .WithStartResource("School")
                .WithEndResource()
                .WithEndProject()
                .ToApiSchemaDocuments();

            requestInfo.ProjectSchema = apiSchemaDocument.FindProjectSchemaForProjectNamespace(new("ed-fi"))!;
            requestInfo.ResourceSchema = new ResourceSchema(
                requestInfo.ProjectSchema.FindResourceSchemaNodeByEndpointName(new("schools"))
                    ?? new JsonObject()
            );

            await BuildMiddleware(["School"]).Execute(requestInfo, NullNext);
        }

        [Test]
        public void It_has_built_the_resource_info()
        {
            requestInfo.ResourceInfo.ProjectName.Value.Should().Be("Ed-Fi");
            requestInfo.ResourceInfo.ResourceName.Value.Should().Be("School");
            requestInfo.ResourceInfo.ResourceVersion.Value.Should().Be("5.0.0");
            requestInfo.ResourceInfo.IsDescriptor.Should().BeFalse();
            requestInfo.ResourceInfo.AllowIdentityUpdates.Should().BeTrue();
        }
    }
}
