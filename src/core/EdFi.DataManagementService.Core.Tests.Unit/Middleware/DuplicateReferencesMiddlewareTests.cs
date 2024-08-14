// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

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
public class DuplicateReferencesMiddlewareTests
{
    internal static IPipelineStep Middleware()
    {
        return new DuplicateReferencesMiddleware(NullLogger.Instance);
    }

    [TestFixture]
    public class Given_Pipeline_Context_With_Duplicate_Document_Reference : DuplicateReferencesMiddlewareTests
    {
        private PipelineContext _context = No.PipelineContext();

        [SetUp]
        public async Task Setup()
        {
            var jsonBody = """
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

            _context = new(frontEndRequest, RequestMethod.POST);
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
            _context.FrontendResponse.Body.Should().Contain("Data Validation Failed");
            _context
                .FrontendResponse.Body.Should()
                .Contain(
                    """
                    "validationErrors":{"$.ClassPeriod":["The 2nd item of the ClassPeriod has the same identifying values as another item earlier in the list."]}
                    """
                );
        }
    }

    [TestFixture]
    public class Given_Pipeline_Context_With_Duplicate_Descriptor_Reference
        : DuplicateReferencesMiddlewareTests
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
            _context = new(frontEndRequest, RequestMethod.POST);
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
            _context.FrontendResponse.Body.Should().Contain("Data Validation Failed");
            _context
                .FrontendResponse.Body.Should()
                .Contain(
                    """
                    "validationErrors": {
                        "$.gradeLevels[*].gradeLevelDescriptor": [
                            "The 3rd item of the gradeLevels has the same identifying values as another item earlier in the list.",
                            "The 4th item of the gradeLevels has the same identifying values as another item earlier in the list."
                        ]
                    }
                    """
                );
        }
    }
}
