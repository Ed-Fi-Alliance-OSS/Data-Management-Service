// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
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
    private const string ProblemJson = "application/problem+json";

    internal static IPipelineStep Middleware()
    {
        return new DuplicatePropertiesMiddleware(NullLogger.Instance);
    }

    internal static IPipelineStep ProblemJsonMiddleware()
    {
        return new DuplicatePropertiesMiddleware(NullLogger.Instance, errorContentType: ProblemJson);
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
                Form: null,
                Headers: [],
                QueryParameters: [],
                TraceId: new TraceId("traceId"),
                RouteQualifiers: []
            );
            _requestInfo = new(frontEndRequest, RequestMethod.POST, No.ServiceProvider)
            {
                ParsedBody = JsonNode.Parse(jsonBody)!,
            };
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
    public class Given_Pipeline_Context_With_Frontend_Detected_Duplicate_Property
        : DuplicatePropertiesMiddlewareTest
    {
        private RequestInfo _requestInfo = No.RequestInfo();

        [SetUp]
        public async Task Setup()
        {
            var frontEndRequest = new FrontendRequest(
                Path: "ed-fi/schools",
                Body: null,
                Form: null,
                Headers: [],
                QueryParameters: [],
                TraceId: new TraceId("traceId"),
                RouteQualifiers: [],
                DuplicatePropertyPath: "$.schoolId"
            );
            _requestInfo = new(frontEndRequest, RequestMethod.POST, No.ServiceProvider)
            {
                ParsedBody = JsonNode.Parse("""{"schoolId":255901001}""")!,
            };
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
                Form: null,
                Headers: [],
                QueryParameters: [],
                TraceId: new TraceId("traceId"),
                RouteQualifiers: []
            );
            _requestInfo = new(frontEndRequest, RequestMethod.POST, No.ServiceProvider)
            {
                ParsedBody = JsonNode.Parse(jsonBody)!,
            };
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
                Form: null,
                Headers: [],
                QueryParameters: [],
                TraceId: new TraceId("traceId"),
                RouteQualifiers: []
            );
            _requestInfo = new(frontEndRequest, RequestMethod.POST, No.ServiceProvider)
            {
                ParsedBody = JsonNode.Parse(jsonBody)!,
            };
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
                Form: null,
                Headers: [],
                QueryParameters: [],
                TraceId: new TraceId("traceId"),
                RouteQualifiers: []
            );
            _requestInfo = new(frontEndRequest, RequestMethod.POST, No.ServiceProvider)
            {
                ParsedBody = JsonNode.Parse(jsonBody)!,
            };
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
                Form: null,
                Headers: [],
                QueryParameters: [],
                TraceId: new TraceId("traceId"),
                RouteQualifiers: []
            );
            _requestInfo = new(frontEndRequest, RequestMethod.POST, No.ServiceProvider)
            {
                ParsedBody = JsonNode.Parse(jsonBody)!,
            };
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
                Form: null,
                Headers: [],
                QueryParameters: [],
                TraceId: new TraceId("traceId"),
                RouteQualifiers: []
            );
            _requestInfo = new(frontEndRequest, RequestMethod.PUT, No.ServiceProvider)
            {
                ParsedBody = JsonNode.Parse(jsonBody)!,
            };
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
                Form: null,
                Headers: [],
                QueryParameters: [],
                TraceId: new TraceId("traceId"),
                RouteQualifiers: []
            );
            _requestInfo = new(frontEndRequest, RequestMethod.PUT, No.ServiceProvider)
            {
                ParsedBody = JsonNode.Parse(jsonBody)!,
            };
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

    /// <summary>
    /// A duplicate found either way - named by the frontend, which is the production path, or found
    /// by scanning a raw body passed through the Core request contract - is answered in the
    /// configured media type: application/json by default, application/problem+json when the
    /// identity JSON-body pipeline asks for it. The status and body do not depend on the option.
    /// </summary>
    [TestFixture("frontend-path")]
    [TestFixture("raw-body-scan")]
    [Parallelizable]
    public class Given_A_Duplicate_Property_With_And_Without_The_Problem_Json_Option(string source)
        : DuplicatePropertiesMiddlewareTest
    {
        private RequestInfo _problemJsonRequestInfo = No.RequestInfo();
        private RequestInfo _defaultRequestInfo = No.RequestInfo();
        private bool _nextWasCalled;

        private FrontendRequest Request() =>
            source switch
            {
                "frontend-path" => RequestWith(body: null, duplicatePropertyPath: "$.FirstName"),
                "raw-body-scan" => RequestWith("""{"FirstName":"Jane","FirstName":"Jane"}"""),
                _ => throw new ArgumentOutOfRangeException(nameof(source), source, null),
            };

        private static FrontendRequest RequestWith(string? body, string? duplicatePropertyPath = null) =>
            new(
                Path: "identities",
                Body: body,
                Form: null,
                Headers: [],
                QueryParameters: [],
                TraceId: new TraceId("traceId"),
                RouteQualifiers: [],
                DuplicatePropertyPath: duplicatePropertyPath
            );

        private Task RecordingNext()
        {
            _nextWasCalled = true;
            return Task.CompletedTask;
        }

        [SetUp]
        public async Task Setup()
        {
            _problemJsonRequestInfo = new(Request(), RequestMethod.POST, No.ServiceProvider);
            await ProblemJsonMiddleware().Execute(_problemJsonRequestInfo, RecordingNext);

            _defaultRequestInfo = new(Request(), RequestMethod.POST, No.ServiceProvider);
            await Middleware().Execute(_defaultRequestInfo, RecordingNext);
        }

        [Test]
        public void It_returns_status_400_either_way()
        {
            _problemJsonRequestInfo.FrontendResponse.StatusCode.Should().Be(400);
            _defaultRequestInfo.FrontendResponse.StatusCode.Should().Be(400);
        }

        [Test]
        public void It_returns_application_json_by_default()
        {
            _defaultRequestInfo.FrontendResponse.ContentType.Should().Be("application/json");
        }

        [Test]
        public void It_returns_application_problem_json_when_configured()
        {
            _problemJsonRequestInfo.FrontendResponse.ContentType.Should().Be(ProblemJson);
        }

        [Test]
        public void It_names_the_duplicate_property()
        {
            _defaultRequestInfo.FrontendResponse.Body!["validationErrors"]!
                .AsObject()
                .Select(error => error.Key)
                .Should()
                .Equal("$.FirstName");
        }

        [Test]
        public void It_returns_the_same_body_either_way()
        {
            _problemJsonRequestInfo
                .FrontendResponse.Body!.ToJsonString()
                .Should()
                .Be(_defaultRequestInfo.FrontendResponse.Body!.ToJsonString());
        }

        [Test]
        public void It_does_not_continue_the_pipeline()
        {
            _nextWasCalled.Should().BeFalse();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_The_Problem_Json_Option_And_No_Duplicate_Property : DuplicatePropertiesMiddlewareTest
    {
        private RequestInfo _requestInfo = No.RequestInfo();
        private bool _nextWasCalled;

        [SetUp]
        public async Task Setup()
        {
            var frontEndRequest = new FrontendRequest(
                Path: "identities",
                Body: """{"FirstName":"Jane","LastSurname":"Rivera"}""",
                Form: null,
                Headers: [],
                QueryParameters: [],
                TraceId: new TraceId("traceId"),
                RouteQualifiers: []
            );
            _requestInfo = new(frontEndRequest, RequestMethod.POST, No.ServiceProvider);
            await ProblemJsonMiddleware()
                .Execute(
                    _requestInfo,
                    () =>
                    {
                        _nextWasCalled = true;
                        return Task.CompletedTask;
                    }
                );
        }

        [Test]
        public void It_continues_the_pipeline()
        {
            _nextWasCalled.Should().BeTrue();
        }

        [Test]
        public void It_does_not_set_an_error_response()
        {
            _requestInfo.FrontendResponse.Should().Be(No.FrontendResponse);
        }
    }
}
