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
public class DisallowDuplicateReferencesMiddlewareTests
{
    internal static ApiSchemaDocument DocRefSchemaDocument()
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
            .ToApiSchemaDocument();

        return result;
    }

    internal PipelineContext DocRefContext(FrontendRequest frontendRequest, RequestMethod method)
    {
        PipelineContext docRefContext =
            new(frontendRequest, method)
            {
                ApiSchemaDocument = DocRefSchemaDocument(),
                PathComponents = new(
                    ProjectNamespace: new("ed-fi"),
                    EndpointName: new("bellschedules"),
                    DocumentUuid: No.DocumentUuid
                )
            };
        docRefContext.ProjectSchema = new ProjectSchema(
            docRefContext.ApiSchemaDocument.FindProjectSchemaNode(new("ed-fi")) ?? new JsonObject(),
            NullLogger.Instance
        );
        docRefContext.ResourceSchema = new ResourceSchema(
            docRefContext.ProjectSchema.FindResourceSchemaNode(new("bellschedules")) ?? new JsonObject()
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

    internal static IPipelineStep BuildResourceInfo()
    {
        return new BuildResourceInfoMiddleware(NullLogger.Instance, new List<string>());
    }

    internal static IPipelineStep ExtractDocument()
    {
        return new ExtractDocumentInfoMiddleware(NullLogger.Instance);
    }

    // Middleware to test
    internal static IPipelineStep Middleware()
    {
        return new DisallowDuplicateReferencesMiddleware(NullLogger.Instance);
    }

    [TestFixture]
    public class Given_Pipeline_Context_With_Duplicate_Document_Reference
        : DisallowDuplicateReferencesMiddlewareTests
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

            FrontendRequest frontEndRequest =
                new(
                    Path: "ed-fi/bellschedules",
                    Body: jsonBody,
                    QueryParameters: [],
                    TraceId: new TraceId("")
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
            _context
                .FrontendResponse.Body?.ToJsonString()
                .Should()
                .Contain("Data Validation Failed");

            _context
                .FrontendResponse.Body?.ToJsonString()
                .Should()
                .Contain(
                    """
                    "validationErrors":{"$.ClassPeriod":["The 2nd item of the ClassPeriod has the same identifying values as another item earlier in the list."]}
                    """
                );
        }
    }

    // Happy path
    [TestFixture]
    public class Given_Pipeline_Context_With_One_Document_Reference
        : DisallowDuplicateReferencesMiddlewareTests
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

            FrontendRequest frontEndRequest =
                new(
                    Path: "ed-fi/bellschedules",
                    Body: jsonBody,
                    QueryParameters: [],
                    TraceId: new TraceId("")
                );

            _context = DocRefContext(frontEndRequest, RequestMethod.POST);

            await BuildResourceInfo().Execute(_context, NullNext);
            await ExtractDocument().Execute(_context, NullNext);

            await Middleware().Execute(_context, NullNext);
        }

        [Test]
        public void It_should_not_have_response()
        {
            _context?.FrontendResponse.Should().Be(No.FrontendResponse);
        }
    }

    // Descriptor Reference evaluation
    internal static ApiSchemaDocument DescRefSchemaDocument()
    {
        var result = new ApiSchemaBuilder()
            .WithStartProject()
            .WithStartResource("School")
            .WithStartDocumentPathsMapping()
            .WithDocumentPathDescriptor("GradeLevelDescriptor", "$.gradeLevels[*].gradeLevelDescriptor")
            .WithEndDocumentPathsMapping()
            .WithEndResource()
            .WithEndProject()
            .ToApiSchemaDocument();

        return result;
    }

    internal PipelineContext DescRefContext(FrontendRequest frontendRequest, RequestMethod method)
    {
        PipelineContext descRefContext =
            new(frontendRequest, method)
            {
                ApiSchemaDocument = DescRefSchemaDocument(),
                PathComponents = new(
                    ProjectNamespace: new("ed-fi"),
                    EndpointName: new("schools"),
                    DocumentUuid: No.DocumentUuid
                )
            };
        descRefContext.ProjectSchema = new ProjectSchema(
            descRefContext.ApiSchemaDocument.FindProjectSchemaNode(new("ed-fi")) ?? new JsonObject(),
            NullLogger.Instance
        );
        descRefContext.ResourceSchema = new ResourceSchema(
            descRefContext.ProjectSchema.FindResourceSchemaNode(new("schools")) ?? new JsonObject()
        );

        if (descRefContext.FrontendRequest.Body != null)
        {
            var body = JsonNode.Parse(descRefContext.FrontendRequest.Body);
            if (body != null)
            {
                descRefContext.ParsedBody = body;
            }
        }

        return descRefContext;
    }

    [TestFixture]
    public class Given_Pipeline_Context_With_Duplicate_Descriptor_Reference
        : DisallowDuplicateReferencesMiddlewareTests
    {
        private PipelineContext _context = No.PipelineContext();

        [SetUp]
        public async Task Setup()
        {
            string jsonBody = """
                {
                  "schoolId":255901001,
                  "nameOfInstitution":"School Test",
                  "gradeLevels": [
                      {
                        "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Sixth grade"
                      },
                      {
                        "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Sixth grade"
                      },
                      {
                        "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Sixth grade"
                      },
                      {
                        "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Sixth grade"
                      },
                      {
                        "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#First grade"
                      },
                      {
                        "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Second grade"
                      },
                      {
                        "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Third grade"
                      },
                      {
                        "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Fourth grade"
                      },
                      {
                        "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Fifth grade"
                      },
                      {
                        "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Seven grade"
                      },
                      {
                        "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Sixth grade"
                      }
                   ],  
                   "educationOrganizationCategories":[
                      {
                         "educationOrganizationCategoryDescriptor":"uri://ed-fi.org/educationOrganizationCategoryDescriptor#School"
                      }
                   ]
                }
                """;

            FrontendRequest frontEndRequest =
                new(Path: "ed-fi/schools", Body: jsonBody, QueryParameters: [], TraceId: new TraceId(""));

            _context = DescRefContext(frontEndRequest, RequestMethod.POST);

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
        public void It_returns_message_body_with_failure_duplicated_descriptor()
        {
            _context
                .FrontendResponse.Body?.ToJsonString()
                .Should()
                .Contain("Data Validation Failed");

            _context
                .FrontendResponse.Body?.ToJsonString()
                .Should()
                .Contain(
                    """
                    "validationErrors":{"$.gradeLevels[*].gradeLevelDescriptor":["The 2nd item of the gradeLevels has the same identifying values as another item earlier in the list.","The 3rd item of the gradeLevels has the same identifying values as another item earlier in the list.","The 4th item of the gradeLevels has the same identifying values as another item earlier in the list.","The 11th item of the gradeLevels has the same identifying values as another item earlier in the list."]}
                    """
                );
        }
    }

    [TestFixture]
    public class Given_Pipeline_Context_With_OTwo_Different_Descriptor_Reference
        : DisallowDuplicateReferencesMiddlewareTests
    {
        private PipelineContext _context = No.PipelineContext();

        [SetUp]
        public async Task Setup()
        {
            string jsonBody = """
                {
                  "schoolId":255901001,
                  "nameOfInstitution":"School Test",
                  "gradeLevels": [
                      {
                        "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Sixth grade"
                      },
                      {
                        "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Seven grade"
                      }
                   ]
                }
                """;

            FrontendRequest frontEndRequest =
                new(Path: "ed-fi/schools", Body: jsonBody, QueryParameters: [], TraceId: new TraceId(""));

            _context = DescRefContext(frontEndRequest, RequestMethod.POST);

            await BuildResourceInfo().Execute(_context, NullNext);
            await ExtractDocument().Execute(_context, NullNext);

            await Middleware().Execute(_context, NullNext);
        }

        [Test]
        public void It_should_not_have_response()
        {
            _context?.FrontendResponse.Should().Be(No.FrontendResponse);
        }
    }
}
