// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.ApiSchema.Helpers;
using EdFi.DataManagementService.Core.External.Frontend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Middleware;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using FluentAssertions;
using Json.Schema;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Middleware
{
    public class CoerceRequestValuesMiddlewareTests
    {
        public static Func<Task> Next()
        {
            return () => Task.CompletedTask;
        }

        internal static ApiSchemaDocuments SchemaDocuments()
        {
            var builder = new JsonSchemaBuilder();
            builder.Title("Ed-Fi.School");
            builder.Description("This entity represents an educational organization");
            builder.Schema("https://json-schema.org/draft/2020-12/schema");
            builder.AdditionalProperties(false);
            builder
                .Properties(
                    ("schoolId", new JsonSchemaBuilder().Type(SchemaValueType.Integer)),
                    ("yearsOld", new JsonSchemaBuilder().Type(SchemaValueType.Number)),
                    (
                        "gradeLevels",
                        new JsonSchemaBuilder()
                            .Type(SchemaValueType.Array)
                            .Properties(
                                (
                                    "gradeLevelDescriptor",
                                    new JsonSchemaBuilder().Type(SchemaValueType.String)
                                ),
                                ("isSecondary", new JsonSchemaBuilder().Type(SchemaValueType.Boolean))
                            )
                            .Required("gradeLevelDescriptor")
                            .AdditionalProperties(false)
                    ),
                    ("nameOfInstitution", new JsonSchemaBuilder().Type(SchemaValueType.String)),
                    (
                        "webSite",
                        new JsonSchemaBuilder().Type(SchemaValueType.String).MinLength(5).MaxLength(10)
                    )
                )
                .Required("schoolId", "gradeLevels", "nameOfInstitution");

            return new ApiSchemaBuilder()
                .WithStartProject("Ed-Fi", "5.0.0")
                .WithStartResource("School")
                .WithJsonSchemaForInsert(builder.Build()!)
                .WithDecimalPropertyValidationInfos(
                    new[] { new DecimalValidationInfo(new JsonPath("$.yearsOld"), 5, 3) }
                )
                .WithBooleanJsonPaths(new[] { "$.gradeLevels[*].isSecondary" })
                .WithNumericJsonPaths(new[] { "$.schoolId" })
                .WithEndResource()
                .WithEndProject()
                .ToApiSchemaDocuments();
        }

        internal static IPipelineStep Middleware()
        {
            return new CoerceRequestValuesMiddleware(NullLogger.Instance);
        }

        internal RequestInfo Context(FrontendRequest frontendRequest, RequestMethod method)
        {
            RequestInfo _requestInfo = new(frontendRequest, method, No.ServiceProvider)
            {
                ApiSchemaDocuments = SchemaDocuments(),
                PathComponents = new(
                    ProjectEndpointName: new("ed-fi"),
                    EndpointName: new("schools"),
                    DocumentUuid: No.DocumentUuid
                ),
            };
            _requestInfo.ProjectSchema = _requestInfo.ApiSchemaDocuments.FindProjectSchemaForProjectNamespace(
                new("ed-fi")
            )!;
            _requestInfo.ResourceSchema = new ResourceSchema(
                _requestInfo.ProjectSchema.FindResourceSchemaNodeByEndpointName(new("schools"))
                    ?? new JsonObject()
            );

            if (_requestInfo.FrontendRequest.Body != null)
            {
                var body = JsonNode.Parse(_requestInfo.FrontendRequest.Body);
                if (body != null)
                {
                    _requestInfo.ParsedBody = body;
                }
            }

            return _requestInfo;
        }

        [TestFixture]
        [Parallelizable]
        public class Given_A_Request_With_Boolean_And_Numeric_Property_As_String
            : CoerceRequestValuesMiddlewareTests
        {
            private RequestInfo _requestInfo = No.RequestInfo();

            [SetUp]
            public async Task Setup()
            {
                string jsonData =
                    """{"schoolId": "1","yearsOld": "1","gradeLevels":[{"gradeLevelDescriptor": "grade1", "isSecondary": "false"}],"nameOfInstitution":"school12"}""";

                var frontEndRequest = new FrontendRequest(
                    Path: "ed-fi/schools",
                    Body: jsonData,
                    Form: null,
                    Headers: [],
                    QueryParameters: [],
                    TraceId: new TraceId("traceId"),
                    RouteQualifiers: []
                );
                _requestInfo = Context(frontEndRequest, RequestMethod.POST);
                await Middleware().Execute(_requestInfo, Next());
            }

            [Test]
            public void It_coerces_numbers()
            {
                var paths = _requestInfo.ResourceSchema.NumericJsonPaths.Select(s => s.Value).ToList();
                paths.Count.Should().BeGreaterThan(0);
                foreach (string path in paths)
                {
                    _requestInfo
                        .ParsedBody.SelectNodeFromPath(path, NullLogger.Instance)!
                        .AsValue()
                        .GetValueKind()
                        .Should()
                        .Be(JsonValueKind.Number);
                }
            }

            [Test]
            public void It_coerces_decimals()
            {
                var paths = _requestInfo
                    .ResourceSchema.DecimalPropertyValidationInfos.Select(s => s.Path.Value)
                    .ToList();
                paths.Count.Should().BeGreaterThan(0);
                foreach (string path in paths)
                {
                    _requestInfo
                        .ParsedBody.SelectNodeFromPath(path, NullLogger.Instance)!
                        .AsValue()
                        .GetValueKind()
                        .Should()
                        .Be(JsonValueKind.Number);
                }
            }

            [Test]
            public void It_coerces_booleans()
            {
                var paths = _requestInfo.ResourceSchema.BooleanJsonPaths.Select(s => s.Value).ToList();
                paths.Count.Should().BeGreaterThan(0);
                foreach (string path in paths)
                {
                    _requestInfo
                        .ParsedBody.SelectNodeFromPath(path, NullLogger.Instance)!
                        .AsValue()
                        .GetValueKind()
                        .Should()
                        .BeOneOf(JsonValueKind.True, JsonValueKind.False);
                }
            }
        }

        [TestFixture]
        [Parallelizable]
        public class Given_A_Request_With_Numeric_Boolean_Aliases : CoerceRequestValuesMiddlewareTests
        {
            private RequestInfo _requestInfo = No.RequestInfo();

            [SetUp]
            public async Task Setup()
            {
                string jsonData = """
                    {
                        "schoolId": "1",
                        "yearsOld": "1",
                        "gradeLevels": [
                            {
                                "gradeLevelDescriptor": "grade1",
                                "isSecondary": 0
                            },
                            {
                                "gradeLevelDescriptor": "grade2",
                                "isSecondary": 1
                            },
                            {
                                "gradeLevelDescriptor": "grade3",
                                "isSecondary": "0"
                            },
                            {
                                "gradeLevelDescriptor": "grade4",
                                "isSecondary": "1"
                            },
                            {
                                "gradeLevelDescriptor": "grade5",
                                "isSecondary": " 0 "
                            },
                            {
                                "gradeLevelDescriptor": "grade6",
                                "isSecondary": " 1 "
                            }
                        ],
                        "nameOfInstitution": "school12",
                        "webSite": "1"
                    }
                    """;

                var frontEndRequest = new FrontendRequest(
                    Path: "ed-fi/schools",
                    Body: jsonData,
                    Form: null,
                    Headers: [],
                    QueryParameters: [],
                    TraceId: new TraceId("traceId"),
                    RouteQualifiers: []
                );
                _requestInfo = Context(frontEndRequest, RequestMethod.POST);
                await Middleware().Execute(_requestInfo, Next());
            }

            [Test]
            public void It_coerces_numeric_boolean_aliases()
            {
                JsonArray gradeLevels = _requestInfo.ParsedBody["gradeLevels"]!.AsArray();

                var booleanValues = gradeLevels
                    .Select(gradeLevel => gradeLevel!["isSecondary"]!.AsValue().GetValue<bool>())
                    .ToList();

                booleanValues.Should().Equal(false, true, false, true, false, true);
            }

            [Test]
            public void It_only_coerces_schema_boolean_paths()
            {
                _requestInfo.ParsedBody["webSite"]!
                    .AsValue()
                    .GetValueKind()
                    .Should()
                    .Be(JsonValueKind.String);
            }
        }

        [TestFixture]
        [Parallelizable]
        public class Given_A_Request_With_Invalid_Numeric_Boolean_Aliases : CoerceRequestValuesMiddlewareTests
        {
            private RequestInfo _requestInfo = No.RequestInfo();

            [SetUp]
            public async Task Setup()
            {
                string jsonData = """
                    {
                        "schoolId": "1",
                        "yearsOld": "1",
                        "gradeLevels": [
                            {
                                "gradeLevelDescriptor": "grade1",
                                "isSecondary": 2
                            },
                            {
                                "gradeLevelDescriptor": "grade2",
                                "isSecondary": "2"
                            }
                        ],
                        "nameOfInstitution": "school12"
                    }
                    """;

                var frontEndRequest = new FrontendRequest(
                    Path: "ed-fi/schools",
                    Body: jsonData,
                    Form: null,
                    Headers: [],
                    QueryParameters: [],
                    TraceId: new TraceId("traceId"),
                    RouteQualifiers: []
                );
                _requestInfo = Context(frontEndRequest, RequestMethod.POST);
                await Middleware().Execute(_requestInfo, Next());
            }

            [Test]
            public void It_leaves_invalid_aliases_for_document_validation()
            {
                JsonArray gradeLevels = _requestInfo.ParsedBody["gradeLevels"]!.AsArray();

                gradeLevels[0]!["isSecondary"]!.AsValue().GetValueKind().Should().Be(JsonValueKind.Number);
                gradeLevels[1]!["isSecondary"]!.AsValue().GetValueKind().Should().Be(JsonValueKind.String);
            }
        }
    }
}
