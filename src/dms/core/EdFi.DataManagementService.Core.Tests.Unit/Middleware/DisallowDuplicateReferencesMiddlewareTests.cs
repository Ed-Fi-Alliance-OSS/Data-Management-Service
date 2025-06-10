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
        public void It_should_not_have_response()
        {
            _context?.FrontendResponse.Should().Be(No.FrontendResponse);
        }
    }

    // Descriptor Reference evaluation
    internal static ApiSchemaDocuments DescRefSchemaDocuments()
    {
        var result = new ApiSchemaBuilder()
            .WithStartProject()
            .WithStartResource("School")
            .WithStartDocumentPathsMapping()
            .WithDocumentPathDescriptor("GradeLevelDescriptor", "$.gradeLevels[*].gradeLevelDescriptor")
            .WithEndDocumentPathsMapping()
            .WithStartArrayUniquenessConstraints()
            .WithArrayUniquenessConstraints(["$.gradeLevels[*].gradeLevelDescriptor"])
            .WithEndArrayUniquenessConstraints()
            .WithEndResource()
            .WithEndProject()
            .ToApiSchemaDocuments();

        return result;
    }

    internal PipelineContext DescRefContext(FrontendRequest frontendRequest, RequestMethod method)
    {
        PipelineContext descRefContext = new(frontendRequest, method)
        {
            ApiSchemaDocuments = DescRefSchemaDocuments(),
            PathComponents = new(
                ProjectNamespace: new("ed-fi"),
                EndpointName: new("schools"),
                DocumentUuid: No.DocumentUuid
            ),
        };
        descRefContext.ProjectSchema = descRefContext.ApiSchemaDocuments.FindProjectSchemaForProjectNamespace(
            new("ed-fi")
        )!;
        descRefContext.ResourceSchema = new ResourceSchema(
            descRefContext.ProjectSchema.FindResourceSchemaNodeByEndpointName(new("schools"))
                ?? new JsonObject()
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

    // Duplicate Descriptor Reference evaluation
    internal static ApiSchemaDocuments DuplicateDescRefSchemaDocuments()
    {
        var result = new ApiSchemaBuilder()
            .WithStartProject()
            .WithStartResource("Assessment")
            .WithStartDocumentPathsMapping()
            .WithDocumentPathDescriptor(
                "AcademicSubjectDescriptor",
                "$.academicSubjects[*].academicSubjectDescriptor"
            )
            .WithDocumentPathDescriptor(
                "PerformanceLevelDescriptor",
                "$.performanceLevels[*].performanceLevelDescriptor"
            )
            .WithDocumentPathDescriptor(
                "AssessmentPerformanceLevel.AssessmentReportingMethodDescriptor",
                "$.performanceLevels[*].assessmentReportingMethodDescriptor"
            )
            .WithDocumentPathDescriptor(
                "AssessmentScore.AssessmentReportingMethodDescriptor",
                "$.scores[*].assessmentReportingMethodDescriptor"
            )
            .WithDocumentPathDescriptor(
                "ResultDatatypeTypeDescriptor",
                "$.scores[*].resultDatatypeTypeDescriptor"
            )
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
            .WithArrayUniquenessConstraints(
                [
                    "$.items[*].assessmentItemReference.assessmentIdentifier",
                    "$.items[*].assessmentItemReference.identificationCode",
                    "$.items[*].assessmentItemReference.namespace",
                ]
            )
            .WithArrayUniquenessConstraints(
                [
                    "$.requiredImmunizations[*].dates[*].immunizationDate",
                    "$.requiredImmunizations[*].immunizationTypeDescriptor",
                ]
            )
            .WithEndArrayUniquenessConstraints()
            .WithEndResource()
            .WithEndProject()
            .ToApiSchemaDocuments();

        return result;
    }

    internal PipelineContext DuplicateDescRefContext(FrontendRequest frontendRequest, RequestMethod method)
    {
        PipelineContext refContext = new(frontendRequest, method)
        {
            ApiSchemaDocuments = DuplicateDescRefSchemaDocuments(),
            PathComponents = new(
                ProjectNamespace: new("ed-fi"),
                EndpointName: new("assessments"),
                DocumentUuid: No.DocumentUuid
            ),
        };
        refContext.ProjectSchema = refContext.ApiSchemaDocuments.FindProjectSchemaForProjectNamespace(
            new("ed-fi")
        )!;
        refContext.ResourceSchema = new ResourceSchema(
            refContext.ProjectSchema.FindResourceSchemaNodeByEndpointName(new("assessments"))
                ?? new JsonObject()
        );

        if (refContext.FrontendRequest.Body != null)
        {
            var body = JsonNode.Parse(refContext.FrontendRequest.Body);
            if (body != null)
            {
                refContext.ParsedBody = body;
            }
        }

        return refContext;
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
                        "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Seventh grade"
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

            FrontendRequest frontEndRequest = new(
                Path: "ed-fi/schools",
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
            _context.FrontendResponse.Body?.ToJsonString().Should().Contain("Data Validation Failed");

            _context
                .FrontendResponse.Body?.ToJsonString()
                .Should()
                .Contain(
                    """
                    "validationErrors":{"$.gradeLevels":["The 2nd item of the gradeLevels has the same identifying values as another item earlier in the list."]}
                    """
                );
        }
    }

    [TestFixture]
    public class Given_Pipeline_Context_Has_Combined_Unique_Descriptors_References
        : DisallowDuplicateReferencesMiddlewareTests
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
                 "academicSubjects": [
                   {
                     "academicSubjectDescriptor": "uri://ed-fi.org/AcademicSubjectDescriptor#English Language Arts"
                   }
                 ],
                 "performanceLevels": [
                   {
                     "performanceLevelDescriptor": "uri://ed-fi.org/PerformanceLevelDescriptor#Advanced",
                     "assessmentReportingMethodDescriptor": "uri://ed-fi.org/AssessmentReportingMethodDescriptor#Scale score",
                     "minimumScore": "23",
                     "maximumScore": "26"
                   },
                   {
                     "performanceLevelDescriptor": "uri://ed-fi.org/PerformanceLevelDescriptor#Below Basic",
                     "assessmentReportingMethodDescriptor": "uri://ed-fi.org/AssessmentReportingMethodDescriptor#Scale score",
                     "minimumScore": "27",
                     "maximumScore": "30"
                   }
                 ],
                 "scores": [
                   {
                     "assessmentReportingMethodDescriptor": "uri://ed-fi.org/AssessmentReportingMethodDescriptor#Raw score",
                     "maximumScore": "10",
                     "minimumScore": "0",
                     "resultDatatypeTypeDescriptor": "uri://ed-fi.org/ResultDatatypeTypeDescriptor#Integer"
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

            _context = DuplicateDescRefContext(frontEndRequest, RequestMethod.POST);

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

    [TestFixture]
    public class Given_Pipeline_Context_Has_Combined_Duplicate_Descriptors_References
        : DisallowDuplicateReferencesMiddlewareTests
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
                 "academicSubjects": [
                   {
                     "academicSubjectDescriptor": "uri://ed-fi.org/AcademicSubjectDescriptor#English Language Arts"
                   }
                 ],
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
                 ],
                 "scores": [
                   {
                     "assessmentReportingMethodDescriptor": "uri://ed-fi.org/AssessmentReportingMethodDescriptor#Raw score",
                     "maximumScore": "10",
                     "minimumScore": "0",
                     "resultDatatypeTypeDescriptor": "uri://ed-fi.org/ResultDatatypeTypeDescriptor#Integer"
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

            _context = DuplicateDescRefContext(frontEndRequest, RequestMethod.POST);

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
    public class Given_Pipeline_Context_With_Two_Different_Descriptor_Reference
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

            FrontendRequest frontEndRequest = new(
                Path: "ed-fi/schools",
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

    [TestFixture]
    public class Given_Pipeline_Context_Has_Combined_Unique_References
        : DisallowDuplicateReferencesMiddlewareTests
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
                 "academicSubjects": [
                   {
                     "academicSubjectDescriptor": "uri://ed-fi.org/AcademicSubjectDescriptor#English Language Arts"
                   }
                 ],
                 "performanceLevels": [
                   {
                     "performanceLevelDescriptor": "uri://ed-fi.org/PerformanceLevelDescriptor#Advanced",
                     "assessmentReportingMethodDescriptor": "uri://ed-fi.org/AssessmentReportingMethodDescriptor#Scale score",
                     "minimumScore": "23",
                     "maximumScore": "26"
                   }
                 ],
                 "items": [
                   {
                     "assessmentResponse": "G",
                     "responseIndicatorDescriptor": "uri://ed-fi.org/ResponseIndicatorDescriptor#Nonscorable response",
                     "assessmentItemResultDescriptor": "uri://ed-fi.org/AssessmentItemResultDescriptor#Correct",
                     "assessmentItemReference": {
                       "identificationCode": "9848478",
                       "assessmentIdentifier": "ae049cb3-33d0-431f-b0f3-a751df7217ef",
                       "namespace": "uri://ed-fi.org/Assessment/Assessment.xml"
                     }
                   },
                   {
                     "assessmentResponse": "G",
                     "responseIndicatorDescriptor": "uri://ed-fi.org/ResponseIndicatorDescriptor#Nonscorable response",
                     "assessmentItemResultDescriptor": "uri://ed-fi.org/AssessmentItemResultDescriptor#Correct",
                     "assessmentItemReference": {
                       "identificationCode": "9848478",
                       "assessmentIdentifier": "ae049cb3-33d0-431f-b0f3-a751df7217ef",
                       "namespace": "uri://ed-fi.org/Assessment/Assessment.xml"
                     }
                   }
                 ],
                 "scores": [
                   {
                     "assessmentReportingMethodDescriptor": "uri://ed-fi.org/AssessmentReportingMethodDescriptor#Raw score",
                     "maximumScore": "10",
                     "minimumScore": "0",
                     "resultDatatypeTypeDescriptor": "uri://ed-fi.org/ResultDatatypeTypeDescriptor#Integer"
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

            _context = DuplicateDescRefContext(frontEndRequest, RequestMethod.POST);

            await BuildResourceInfo().Execute(_context, NullNext);
            await ExtractDocument().Execute(_context, NullNext);

            await Middleware().Execute(_context, NullNext);
        }

        [Test]
        public void It_returns_status_400()
        {
            _context.FrontendResponse.StatusCode.Should().Be(400);

            _context.FrontendResponse.Body?.ToJsonString().Should().Contain("Data Validation Failed");

            _context
                .FrontendResponse.Body?.ToJsonString()
                .Should()
                .Contain(
                    """
                    "validationErrors":{"$.items":["The 2nd item of the items has the same identifying values as another item earlier in the list."]}
                    """
                );
        }
    }

    [TestFixture]
    public class Given_Pipeline_Context_Has_Not_Flattern_Unique_References
        : DisallowDuplicateReferencesMiddlewareTests
    {
        private PipelineContext _context = No.PipelineContext();

        [SetUp]
        public async Task Setup()
        {
            string jsonBody = """
                {
                 "requiredImmunizations": [
                    {
                        "dates": [
                            {
                                "immunizationDate": "2007-07-01"
                            }
                        ],
                        "immunizationTypeDescriptor": "uri://ed-fi.org/ImmunizationTypeDescriptor#MMR"
                    },
                    {
                        "dates": [
                            {
                                "immunizationDate": "2010-04-01"
                            }
                        ],
                        "immunizationTypeDescriptor": "uri://ed-fi.org/ImmunizationTypeDescriptor#IPV"
                    }
                  ]
                }
                """;
            FrontendRequest frontEndRequest = new(
                Path: "ed-fi/requiredImmunizations",
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
            _context = DuplicateDescRefContext(frontEndRequest, RequestMethod.POST);

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
