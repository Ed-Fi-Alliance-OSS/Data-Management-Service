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
public class ValidateRepeatedPropertiesMiddlewareTests
{
    internal static IPipelineStep Middleware()
    {
        return new ValidateRepeatedPropertiesMiddleware(NullLogger.Instance);
    }

    [TestFixture]
    public class Given_Pipeline_Context_With_Duplicate_Property_On_First_Level
        : ValidateRepeatedPropertiesMiddlewareTests
    {
        private PipelineContext _context = No.PipelineContext();

        [SetUp]
        public async Task Setup()
        {
            string jsonBody = """
                {
                  "schoolId":255901001,
                  "schoolId":255901001,
                  "nameOfInstitution":"School Test",
                  "gradeLevels":[
                      {
                          "gradeLevelDescriptor":"uri://ed-fi.org/gradeLevelDescriptor#Ninth grade"
                      }
                  ],
                  "educationOrganizationCategories":[
                      {
                          "educationOrganizationCategoryDescriptor":"uri://ed-fi.org/educationOrganizationCategoryDescriptor#School"
                      }
                  ]
                }
                """;
            var frontEndRequest = new FrontendRequest(
                "ed-fi/schools",
                Body: jsonBody,
                QueryParameters: [],
                new TraceId("traceId")
            );
            _context = new(frontEndRequest, RequestMethod.POST);
            await Middleware().Execute(_context, NullNext);
        }

        [Test]
        public void It_returns_status_400()
        {
            _context?.FrontendResponse.StatusCode.Should().Be(400);
        }

        [Test]
        public void It_returns_message_body_with_failure_duplicated_property()
        {
            _context?.FrontendResponse.Body.Should().Contain("Data Validation Failed");
            _context
                ?.FrontendResponse.Body.Should()
                .Contain(
                    """
                    "validationErrors":{"$.schoolId":["This property is duplicated."]}
                    """
                );
        }
    }

    [TestFixture]
    public class Given_Pipeline_Context_With_A_Collection_As_Duplicated_Property_On_First_Level
        : ValidateRepeatedPropertiesMiddlewareTests
    {
        private PipelineContext _context = No.PipelineContext();

        [SetUp]
        public async Task Setup()
        {
            string jsonBody = """
                              {
                                "schoolId":255901001,
                                "nameOfInstitution":"School Test",
                                "gradeLevels":[
                                    {
                                        "gradeLevelDescriptor":"uri://ed-fi.org/gradeLevelDescriptor#Ninth grade"
                                    }
                                ],
                                "gradeLevels":[
                                  {
                                      "gradeLevelDescriptor":"uri://ed-fi.org/gradeLevelDescriptor#Ten grade"
                                  }
                                ],
                                "educationOrganizationCategories":[
                                    {
                                        "educationOrganizationCategoryDescriptor":"uri://ed-fi.org/educationOrganizationCategoryDescriptor#School"
                                    }
                                ]
                              }
                              """;
            var frontEndRequest = new FrontendRequest(
                "ed-fi/schools",
                Body: jsonBody,
                QueryParameters: [],
                new TraceId("traceId")
            );
            _context = new(frontEndRequest, RequestMethod.POST);
            await Middleware().Execute(_context, NullNext);
        }

        [Test]
        public void It_returns_status_400()
        {
            _context?.FrontendResponse.StatusCode.Should().Be(400);
        }

        [Test]
        public void It_returns_message_body_with_failure_duplicated_property()
        {
            _context?.FrontendResponse.Body.Should().Contain("Data Validation Failed");
            _context
                ?.FrontendResponse.Body.Should()
                .Contain(
                    """
                    "validationErrors":{"$.gradeLevels":["This property is duplicated."]}
                    """
                );
        }
    }

