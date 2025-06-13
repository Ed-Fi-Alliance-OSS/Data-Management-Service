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
public class DuplicateReferenceValidationMiddlewareTests
{
    internal static ApiSchemaDocuments DocRefSchemaDocuments()
    {
        var result = new ApiSchemaBuilder()
            .WithStartProject()
            .WithStartResource("BellSchedule")
            .WithStartDocumentPathsMapping()
            .WithDocumentPathReference(
                "ClassPeriod",
                [
                    new("$.classPeriodName", "$.classPeriods[*].classPeriodReference.classPeriodName"),
                    new("$.schoolId", "$.classPeriods[*].classPeriodReference.schoolId"),
                ]
            )
            .WithEndDocumentPathsMapping()
            .WithEndResource()
            .WithEndProject()
            .ToApiSchemaDocuments();

        return result;
    }

    internal PipelineContext DocRefContext(FrontendRequest frontendRequest, RequestMethod method)
    {
        PipelineContext docRefContext = new(frontendRequest, method)
        {
            ApiSchemaDocuments = DocRefSchemaDocuments(),
            PathComponents = new(
                ProjectNamespace: new("ed-fi"),
                EndpointName: new("bellschedules"),
                DocumentUuid: No.DocumentUuid
            ),
        };
        docRefContext.ProjectSchema = docRefContext.ApiSchemaDocuments.FindProjectSchemaForProjectNamespace(
            new("ed-fi")
        )!;
        docRefContext.ResourceSchema = new ResourceSchema(
            docRefContext.ProjectSchema.FindResourceSchemaNodeByEndpointName(new("bellschedules"))
                ?? new JsonObject()
        );

        if (docRefContext.FrontendRequest.Body != null)
        {
            var body = JsonNode.Parse(docRefContext.FrontendRequest.Body);
            if (body != null)
            {
                docRefContext.ParsedBody = body;
            }
        }

        return docRefContext;
    }

    internal static BuildResourceInfoMiddleware BuildResourceInfo()
    {
        return new BuildResourceInfoMiddleware(NullLogger.Instance, new List<string>());
    }

    internal static ExtractDocumentInfoMiddleware ExtractDocument()
    {
        return new ExtractDocumentInfoMiddleware(NullLogger.Instance);
    }

    internal static DuplicateReferenceValidationMiddleware Middleware()
    {
        return new DuplicateReferenceValidationMiddleware(NullLogger.Instance);
    }

    [TestFixture]
    public class Given_Document_With_Duplicate_References : DuplicateReferenceValidationMiddlewareTests
    {
        private PipelineContext _context = No.PipelineContext();

        [SetUp]
        public async Task Setup()
        {
            string jsonBody = """
                {
                    "schoolReference": {
                        "schoolId": 1
                    },
                    "bellScheduleName": "Test Schedule",
                    "totalInstructionalTime": 325,
                    "classPeriods": [
                        {
                            "classPeriodReference": {
                                "classPeriodName": "01 - Traditional",
                                "schoolId": 1
                            }
                        },
                        {
                            "classPeriodReference": {
                                "classPeriodName": "01 - Traditional",
                                "schoolId": 1
                            }
                        }
                    ],
                    "dates": [],
                    "gradeLevels": []
                }
                """;

            FrontendRequest frontEndRequest = new(
                Path: "ed-fi/bellschedules",
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

            _context = DocRefContext(frontEndRequest, RequestMethod.POST);

            await BuildResourceInfo().Execute(_context, NullNext);
            await ExtractDocument().Execute(_context, NullNext);

            await Middleware().Execute(_context, NullNext);
        }

        [Test]
        public void It_returns_status_400()
        {
            _context.FrontendResponse.StatusCode.Should().Be(400);
        }

        [Test]
        public void It_returns_validation_error_with_duplicated_document_reference()
        {
            _context.FrontendResponse.Body?.ToJsonString().Should().Contain("Data Validation Failed");

            _context
                .FrontendResponse.Body?.ToJsonString()
                .Should()
                .Contain(
                    """
                    "validationErrors":{"$.classPeriods":["The 2nd item of the classPeriods has the same identifying values as another item earlier in the list."]}
                    """
                );
        }
    }

    [TestFixture]
    public class Given_Document_With_Unique_References : DuplicateReferenceValidationMiddlewareTests
    {
        private PipelineContext _context = No.PipelineContext();

        [SetUp]
        public async Task Setup()
        {
            string jsonBody = """
                {
                    "schoolReference": {
                        "schoolId": 1
                    },
                    "bellScheduleName": "Test Schedule",
                    "totalInstructionalTime": 325,
                    "classPeriods": [
                        {
                            "classPeriodReference": {
                                "classPeriodName": "01 - Traditional",
                                "schoolId": 1
                            }
                        },
                        {
                            "classPeriodReference": {
                                "classPeriodName": "02 - Block",
                                "schoolId": 1
                            }
                        }
                    ],
                    "dates": [],
                    "gradeLevels": []
                }
                """;

            FrontendRequest frontEndRequest = new(
                Path: "ed-fi/bellschedules",
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

            _context = DocRefContext(frontEndRequest, RequestMethod.POST);

            await BuildResourceInfo().Execute(_context, NullNext);
            await ExtractDocument().Execute(_context, NullNext);

            await Middleware().Execute(_context, NullNext);
        }

        [Test]
        public void It_continues_to_next_middleware()
        {
            _context.FrontendResponse.Should().Be(No.FrontendResponse);
        }
    }

    [TestFixture]
    public class Given_Document_With_Single_Reference : DuplicateReferenceValidationMiddlewareTests
    {
        private PipelineContext _context = No.PipelineContext();

        [SetUp]
        public async Task Setup()
        {
            string jsonBody = """
                {
                    "schoolReference": {
                        "schoolId": 1
                    },
                    "bellScheduleName": "Test Schedule",
                    "totalInstructionalTime": 325,
                    "classPeriods": [
                        {
                            "classPeriodReference": {
                                "classPeriodName": "01 - Traditional",
                                "schoolId": 1
                            }
                        }
                    ],
                    "dates": [],
                    "gradeLevels": []
                }
                """;

            FrontendRequest frontEndRequest = new(
                Path: "ed-fi/bellschedules",
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

            _context = DocRefContext(frontEndRequest, RequestMethod.POST);

            await BuildResourceInfo().Execute(_context, NullNext);
            await ExtractDocument().Execute(_context, NullNext);

            await Middleware().Execute(_context, NullNext);
        }

        [Test]
        public void It_continues_to_next_middleware()
        {
            _context.FrontendResponse.Should().Be(No.FrontendResponse);
        }
    }

    [TestFixture]
    public class Given_Document_With_No_Reference_Arrays : DuplicateReferenceValidationMiddlewareTests
    {
        private PipelineContext _context = No.PipelineContext();

        [SetUp]
        public async Task Setup()
        {
            string jsonBody = """
                {
                    "schoolReference": {
                        "schoolId": 1
                    },
                    "bellScheduleName": "Test Schedule",
                    "totalInstructionalTime": 325
                }
                """;

            FrontendRequest frontEndRequest = new(
                Path: "ed-fi/bellschedules",
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

            _context = DocRefContext(frontEndRequest, RequestMethod.POST);

            await BuildResourceInfo().Execute(_context, NullNext);
            await ExtractDocument().Execute(_context, NullNext);

            await Middleware().Execute(_context, NullNext);
        }

        [Test]
        public void It_continues_to_next_middleware()
        {
            _context.FrontendResponse.Should().Be(No.FrontendResponse);
        }
    }
}
