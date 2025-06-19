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
public class ReferenceArrayUniquenessValidationMiddlewareTests
{
    internal static ApiSchemaDocuments BellScheduleApiSchema()
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

    internal static async Task<PipelineContext> CreateContextAndExecute(
        ApiSchemaDocuments apiSchema,
        string jsonBody,
        string endpointName,
        RequestMethod method
    )
    {
        FrontendRequest frontEndRequest = new(
            Path: $"ed-fi/{endpointName}",
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

        PipelineContext context = new(frontEndRequest, method)
        {
            ApiSchemaDocuments = apiSchema,
            PathComponents = new(
                ProjectNamespace: new("ed-fi"),
                EndpointName: new(endpointName),
                DocumentUuid: No.DocumentUuid
            ),
        };
        context.ProjectSchema = context.ApiSchemaDocuments.FindProjectSchemaForProjectNamespace(
            new("ed-fi")
        )!;
        context.ResourceSchema = new ResourceSchema(
            context.ProjectSchema.FindResourceSchemaNodeByEndpointName(new(endpointName)) ?? new JsonObject()
        );

        var body = JsonNode.Parse(context.FrontendRequest.Body!);
        if (body != null)
        {
            context.ParsedBody = body;
        }

        await BuildResourceInfo().Execute(context, NullNext);
        await ExtractDocument().Execute(context, NullNext);
        await Middleware().Execute(context, NullNext);
        return context;
    }

    internal static BuildResourceInfoMiddleware BuildResourceInfo()
    {
        return new BuildResourceInfoMiddleware(NullLogger.Instance, new List<string>());
    }

    internal static ExtractDocumentInfoMiddleware ExtractDocument()
    {
        return new ExtractDocumentInfoMiddleware(NullLogger.Instance);
    }

    internal static ReferenceArrayUniquenessValidationMiddleware Middleware()
    {
        return new ReferenceArrayUniquenessValidationMiddleware(NullLogger.Instance);
    }

    [TestFixture]
    public class Given_Document_With_Duplicate_References : ReferenceArrayUniquenessValidationMiddlewareTests
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

            _context = await CreateContextAndExecute(
                BellScheduleApiSchema(),
                jsonBody,
                "bellschedules",
                RequestMethod.POST
            );
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
    public class Given_Document_With_Unique_References : ReferenceArrayUniquenessValidationMiddlewareTests
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

            _context = await CreateContextAndExecute(
                BellScheduleApiSchema(),
                jsonBody,
                "bellschedules",
                RequestMethod.POST
            );
        }

        [Test]
        public void It_continues_to_next_middleware()
        {
            _context.FrontendResponse.Should().Be(No.FrontendResponse);
        }
    }

    [TestFixture]
    public class Given_Document_With_Single_Reference : ReferenceArrayUniquenessValidationMiddlewareTests
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

            _context = await CreateContextAndExecute(
                BellScheduleApiSchema(),
                jsonBody,
                "bellschedules",
                RequestMethod.POST
            );
        }

        [Test]
        public void It_continues_to_next_middleware()
        {
            _context.FrontendResponse.Should().Be(No.FrontendResponse);
        }
    }

    [TestFixture]
    public class Given_Document_With_No_Reference_Arrays : ReferenceArrayUniquenessValidationMiddlewareTests
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

            _context = await CreateContextAndExecute(
                BellScheduleApiSchema(),
                jsonBody,
                "bellschedules",
                RequestMethod.POST
            );
        }

        [Test]
        public void It_continues_to_next_middleware()
        {
            _context.FrontendResponse.Should().Be(No.FrontendResponse);
        }
    }
}
