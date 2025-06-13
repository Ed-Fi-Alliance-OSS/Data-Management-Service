// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.External.Frontend;
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
public class ArrayUniquenessValidationMiddlewareTests
{
    internal static ApiSchemaDocuments ArrayUniquenessSchemaDocuments()
    {
        var result = new ApiSchemaBuilder()
            .WithStartProject()
            .WithStartResource("Assessment")
            .WithStartDocumentPathsMapping()
            .WithDocumentPathDescriptor(
                "AssessmentItemResultDescriptor",
                "$.items[*].assessmentItemResultDescriptor"
            )
            .WithEndDocumentPathsMapping()
            .WithStartArrayUniquenessConstraints()
            .WithArrayUniquenessConstraints(
                [
                    "$.performanceLevels[*].assessmentReportingMethodDescriptor",
                    "$.performanceLevels[*].performanceLevelDescriptor",
                ]
            )
            .WithEndArrayUniquenessConstraints()
            .WithEndResource()
            .WithEndProject()
            .ToApiSchemaDocuments();

        return result;
    }

    internal PipelineContext ArrayUniquenessContext(FrontendRequest frontendRequest, RequestMethod method)
    {
        PipelineContext context = new(frontendRequest, method)
        {
            ApiSchemaDocuments = ArrayUniquenessSchemaDocuments(),
            PathComponents = new(
                ProjectNamespace: new("ed-fi"),
                EndpointName: new("assessments"),
                DocumentUuid: No.DocumentUuid
            ),
        };
        context.ProjectSchema = context.ApiSchemaDocuments.FindProjectSchemaForProjectNamespace(
            new("ed-fi")
        )!;
        context.ResourceSchema = new ResourceSchema(
            context.ProjectSchema.FindResourceSchemaNodeByEndpointName(new("assessments")) ?? new JsonObject()
        );

        if (context.FrontendRequest.Body != null)
        {
            var body = JsonNode.Parse(context.FrontendRequest.Body);
            if (body != null)
            {
                context.ParsedBody = body;
            }
        }

        return context;
    }

    internal static ArrayUniquenessValidationMiddleware Middleware()
    {
        return new ArrayUniquenessValidationMiddleware(NullLogger.Instance);
    }

    [TestFixture]
    public class Given_Document_With_Array_Uniqueness_Constraint_Violation
        : ArrayUniquenessValidationMiddlewareTests
    {
        private PipelineContext _context = No.PipelineContext();

        [SetUp]
        public async Task Setup()
        {
            string jsonBody = """
                {
                 "assessmentIdentifier": "01774fa3-06f1-47fe-8801-c8b1e65057f2",
                 "namespace": "uri://ed-fi.org/Assessment/Assessment.xml",
                 "assessmentTitle": "3rd Grade Reading 1st Six Weeks 2021-2022",
                 "performanceLevels": [
                   {
                     "performanceLevelDescriptor": "uri://ed-fi.org/PerformanceLevelDescriptor#Advanced",
                     "assessmentReportingMethodDescriptor": "uri://ed-fi.org/AssessmentReportingMethodDescriptor#Scale score",
                     "minimumScore": "23",
                     "maximumScore": "26"
                   },
                   {
                     "performanceLevelDescriptor": "uri://ed-fi.org/PerformanceLevelDescriptor#Advanced",
                     "assessmentReportingMethodDescriptor": "uri://ed-fi.org/AssessmentReportingMethodDescriptor#Scale score",
                     "minimumScore": "27",
                     "maximumScore": "30"
                   }
                 ]
                }
                """;

            FrontendRequest frontEndRequest = new(
                Path: "ed-fi/assessments",
                Body: jsonBody,
                Headers: [],
                QueryParameters: [],
                TraceId: new TraceId(""),
                new ClientAuthorizations(
                    TokenId: "",
                    ClaimSetName: "",
                    EducationOrganizationIds: [],
                    NamespacePrefixes: []
                )
            );

            _context = ArrayUniquenessContext(frontEndRequest, RequestMethod.POST);

            await Middleware().Execute(_context, NullNext);
        }

        [Test]
        public void It_returns_status_400()
        {
            _context.FrontendResponse.StatusCode.Should().Be(400);
        }

        [Test]
        public void It_returns_validation_error_with_duplicated_array_constraint()
        {
            _context.FrontendResponse.Body?.ToJsonString().Should().Contain("Data Validation Failed");

            _context
                .FrontendResponse.Body?.ToJsonString()
                .Should()
                .Contain(
                    """
                    "validationErrors":{"$.performanceLevels":["The 2nd item of the performanceLevels has the same identifying values as another item earlier in the list."]}
                    """
                );
        }
    }

