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
using EdFi.DataManagementService.Core.Utilities;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Extensions.Logging;
using static EdFi.DataManagementService.Core.Tests.Unit.TestHelper;

namespace EdFi.DataManagementService.Core.Tests.Unit.Middleware;

[TestFixture]
[Parallelizable]
public class DuplicatePropertiesMiddlewareTest
{
    private const string ProblemJson = "application/problem+json";

    private static RequestInfo RawRequest(string body) =>
        new(
            new FrontendRequest(
                Path: "ed-fi/schools",
                Body: body,
                Form: null,
                Headers: [],
                QueryParameters: [],
                TraceId: new TraceId("traceId"),
                RouteQualifiers: []
            ),
            RequestMethod.POST,
            No.ServiceProvider
        );

    private static void AssertDuplicateProblem(RequestInfo requestInfo, string expectedPath)
    {
        requestInfo.FrontendResponse.StatusCode.Should().Be(400);
        JsonNode body = requestInfo.FrontendResponse.Body!;
        body["type"]!.GetValue<string>().Should().Be("urn:ed-fi:api:bad-request:data-validation-failed");
        body["title"]!.GetValue<string>().Should().Be("Data Validation Failed");
        body["status"]!.GetValue<int>().Should().Be(400);
        body["detail"]!
            .GetValue<string>()
            .Should()
            .Be("Data validation failed. See 'validationErrors' for details.");
        body["correlationId"]!.GetValue<string>().Should().Be("traceId");
        body["errors"]!.AsArray().Should().BeEmpty();
        JsonObject errors = body["validationErrors"]!.AsObject();
        errors.Select(item => item.Key).Should().Equal(expectedPath);
        errors[expectedPath]!
            .AsArray()
            .Select(item => item!.GetValue<string>())
            .Should()
            .Equal("An item with the same key has already been added.");
    }

    [TestFixtureSource(nameof(Cases))]
    public class Given_Raw_Duplicate_Property_Paths(string json, string expectedPath)
    {
        private RequestInfo _requestInfo = No.RequestInfo();
        private int _nextCalls;

        private static IEnumerable<object[]> Cases()
        {
            yield return
            [
                """{"studentUniqueId":"1","addresses":[{"city":"a","streetNumberName":"b"},{"city":"x","city":"y"}]}""",
                "$.addresses[1].city",
            ];
            yield return
            [
                """{"studentUniqueId":"1","addresses":[{"city":"a"}],"electronicMails":[{"x":"1","x":"2"}]}""",
                "$.electronicMails[0].x",
            ];
            yield return
            [
                """[{"LastSurname":"a"},{"BirthLocation":{"City":"x","City":"y"}}]""",
                "$[1].BirthLocation.City",
            ];
            (string Sequence, string Suffix)[] matrix =
            [
                ("""{"x":1,"x":2}""", "[0].x"),
                ("""{"before":1,"x":1,"x":2}""", "[0].x"),
                ("""{"a":1,"b":2},{"before":0,"x":1,"x":2}""", "[1].x"),
                ("""{},{"before":1,"groups":[{"a":1},{"before":0,"x":1,"x":2}]}""", "[1].groups[1].x"),
            ];
            foreach (var (sequence, suffix) in matrix)
            {
                yield return ["{\"items\":[" + sequence + "]}", "$.items" + suffix];
                yield return ["[" + sequence + "]", "$" + suffix];
            }
        }

        [SetUp]
        public async Task Setup()
        {
            _nextCalls = 0;
            _requestInfo = RawRequest(json);
            await Middleware()
                .Execute(
                    _requestInfo,
                    () =>
                    {
                        _nextCalls++;
                        return Task.CompletedTask;
                    }
                );
        }

        [Test]
        public void It_reports_the_exact_problem() => AssertDuplicateProblem(_requestInfo, expectedPath);

        [Test]
        public void It_rejects_before_the_next_step() => _nextCalls.Should().Be(0);
    }

    [TestFixture("""{"other":1,"other":2}""", "$.supplied")]
    [TestFixture("""{"other":1,"other":2}""", "")]
    [TestFixture("not JSON", "$.supplied")]
    [TestFixture("not JSON", "")]
    public class Given_A_Raw_Body_With_Supplied_Metadata(string body, string suppliedPath)
    {
        private RequestInfo _requestInfo = No.RequestInfo();
        private int _nextCalls;

