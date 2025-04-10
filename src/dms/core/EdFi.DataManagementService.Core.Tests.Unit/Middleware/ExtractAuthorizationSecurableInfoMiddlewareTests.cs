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
                .WithStudentAuthorizationSecurablePaths(["$.studentUniqueId"]) // This indicates that the StudentUniqueId should be extracted for authorization
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
        public void It_has_extracted_the_StudentUniqueId()
        {
            context.AuthorizationSecurableInfo[0].Should().Be("12345");
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
                .WithStudentAuthorizationSecurablePaths([]) // No paths specified for StudentAuthorizationSecurable
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
        public void It_does_not_extract_StudentUniqueId()
        {
            context.AuthorizationSecurableInfo[0].Should().BeNull();
        }
    }

    [TestFixture]
    public class Given_a_document_with_multiple_StudentUniqueId_paths
        : ProvideAuthorizationSecurableInfoMiddlewareTests
    {
        private PipelineContext context = No.PipelineContext();

        [SetUp]
        public async Task Setup()
        {
            ApiSchemaDocuments apiSchemaDocuments = new ApiSchemaBuilder()
                .WithStartProject()
                .WithStartResource("Student")
                .WithStudentAuthorizationSecurablePaths(["$.studentUniqueId", "$.alternateStudentUniqueId"]) // Multiple paths specified
                .WithStartDocumentPathsMapping()
                .WithDocumentPathScalar("StudentUniqueId", "$.studentUniqueId")
                .WithDocumentPathScalar("AlternateStudentUniqueId", "$.alternateStudentUniqueId")
                .WithEndDocumentPathsMapping()
                .WithEndResource()
                .WithEndProject()
                .ToApiSchemaDocuments();

            ResourceSchema resourceSchema = BuildResourceSchema(apiSchemaDocuments, "students");

            string body = """{"studentUniqueId": "12345", "alternateStudentUniqueId": "12345"}""";

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
        public void It_has_extracted_the_StudentUniqueId()
        {
            context.AuthorizationSecurableInfo[0].Should().Be("12345");
        }
    }

    [TestFixture]
    public class Given_an_invalid_document_with_multiple_StudentUniqueId_paths_and_different_ids
        : ProvideAuthorizationSecurableInfoMiddlewareTests
    {
        private PipelineContext context = No.PipelineContext();

        [SetUp]
        public void Setup()
        {
            ApiSchemaDocuments apiSchemaDocuments = new ApiSchemaBuilder()
                .WithStartProject()
                .WithStartResource("Student")
                .WithStudentAuthorizationSecurablePaths(["$.studentUniqueId", "$.alternateStudentUniqueId"]) // Multiple paths specified
                .WithStartDocumentPathsMapping()
                .WithDocumentPathScalar("StudentUniqueId", "$.studentUniqueId")
                .WithDocumentPathScalar("AlternateStudentUniqueId", "$.alternateStudentUniqueId")
                .WithEndDocumentPathsMapping()
                .WithEndResource()
                .WithEndProject()
                .ToApiSchemaDocuments();

            ResourceSchema resourceSchema = BuildResourceSchema(apiSchemaDocuments, "students");

            string body = """{"studentUniqueId": "12345", "alternateStudentUniqueId": "67890"}""";

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
        }

        [Test]
        public void It_throws_an_exception_due_to_multiple_StudentUniqueId_paths()
        {
            Action action = () => BuildMiddleware().Execute(context, NullNext).GetAwaiter().GetResult();
            _ = action
                .Should()
                .Throw<InvalidOperationException>()
                .WithMessage(
                    "More than one distinct StudentUniqueId found on StudentAuthorizationSecurable document."
                );
        }
    }
}
