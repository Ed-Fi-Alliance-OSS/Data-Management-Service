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
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using static EdFi.DataManagementService.Core.Tests.Unit.TestHelper;

namespace EdFi.DataManagementService.Core.Tests.Unit.Middleware;

[TestFixture]
[Parallelizable]
public class ExtractDocumentInfoMiddlewareTests
{
    internal static IPipelineStep BuildMiddleware()
    {
        return new ExtractDocumentInfoMiddleware(NullLogger.Instance);
    }

    [TestFixture]
    [Parallelizable]
    public class Given_a_school_that_is_a_subclass_with_no_outbound_references
        : ExtractDocumentInfoMiddlewareTests
    {
        private RequestInfo requestInfo = No.RequestInfo();

        [SetUp]
        public async Task Setup()
        {
            ApiSchemaDocuments apiSchemaDocuments = new ApiSchemaBuilder()
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
                .ToApiSchemaDocuments();

            ResourceSchema resourceSchema = BuildResourceSchema(apiSchemaDocuments, "schools");

            string body = """{"schoolId": "123"}""";

            requestInfo = new(
                new(
                    Body: body,
                    Headers: [],
                    QueryParameters: [],
                    Path: "/ed-fi/schools",
                    TraceId: new TraceId("123")
                ),
                RequestMethod.POST
            )
            {
                ResourceSchema = resourceSchema,
                ParsedBody = JsonNode.Parse(body)!,
            };

            await BuildMiddleware().Execute(requestInfo, NullNext);
        }

        [Test]
        public void It_has_no_document_references()
        {
            requestInfo.DocumentInfo.DocumentReferences.Should().HaveCount(0);
        }

        [Test]
        public void It_has_no_descriptor_references()
        {
            requestInfo.DocumentInfo.DescriptorReferences.Should().HaveCount(0);
        }

        [Test]
        public void It_has_built_the_document_identity()
        {
            var identityElements = requestInfo.DocumentInfo.DocumentIdentity.DocumentIdentityElements;
            identityElements.Should().HaveCount(1);
            identityElements[0].IdentityJsonPath.Value.Should().Be("$.schoolId");
            identityElements[0].IdentityValue.Should().Be("123");
        }

        public void It_has_derived_the_superclass_identity()
        {
            var superclassIdentityElements = requestInfo
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
            var superclassResourceInfo = requestInfo.DocumentInfo.SuperclassIdentity!.ResourceInfo;

            superclassResourceInfo.IsDescriptor.Should().Be(false);
            superclassResourceInfo.ProjectName.Value.Should().Be("Ed-Fi");
            superclassResourceInfo.ResourceName.Value.Should().Be("EducationOrganization");
        }
    }
}