    [TestFixture]
    public class Given_Document_With_Unique_Array_Elements : ArrayUniquenessValidationMiddlewareTests
    {
        private PipelineContext _context = No.PipelineContext();

        [SetUp]
        public async Task Setup()
        {
            string jsonBody = """
                {
                 "assessmentIdentifier": "01774fa3-06f1-47fe-8801-c8b1e65057f2",
                 "namespace": "uri://ed-fi.org/Assessment/Assessment.xml",
                 "assessmentTitle": "3rd Grade Reading 1st Six Weeks 2021-2022",
                 "performanceLevels": [
                   {
                     "performanceLevelDescriptor": "uri://ed-fi.org/PerformanceLevelDescriptor#Advanced",
                     "assessmentReportingMethodDescriptor": "uri://ed-fi.org/AssessmentReportingMethodDescriptor#Scale score",
                     "minimumScore": "23",
                     "maximumScore": "26"
                   },
                   {
                     "performanceLevelDescriptor": "uri://ed-fi.org/PerformanceLevelDescriptor#Proficient",
                     "assessmentReportingMethodDescriptor": "uri://ed-fi.org/AssessmentReportingMethodDescriptor#Raw score",
                     "minimumScore": "15",
                     "maximumScore": "22"
                   }
                 ]
                }
                """;

            FrontendRequest frontEndRequest = new(
                Path: "ed-fi/assessments",
                Body: jsonBody,
                Headers: [],
                QueryParameters: [],
                TraceId: new TraceId(""),
                new ClientAuthorizations(
                    TokenId: "",
                    ClaimSetName: "",
                    EducationOrganizationIds: [],
                    NamespacePrefixes: []
                )
            );

            _context = ArrayUniquenessContext(frontEndRequest, RequestMethod.POST);

            await Middleware().Execute(_context, NullNext);
        }

        [Test]
        public void It_continues_to_next_middleware()
        {
            _context.FrontendResponse.Should().Be(No.FrontendResponse);
        }
    }

    [TestFixture]
    public class Given_Document_With_No_Array_Uniqueness_Constraints
        : ArrayUniquenessValidationMiddlewareTests
    {
        private PipelineContext _context = No.PipelineContext();

        [SetUp]
        public async Task Setup()
        {
            // Use a schema without array uniqueness constraints
            var schemaDocuments = new ApiSchemaBuilder()
                .WithStartProject()
                .WithStartResource("SimpleResource")
                .WithStartDocumentPathsMapping()
                .WithEndDocumentPathsMapping()
                .WithEndResource()
                .WithEndProject()
                .ToApiSchemaDocuments();

            string jsonBody = """
                {
                 "simpleProperty": "value"
                }
                """;

            FrontendRequest frontEndRequest = new(
                Path: "ed-fi/simpleresources",
                Body: jsonBody,
                Headers: [],
                QueryParameters: [],
                TraceId: new TraceId(""),
                new ClientAuthorizations(
                    TokenId: "",
                    ClaimSetName: "",
                    EducationOrganizationIds: [],
                    NamespacePrefixes: []
                )
            );

            _context = new PipelineContext(frontEndRequest, RequestMethod.POST)
            {
                ApiSchemaDocuments = schemaDocuments,
                PathComponents = new(
                    ProjectNamespace: new("ed-fi"),
                    EndpointName: new("simpleresources"),
                    DocumentUuid: No.DocumentUuid
                ),
            };
            _context.ProjectSchema = _context.ApiSchemaDocuments.FindProjectSchemaForProjectNamespace(
                new("ed-fi")
            )!;
            _context.ResourceSchema = new ResourceSchema(
                _context.ProjectSchema.FindResourceSchemaNodeByEndpointName(new("simpleresources"))
                    ?? new JsonObject()
            );

            var body = JsonNode.Parse(_context.FrontendRequest.Body!);
            if (body != null)
            {
                _context.ParsedBody = body;
            }

            await Middleware().Execute(_context, NullNext);
        }

        [Test]
        public void It_continues_to_next_middleware()
        {
            _context.FrontendResponse.Should().Be(No.FrontendResponse);
        }
    }
}
