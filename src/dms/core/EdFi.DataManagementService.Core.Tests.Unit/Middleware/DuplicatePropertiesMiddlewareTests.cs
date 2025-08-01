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
[Parallelizable]
public class DuplicatePropertiesMiddlewareTest
{
    internal static IPipelineStep Middleware()
    {
        return new DuplicatePropertiesMiddleware(NullLogger.Instance);
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Pipeline_Context_With_Duplicate_Property_On_First_Level
        : DuplicatePropertiesMiddlewareTest
    {
        private RequestInfo _requestInfo = No.RequestInfo();

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
                Path: "ed-fi/schools",
                Body: jsonBody,
                Headers: [],
                QueryParameters: [],
                TraceId: new TraceId("traceId")
            );
            _requestInfo = new(frontEndRequest, RequestMethod.POST);
            await Middleware().Execute(_requestInfo, NullNext);
        }

        [Test]
        public void It_returns_status_400()
        {
            _requestInfo?.FrontendResponse.StatusCode.Should().Be(400);
        }

        [Test]
        public void It_returns_message_body_with_failure_duplicated_property()
        {
            _requestInfo.FrontendResponse.Body?.ToJsonString().Should().Contain("Data Validation Failed");

            _requestInfo
                .FrontendResponse.Body?.ToJsonString()
                .Should()
                .Contain(
                    """
                    "validationErrors":{"$.schoolId":["An item with the same key has already been added."]}
                    """
                );
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Pipeline_Context_With_A_Collection_As_Duplicated_Property_On_First_Level
        : DuplicatePropertiesMiddlewareTest
    {
        private RequestInfo _requestInfo = No.RequestInfo();

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
                Path: "ed-fi/schools",
                Body: jsonBody,
                Headers: [],
                QueryParameters: [],
                TraceId: new TraceId("traceId")
            );
            _requestInfo = new(frontEndRequest, RequestMethod.POST);
            await Middleware().Execute(_requestInfo, NullNext);
        }

        [Test]
        public void It_returns_status_400()
        {
            _requestInfo?.FrontendResponse.StatusCode.Should().Be(400);
        }

        [Test]
        public void It_returns_message_body_with_failure_duplicated_property()
        {
            _requestInfo
                ?.FrontendResponse.Body?.ToJsonString()
                .ToString()
                .Should()
                .Contain("Data Validation Failed");

            _requestInfo
                ?.FrontendResponse.Body?.ToJsonString()
                .ToString()
                .Should()
                .Contain(
                    """
                    "validationErrors":{"$.gradeLevels":["An item with the same key has already been added."]}
                    """
                );
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Pipeline_Context_With_Duplicate_Property_Inside_Of_A_Collection
        : DuplicatePropertiesMiddlewareTest
    {
        private RequestInfo _requestInfo = No.RequestInfo();

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
                Path: "ed-fi/bellschedules",
                Body: jsonBody,
                Headers: [],
                QueryParameters: [],
                TraceId: new TraceId("traceId")
            );
            _requestInfo = new(frontEndRequest, RequestMethod.POST);
            await Middleware().Execute(_requestInfo, NullNext);
        }

        [Test]
        public void It_returns_status_400()
        {
            _requestInfo?.FrontendResponse.StatusCode.Should().Be(400);
        }

        [Test]
        public void It_returns_message_body_with_failure_duplicated_property()
        {
            _requestInfo.FrontendResponse.Body?.ToJsonString().Should().Contain("Data Validation Failed");

            _requestInfo
                .FrontendResponse.Body?.ToJsonString()
                .Should()
                .Contain(
                    """
                    "validationErrors":{"$.classPeriods[0].classPeriodReference.classPeriodName":["An item with the same key has already been added."]}
                    """
                );
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Pipeline_Context_With_Same_Value_Inside_An_Array_Of_Properties
        : DuplicatePropertiesMiddlewareTest
    {
        private RequestInfo _requestInfo = No.RequestInfo();

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
                Path: "ed-fi/schools",
                Body: jsonBody,
                Headers: [],
                QueryParameters: [],
                TraceId: new TraceId("traceId")
            );
            _requestInfo = new(frontEndRequest, RequestMethod.POST);
            await Middleware().Execute(_requestInfo, NullNext);
        }

        [Test]
        public void It_returns_status_400()
        {
            _requestInfo?.FrontendResponse.StatusCode.Should().Be(400);
        }

