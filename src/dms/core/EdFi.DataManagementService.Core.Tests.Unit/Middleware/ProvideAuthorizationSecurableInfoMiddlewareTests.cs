// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.External.Backend;
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
public class ProvideAuthorizationSecurableInfoMiddlewareTests
{
    internal static IPipelineStep BuildMiddleware()
    {
        return new ProvideAuthorizationSecurableInfoMiddleware(NullLogger.Instance);
    }

    [TestFixture]
    public class Given_a_document_with_a_StudentUniqueId : ProvideAuthorizationSecurableInfoMiddlewareTests
    {
        private PipelineContext context = No.PipelineContext();

        [SetUp]
        public async Task Setup()
        {
            ApiSchemaDocuments apiSchemaDocuments = new ApiSchemaBuilder()
                .WithStartProject()
                .WithStartResource("Student")
                .WithStudentSecurityElements(["$.studentUniqueId"]) // This indicates that the StudentUniqueId should be extracted for authorization
                .WithEducationOrganizationSecurityElements([])
                .WithStartDocumentPathsMapping()
                .WithDocumentPathScalar("StudentUniqueId", "$.studentUniqueId")
                .WithEndDocumentPathsMapping()
                .WithEndResource()
                .WithEndProject()
                .ToApiSchemaDocuments();

            ResourceSchema resourceSchema = BuildResourceSchema(apiSchemaDocuments, "students");

            string body = """{"studentUniqueId": "12345"}""";

            context = new(
                new(
                    Body: body,
                    QueryParameters: [],
                    Path: "/ed-fi/students",
                    TraceId: new TraceId("123"),
                    ClientAuthorizations: new ClientAuthorizations(
                        TokenId: "",
                        ClaimSetName: "",
                        EducationOrganizationIds: [],
                        NamespacePrefixes: []
                    )
                ),
                RequestMethod.POST
            )
            {
                ResourceSchema = resourceSchema,
                ParsedBody = JsonNode.Parse(body)!,
            };

            await BuildMiddleware().Execute(context, NullNext);
        }

        [Test]
        public void It_has_StudentUniqueId_as_securable_key()
        {
            context
                .AuthorizationSecurableInfo[0]
                .SecurableKey.Should()
                .Be(SecurityElementNameConstants.StudentUniqueId);
        }
    }

    [TestFixture]
    public class Given_a_document_without_StudentAuthorizationSecurablePaths
        : ProvideAuthorizationSecurableInfoMiddlewareTests
    {
        private PipelineContext context = No.PipelineContext();

        [SetUp]
        public async Task Setup()
        {
            ApiSchemaDocuments apiSchemaDocuments = new ApiSchemaBuilder()
                .WithStartProject()
                .WithStartResource("Student")
                .WithStudentSecurityElements([]) // No paths specified for Student Securable elements
                .WithEducationOrganizationSecurityElements([])
                .WithStartDocumentPathsMapping()
                .WithDocumentPathScalar("StudentUniqueId", "$.studentUniqueId")
                .WithEndDocumentPathsMapping()
                .WithEndResource()
                .WithEndProject()
                .ToApiSchemaDocuments();

            ResourceSchema resourceSchema = BuildResourceSchema(apiSchemaDocuments, "students");

            string body = """{"studentUniqueId": "12345"}""";

            context = new(
                new(
                    Body: body,
                    QueryParameters: [],
                    Path: "/ed-fi/students",
                    TraceId: new TraceId("123"),
                    ClientAuthorizations: new ClientAuthorizations(
                        TokenId: "",
                        ClaimSetName: "",
                        EducationOrganizationIds: [],
                        NamespacePrefixes: []
                    )
                ),
                RequestMethod.POST
            )
            {
                ResourceSchema = resourceSchema,
                ParsedBody = JsonNode.Parse(body)!,
            };

            await BuildMiddleware().Execute(context, NullNext);
        }

        [Test]
        public void It_does_not_have_securable_key()
        {
            context.AuthorizationSecurableInfo.Should().BeEmpty();
        }
    }
}