        [SetUp]
        public async Task Setup()
        {
            _requestInfo = RawRequest(body);
            _requestInfo.FrontendRequest = _requestInfo.FrontendRequest with
            {
                DuplicatePropertyPath = suppliedPath,
            };
            _nextCalls = 0;
            await Middleware()
                .Execute(
                    _requestInfo,
                    () =>
                    {
                        _nextCalls++;
                        return Task.CompletedTask;
                    }
                );
        }

        [Test]
        public void It_prefers_the_supplied_path() => AssertDuplicateProblem(_requestInfo, suppliedPath);

        [Test]
        public void It_rejects_before_the_next_step() => _nextCalls.Should().Be(0);
    }

    [TestFixture("""[{"x":1},{"x":2}]""")]
    [TestFixture("")]
    public class Given_A_Raw_Body_Without_Duplicate_Metadata_Or_Properties(string body)
    {
        private int _nextCalls;

        [SetUp]
        public async Task Setup()
        {
            _nextCalls = 0;
            await Middleware()
                .Execute(
                    RawRequest(body),
                    () =>
                    {
                        _nextCalls++;
                        return Task.CompletedTask;
                    }
                );
        }

        [Test]
        public void It_continues_once_without_duplicates() => _nextCalls.Should().Be(1);
    }

    [TestFixture("control-padded")]
    [TestFixture("short-controls")]
    [TestFixture("astral-boundary")]
    public class Given_A_Duplicate_Path_Is_Logged(string scenario)
    {
        private RequestInfo _requestInfo = No.RequestInfo();
        private LogEvent _duplicateEvent = null!;
        private string _originalPath = string.Empty;
        private string _expectedLog = string.Empty;

        private sealed class CapturingSink : ILogEventSink
        {
            public List<LogEvent> Events { get; } = [];

            public void Emit(LogEvent logEvent) => Events.Add(logEvent);
        }

        [SetUp]
        public async Task Setup()
        {
            (_originalPath, _expectedLog) = scenario switch
            {
                "control-padded" => (
                    "$." + new string('\n', 1100) + new string('a', 1023) + "😀tail",
                    "$." + new string('a', 1022) + LoggingSanitizer.TruncationSuffix
                ),
                "short-controls" => ("$.x\r\nforged\t\u202E", "$.xforged"),
                _ => (
                    "$." + new string('a', 1021) + "😀tail",
                    "$." + new string('a', 1021) + LoggingSanitizer.TruncationSuffix
                ),
            };
            var sink = new CapturingSink();
            using var serilogLogger = new LoggerConfiguration()
                .MinimumLevel.Debug()
                .WriteTo.Sink(sink)
                .CreateLogger();
            using var factory = new SerilogLoggerFactory(serilogLogger);
            _requestInfo = RawRequest("{}");
            _requestInfo.FrontendRequest = _requestInfo.FrontendRequest with
            {
                DuplicatePropertyPath = _originalPath,
            };
            await new DuplicatePropertiesMiddleware(
                factory.CreateLogger<DuplicatePropertiesMiddleware>()
            ).Execute(_requestInfo, () => throw new AssertionException("Duplicate continued"));

            _duplicateEvent = sink.Events.Single(item => item.Properties.ContainsKey("DuplicatePath"));
        }

        [Test]
        public void It_sanitizes_before_truncating() =>
            ((ScalarValue)_duplicateEvent.Properties["DuplicatePath"]).Value.Should().Be(_expectedLog);

        [Test]
        public void It_removes_controls_from_the_rendered_message() =>
            _duplicateEvent
                .RenderMessage()
                .Should()
                .NotContain("\n")
                .And.NotContain("\r")
                .And.NotContain("\t")
                .And.NotContain("\u202E");

        [Test]
        public void It_logs_the_trace_id_value() =>
            ((ScalarValue)_duplicateEvent.Properties["TraceId"]).Value.Should().Be("traceId");

        [Test]
        public void It_keeps_the_original_path_in_the_response() =>
            AssertDuplicateProblem(_requestInfo, _originalPath);
    }

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