    [TestFixture]
    public class Given_Pipeline_Context_With_Duplicate_Property_Inside_Of_A_Collection
        : ValidateRepeatedPropertiesMiddlewareTests
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
                              				  "classPeriodName": "01 - Traditional",
                                              "schoolId": 1
                                          }
                                      }
                                  ]
                              }
                              """;
            var frontEndRequest = new FrontendRequest(
                "ed-fi/bellschedules",
                Body: jsonBody,
                QueryParameters: [],
                new TraceId("traceId")
            );
            _context = new(frontEndRequest, RequestMethod.POST);
            await Middleware().Execute(_context, NullNext);
        }

        [Test]
        public void It_returns_status_400()
        {
            _context?.FrontendResponse.StatusCode.Should().Be(400);
        }

        [Test]
        public void It_returns_message_body_with_failure_duplicated_property()
        {
            _context?.FrontendResponse.Body.Should().Contain("Data Validation Failed");
            _context
                ?.FrontendResponse.Body.Should()
                .Contain(
                    """
                    "validationErrors":{"$.classPeriods[0].classPeriodReference.classPeriodName":["This property is duplicated."]}
                    """
                );
        }
    }

    [TestFixture]
    public class Given_Pipeline_Context_With_Same_Value_Inside_An_Array_Of_Properties
        : ValidateRepeatedPropertiesMiddlewareTests
    {
        private PipelineContext _context = No.PipelineContext();

        [SetUp]
        public async Task Setup()
        {
            string jsonBody = """
                              {
                                "schoolId":255901001,
                                "nameOfInstitution":"School Test",
                                "gradeLevels":[
                                    {
                                        "gradeLevelDescriptor":"uri://ed-fi.org/gradeLevelDescriptor#Ninth grade"
                                    }
                                ],
                                "gradeLevels":[
                                  {
                                      "gradeLevelDescriptor":"uri://ed-fi.org/gradeLevelDescriptor#Ninth grade"
                                  }
                                ],
                                "educationOrganizationCategories":[
                                    {
                                        "educationOrganizationCategoryDescriptor":"uri://ed-fi.org/educationOrganizationCategoryDescriptor#School"
                                    }
                                ]
                              }
                              """;
            var frontEndRequest = new FrontendRequest(
                "ed-fi/schools",
                Body: jsonBody,
                QueryParameters: [],
                new TraceId("traceId")
            );
            _context = new(frontEndRequest, RequestMethod.POST);
            await Middleware().Execute(_context, NullNext);
        }

        [Test]
        public void It_returns_status_400()
        {
            _context?.FrontendResponse.StatusCode.Should().Be(400);
        }

        [Test]
        public void It_returns_message_body_with_failure_duplicated_property()
        {
            _context?.FrontendResponse.Body.Should().Contain("Data Validation Failed");
            _context
                ?.FrontendResponse.Body.Should()
                .Contain(
                    """
                    "validationErrors":{"$.gradeLevels":["This property is duplicated."]}
                    """
                );
        }
    }
    [TestFixture]
    public class Given_Pipeline_Context_With_Duplicate_Property_And_Same_Value_Inside_An_Array_Of_Properties
        : ValidateRepeatedPropertiesMiddlewareTests
    {
        private PipelineContext _context = No.PipelineContext();

        [SetUp]
        public async Task Setup()
        {
            string jsonBody = """
                              {
                                "schoolId":255901001,
                                "schoolId":255901001,
                                "nameOfInstitution":"School Test",
                                "gradeLevels":[
                                    {
                                        "gradeLevelDescriptor":"uri://ed-fi.org/gradeLevelDescriptor#Ninth grade"
                                    }
                                ],
                                "gradeLevels":[
                                  {
                                      "gradeLevelDescriptor":"uri://ed-fi.org/gradeLevelDescriptor#Ninth grade"
                                  }
                                ],
                                "educationOrganizationCategories":[
                                    {
                                        "educationOrganizationCategoryDescriptor":"uri://ed-fi.org/educationOrganizationCategoryDescriptor#School"
                                    }
                                ]
                              }
                              """;
            var frontEndRequest = new FrontendRequest(
                "ed-fi/schools",
                Body: jsonBody,
                QueryParameters: [],
                new TraceId("traceId")
            );
            _context = new(frontEndRequest, RequestMethod.POST);
            await Middleware().Execute(_context, NullNext);
        }

        [Test]
        public void It_returns_status_400()
        {
            _context?.FrontendResponse.StatusCode.Should().Be(400);
        }

        [Test]
        public void It_returns_message_body_with_failure_duplicated_property()
        {
            _context?.FrontendResponse.Body.Should().Contain("Data Validation Failed");
            _context
                ?.FrontendResponse.Body.Should()
                .Contain(
                    """
                    "validationErrors":{"$.schoolId":["This property is duplicated."],"$.gradeLevels":["This property is duplicated."]}
                    """
                );
        }
    }
}