        [Test]
        public void It_returns_message_body_with_failure_duplicated_property()
        {
            _requestInfo
                .FrontendResponse.Body?.ToJsonString()
                .Should()
                .ContainAll(
                    "Data Validation Failed",
                    """
                    "validationErrors":{"$.gradeLevels":["An item with the same key has already been added."]}
                    """
                );
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Pipeline_Context_With_Duplicate_Property_And_Same_Value_Inside_An_Array_Of_Properties
        : DuplicatePropertiesMiddlewareTest
    {
        private RequestInfo _requestInfo = No.RequestInfo();

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
                Path: "ed-fi/schools",
                Body: jsonBody,
                Headers: [],
                QueryParameters: [],
                TraceId: new TraceId("traceId")
            );
            _requestInfo = new(frontEndRequest, RequestMethod.POST);
            await Middleware().Execute(_requestInfo, NullNext);
        }

        [Test]
        public void It_returns_status_400()
        {
            _requestInfo?.FrontendResponse.StatusCode.Should().Be(400);
        }

        [Test]
        public void It_returns_message_body_with_failure_duplicated_property()
        {
            _requestInfo.FrontendResponse.Body?.ToJsonString().Should().Contain("Data Validation Failed");

            _requestInfo
                .FrontendResponse.Body?.ToJsonString()
                .Should()
                .Contain(
                    """
                    "validationErrors":{"$.schoolId":["An item with the same key has already been added."]}
                    """
                );
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Pipeline_Context_With_Duplicate_Property_On_First_Level_Using_Put
        : DuplicatePropertiesMiddlewareTest
    {
        private RequestInfo _requestInfo = No.RequestInfo();
        private readonly string id = Guid.NewGuid().ToString();

        [SetUp]
        public async Task Setup()
        {
            string jsonBody = """
                {
                    "id": "{{id}}",
                    "studentUniqueId":"123",
                    "birthDate":"2000-01-01",
                    "firstName":"Thomas",
                    "firstName":"John",
                    "lastSurname":"Muller"
                }
                """;
            var frontEndRequest = new FrontendRequest(
                Path: $"ed-fi/students/{id}",
                Body: jsonBody,
                Headers: [],
                QueryParameters: [],
                TraceId: new TraceId("traceId")
            );
            _requestInfo = new(frontEndRequest, RequestMethod.PUT);
            await Middleware().Execute(_requestInfo, NullNext);
        }

        [Test]
        public void It_returns_status_400()
        {
            _requestInfo?.FrontendResponse.StatusCode.Should().Be(400);
        }

        [Test]
        public void It_returns_message_body_with_failure_duplicated_property()
        {
            _requestInfo.FrontendResponse.Body?.ToJsonString().Should().Contain("Data Validation Failed");

            _requestInfo
                .FrontendResponse.Body?.ToJsonString()
                .Should()
                .Contain(
                    """
                    "validationErrors":{"$.firstName":["An item with the same key has already been added."]}
                    """
                );
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Pipeline_Context_With_Duplicate_Property_And_Same_Value_Inside_An_Array_Of_Properties_Using_Put
        : DuplicatePropertiesMiddlewareTest
    {
        private RequestInfo _requestInfo = No.RequestInfo();
        private readonly string id = Guid.NewGuid().ToString();

        [SetUp]
        public async Task Setup()
        {
            string jsonBody = $$"""
                {
                  "id": "{{id}}",
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
                Path: $"ed-fi/schools/{id}",
                Body: jsonBody,
                Headers: [],
                QueryParameters: [],
                TraceId: new TraceId("traceId")
            );
            _requestInfo = new(frontEndRequest, RequestMethod.PUT);
            await Middleware().Execute(_requestInfo, NullNext);
        }

        [Test]
        public void It_returns_status_400()
        {
            _requestInfo?.FrontendResponse.StatusCode.Should().Be(400);
        }

        [Test]
        public void It_returns_message_body_with_failure_duplicated_property()
        {
            _requestInfo.FrontendResponse.Body?.ToJsonString().Should().Contain("Data Validation Failed");

            _requestInfo
                .FrontendResponse.Body?.ToJsonString()
                .Should()
                .Contain(
                    """
                    "correlationId":"traceId"
                    """
                );

            _requestInfo
                .FrontendResponse.Body?.ToJsonString()
                .Should()
                .Contain(
                    """
                    "validationErrors":{"$.schoolId":["An item with the same key has already been added."]
                    """
                );
        }
    }
}
