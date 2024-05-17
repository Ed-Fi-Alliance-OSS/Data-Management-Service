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
public class ExtractDocumentInfoMiddlewareTests
{
    internal static IPipelineStep BuildMiddleware()
    {
        return new ExtractDocumentInfoMiddleware(NullLogger.Instance);
    }

    [TestFixture]
    public class Given_pipeline_context_has_resource_schemas_and_document_body
        : ExtractDocumentInfoMiddlewareTests
    {
        private PipelineContext context = No.PipelineContext();

        [SetUp]
        public async Task Setup()
        {
            ApiSchemaDocument apiSchemaDocument = new ApiSchemaBuilder()
                .WithStartProject()
                .WithStartResource("School")
                .WithIdentityJsonPaths(["$.schoolId"])
                .WithStartDocumentPathsMapping()
                .WithDocumentPathScalar("SchoolId", "$.schoolId")
                .WithEndDocumentPathsMapping()
                .WithSuperclassInformation(
                    subclassType: "domainEntity",
                    superclassIdentityJsonPath: "$.educationOrganizationId",
                    superclassResourceName: "EducationOrganization"
                )
                .WithEndResource()
                .WithEndProject()
                .ToApiSchemaDocument();

            ResourceSchema resourceSchema = BuildResourceSchema(apiSchemaDocument, "schools");

            DocumentIdentity documentIdentity = resourceSchema.ExtractDocumentIdentity(
                JsonNode.Parse(
                    """
                    {
                        "schoolId": "123"
                    }
"""
                )!
            );

            context = new(
                new(
                    Body: """{"schoolId": "123"}"""!,
                    QueryParameters: [],
                    Path: "/ed-fi/schools",
                    TraceId: new("123")
                ),
                RequestMethod.POST
            )
            {
                ResourceSchema = resourceSchema
            };

            await BuildMiddleware().Execute(context, NullNext);
        }

        [Test]
        public void It_has_built_the_document_info_document_references()
        {
            context.DocumentInfo.DocumentReferences.Should().HaveCount(0);
        }

        [Test]
        public void It_has_built_the_document_info_descriptor_references()
        {
            context.DocumentInfo.DescriptorReferences.Should().HaveCount(0);
        }

        [Test]
        public void It_has_built_the_document_info_document_identity()
        {
            var identityElements = context.DocumentInfo.DocumentIdentity.DocumentIdentityElements;
            identityElements.Should().HaveCount(1);
            identityElements[0].IdentityJsonPath.Value.Should().Be("$.schoolId");
            identityElements[0].IdentityValue.Should().Be("123");
        }

        public void It_has_derived_the_superclass_identity()
        {
            var superclassIdentityElements = context
                .DocumentInfo
                .SuperclassIdentity!
                .DocumentIdentity
                .DocumentIdentityElements;
            superclassIdentityElements.Should().HaveCount(1);
            superclassIdentityElements[0].IdentityJsonPath.Value.Should().Be("$.educationOrganizationId");
            superclassIdentityElements[0].IdentityValue.Should().Be("123");
        }

        [Test]
        public void It_has_derived_the_superclass_resource_info()
        {
            var superclassResourceInfo = context.DocumentInfo.SuperclassIdentity!.ResourceInfo;

            superclassResourceInfo.IsDescriptor.Should().Be(false);
            superclassResourceInfo.ProjectName.Value.Should().Be("Ed-Fi");
            superclassResourceInfo.ResourceName.Value.Should().Be("EducationOrganization");
        }
    }
}
