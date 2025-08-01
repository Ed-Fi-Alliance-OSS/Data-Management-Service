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
using EdFi.DataManagementService.Core.Validation;
using FluentAssertions;
using Json.Schema;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Middleware;

public class ValidateDocumentMiddlewareTests
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
                (
                    "gradeLevels",
                    new JsonSchemaBuilder()
                        .Type(SchemaValueType.Object)
                        .Properties(
                            ("gradeLevelDescriptor", new JsonSchemaBuilder().Type(SchemaValueType.String)),
                            ("optionalNested", new JsonSchemaBuilder().Type(SchemaValueType.String))
                        )
                        .Required("gradeLevelDescriptor")
                        .AdditionalProperties(false)
                ),
                (
                    "nameOfInstitution",
                    new JsonSchemaBuilder().Type(SchemaValueType.String).Pattern("^(?!\\s*$).+")
                ),
                (
                    "identityProperty",
                    new JsonSchemaBuilder().Type(SchemaValueType.String).Pattern("^(?!\\s)(.*\\S)$")
                ),
                ("optionalProperty", new JsonSchemaBuilder().Type(SchemaValueType.String)),
                ("optionalNumber", new JsonSchemaBuilder().Type(SchemaValueType.Number)),
                ("optionalBoolean", new JsonSchemaBuilder().Type(SchemaValueType.Boolean)),
                ("webSite", new JsonSchemaBuilder().Type(SchemaValueType.String).MinLength(5).MaxLength(10)),
                (
                    "educationOrganizationCategories",
                    new JsonSchemaBuilder()
                        .Type(SchemaValueType.Array)
                        .Items(
                            new JsonSchemaBuilder()
                                .Type(SchemaValueType.Object)
                                .Properties(
                                    (
                                        "educationOrganizationCategoryDescriptor",
                                        new JsonSchemaBuilder().Type(SchemaValueType.String)
                                    )
                                )
                                .Required("educationOrganizationCategoryDescriptor")
                                .AdditionalProperties(false)
                        )
                        .MinItems(1)
                )
            )
            .Required("schoolId", "gradeLevels", "nameOfInstitution");

        return new ApiSchemaBuilder()
            .WithStartProject("Ed-Fi", "5.0.0")
            .WithStartResource("School")
            .WithJsonSchemaForInsert(builder.Build()!)
            .WithEndResource()
            .WithEndProject()
            .ToApiSchemaDocuments();
    }

    internal static IPipelineStep Middleware()
    {
        var documentValidator = new DocumentValidator();
        return new ValidateDocumentMiddleware(NullLogger.Instance, documentValidator);
    }

    internal RequestInfo Context(FrontendRequest frontendRequest, RequestMethod method)
    {
        RequestInfo _requestInfo = new(frontendRequest, method)
        {
            ApiSchemaDocuments = SchemaDocuments(),
            PathComponents = new(
                ProjectNamespace: new("ed-fi"),
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
    public class Given_A_Request_With_Overposted_Property : ValidateDocumentMiddlewareTests
    {
        private RequestInfo _requestInfo = No.RequestInfo();

        [SetUp]
        public async Task Setup()
        {
            string jsonData =
                """{"schoolId": 989, "gradeLevels":{"gradeLevelDescriptor": "grade1"},"nameOfInstitution":"school12", "propertyOverPost": "overpostedvalue"}""";

            var frontEndRequest = new FrontendRequest(
                "ed-fi/schools",
                Body: jsonData,
                Headers: [],
                QueryParameters: [],
                TraceId: new TraceId("traceId")
            );
            _requestInfo = Context(frontEndRequest, RequestMethod.POST);
            await Middleware().Execute(_requestInfo, Next());
        }

        [Test]
        public void It_should_not_have_response()
        {
            _requestInfo.FrontendResponse.Should().Be(No.FrontendResponse);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Request_With_Overposted_Nested_Property : ValidateDocumentMiddlewareTests
    {
        private RequestInfo _requestInfo = No.RequestInfo();

        [SetUp]
        public async Task Setup()
        {
            string jsonData =
                """{"schoolId": 989, "gradeLevels":{"gradeLevelDescriptor": "grade1", "gradeLevelOverPost": "overPostedValue"},"nameOfInstitution":"school12", "propertyOverPost": "overPostedValue"}""";

            var frontEndRequest = new FrontendRequest(
                "ed-fi/schools",
                Body: jsonData,
                Headers: [],
                QueryParameters: [],
                TraceId: new TraceId("traceId")
            );
            _requestInfo = Context(frontEndRequest, RequestMethod.POST);
            await Middleware().Execute(_requestInfo, Next());
        }

        [Test]
        public void It_should_not_have_response()
        {
            _requestInfo.FrontendResponse.Should().Be(No.FrontendResponse);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Request_With_Overposted_Object_Property : ValidateDocumentMiddlewareTests
    {
        private RequestInfo _requestInfo = No.RequestInfo();

        [SetUp]
        public async Task Setup()
        {
            string jsonData =
                """{"schoolId": 989, "gradeLevels":{"gradeLevelDescriptor": "grade1", "gradeLevelOverPost": "overPostedValue" },"nameOfInstitution":"school12", "objectOverpost": { "x": "overPostedValue"}, "educationOrganizationCategories":[{"educationOrganizationCategoryDescriptor": "School", "newOverposted":{"objectOverposted":"y"}}]}""";

            var frontEndRequest = new FrontendRequest(
                "ed-fi/schools",
                Body: jsonData,
                Headers: [],
                QueryParameters: [],
                TraceId: new TraceId("traceId")
            );
            _requestInfo = Context(frontEndRequest, RequestMethod.POST);
            await Middleware().Execute(_requestInfo, Next());
        }

        [Test]
        public void It_should_not_have_response()
        {
            _requestInfo.FrontendResponse.Should().Be(No.FrontendResponse);
        }

        [Test]
        public void It_should_not_be_equal_than_parsed_body()
        {
            _requestInfo.FrontendRequest.Body.Should().NotBe(_requestInfo.ParsedBody.ToJsonString());
        }

        [Test]
        public void It_should_not_contain_objectOverpost()
        {
            _requestInfo
                .ParsedBody.ToJsonString()
                .Should()
                .NotContain("\"objectOverpost\": { \"x\": \"overPostedValue\"}");
        }

        [Test]
        public void It_should_contain_objectOverpost()
        {
            _requestInfo
                .FrontendRequest.Body.Should()
                .Contain(""""objectOverpost": { "x": "overPostedValue"}"""");
        }

        [Test]
        public void It_should_be_correct_parsed_body()
        {
            _requestInfo
                .ParsedBody.ToJsonString()
                .Should()
                .Be(
                    """{"schoolId":989,"gradeLevels":{"gradeLevelDescriptor":"grade1"},"nameOfInstitution":"school12","educationOrganizationCategories":[{"educationOrganizationCategoryDescriptor":"School"}]}"""
                );
        }

        [Test]
        public void It_should_not_contain_newOverposted_in_educationOrganizationCategories()
        {
            _requestInfo
                .ParsedBody.ToJsonString()
                .Should()
                .Contain(
                    """educationOrganizationCategories":[{"educationOrganizationCategoryDescriptor":"School"}]"""
                );
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Request_With_Null_Property : ValidateDocumentMiddlewareTests
    {
        private RequestInfo _requestInfo = No.RequestInfo();

        [SetUp]
        public async Task Setup()
        {
            string jsonData =
                """{"schoolId": 989, "gradeLevels":{"gradeLevelDescriptor": "grade1"},"nameOfInstitution":"school12", "optionalProperty": null, "optionalNumber": null, "optionalBoolean": null}""";

            var frontEndRequest = new FrontendRequest(
                "ed-fi/schools",
                Body: jsonData,
                Headers: [],
                QueryParameters: [],
                TraceId: new TraceId("traceId")
            );
            _requestInfo = Context(frontEndRequest, RequestMethod.POST);
            await Middleware().Execute(_requestInfo, Next());
        }

        [Test]
        public void It_should_not_have_response()
        {
            _requestInfo.FrontendResponse.Should().Be(No.FrontendResponse);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Request_With_Null_Nested_Property : ValidateDocumentMiddlewareTests
    {
        private RequestInfo _requestInfo = No.RequestInfo();

        [SetUp]
        public async Task Setup()
        {
            string jsonData =
                """{"schoolId": 989, "gradeLevels":{"gradeLevelDescriptor": "grade1", "optionalNested": null},"nameOfInstitution":"school12", "propertyOverPost": "overPostedValue"}""";

            var frontEndRequest = new FrontendRequest(
                "ed-fi/schools",
                Body: jsonData,
                Headers: [],
                QueryParameters: [],
                TraceId: new TraceId("traceId")
            );
            _requestInfo = Context(frontEndRequest, RequestMethod.POST);
            await Middleware().Execute(_requestInfo, Next());
        }

        [Test]
        public void It_should_not_have_response()
        {
            _requestInfo.FrontendResponse.Should().Be(No.FrontendResponse);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Request_With_No_Required_Property : ValidateDocumentMiddlewareTests
    {
        private RequestInfo _requestInfo = No.RequestInfo();

        [SetUp]
        public async Task Setup()
        {
            string jsonData =
                """{"gradeLevels":{"gradeLevelDescriptor": "grade1"},"nameOfInstitution":"school12"}""";

            var frontEndRequest = new FrontendRequest(
                "ed-fi/schools",
                Body: jsonData,
                Headers: [],
                QueryParameters: [],
                TraceId: new TraceId("traceId")
            );
            _requestInfo = Context(frontEndRequest, RequestMethod.POST);
            await Middleware().Execute(_requestInfo, Next());
        }

        [Test]
        public void It_has_a_response()
        {
            _requestInfo.FrontendResponse.Should().NotBe(No.FrontendResponse);
        }

        [Test]
        public void It_returns_status_400()
        {
            _requestInfo.FrontendResponse.StatusCode.Should().Be(400);
        }

        [Test]
        public void It_returns_message_body_with_required_validation_error()
        {
            _requestInfo.FrontendResponse.Body?.ToJsonString().Should().ContainAll("is required", "schoolId");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Request_With_Wrong_Type_Property_Value : ValidateDocumentMiddlewareTests
    {
        private RequestInfo _requestInfo = No.RequestInfo();

        [SetUp]
        public async Task Setup()
        {
            string jsonData =
                """{"schoolId": "wrong value","gradeLevels":{"gradeLevelDescriptor": "grade1"},"nameOfInstitution":"school12"}""";

            var frontEndRequest = new FrontendRequest(
                "ed-fi/schools",
                Body: jsonData,
                Headers: [],
                QueryParameters: [],
                TraceId: new TraceId("traceId")
            );
            _requestInfo = Context(frontEndRequest, RequestMethod.POST);
            await Middleware().Execute(_requestInfo, Next());
        }

        [Test]
        public void It_has_a_response()
        {
            _requestInfo.FrontendResponse.Should().NotBe(No.FrontendResponse);
        }

        [Test]
        public void It_returns_status_400()
        {
            _requestInfo.FrontendResponse.StatusCode.Should().Be(400);
        }

        [Test]
        public void It_returns_message_body_with_wrong_data_type_validation_error()
        {
            _requestInfo
                .FrontendResponse.Body?.ToJsonString()
                .Should()
                .ContainAll("schoolId Value is", "integer");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Update_Request_With_No_Id_Property : ValidateDocumentMiddlewareTests
    {
        private RequestInfo _requestInfo = No.RequestInfo();

        [SetUp]
        public async Task Setup()
        {
            string jsonData =
                """{"schoolId": 7687,"gradeLevels":{"gradeLevelDescriptor": "grade1"},"nameOfInstitution":"school12"}""";

            var frontEndRequest = new FrontendRequest(
                "ed-fi/schools",
                Body: jsonData,
                Headers: [],
                QueryParameters: [],
                TraceId: new TraceId("traceId")
            );
            _requestInfo = Context(frontEndRequest, RequestMethod.PUT);
            await Middleware().Execute(_requestInfo, Next());
        }

        [Test]
        public void It_has_a_response()
        {
            _requestInfo.FrontendResponse.Should().NotBe(No.FrontendResponse);
        }

        [Test]
        public void It_returns_status_400()
        {
            _requestInfo.FrontendResponse.StatusCode.Should().Be(400);
        }

        [Test]
        public void It_returns_message_body_with_required_validation_error()
        {
            _requestInfo.FrontendResponse.Body?.ToJsonString().Should().ContainAll("is required", "id");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_An_Insert_Request_With_Required_String_Property_Value_Contains_Only_Whitespaces
        : ValidateDocumentMiddlewareTests
    {
        private RequestInfo _requestInfo = No.RequestInfo();

        [SetUp]
        public async Task Setup()
        {
            string jsonData =
                """{"schoolId": 7687,"gradeLevels":{"gradeLevelDescriptor": "grade1"},"nameOfInstitution":"         "}""";

            var frontEndRequest = new FrontendRequest(
                "ed-fi/schools",
                Body: jsonData,
                Headers: [],
                QueryParameters: [],
                TraceId: new TraceId("traceId")
            );
            _requestInfo = Context(frontEndRequest, RequestMethod.POST);
            await Middleware().Execute(_requestInfo, Next());
        }

        [Test]
        public void It_has_a_response()
        {
            _requestInfo.FrontendResponse.Should().NotBe(No.FrontendResponse);
        }

        [Test]
        public void It_returns_status_400()
        {
            _requestInfo.FrontendResponse.StatusCode.Should().Be(400);
        }

        [Test]
        public void It_returns_message_body_with_required_validation_error()
        {
            _requestInfo
                .FrontendResponse.Body?.ToJsonString()
                .Should()
                .ContainAll("nameOfInstitution", "cannot contain leading or trailing spaces");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_An_Insert_Request_With_Required_String_Property_Value_Is_Null
        : ValidateDocumentMiddlewareTests
    {
        private RequestInfo _requestInfo = No.RequestInfo();

        [SetUp]
        public async Task Setup()
        {
            string jsonData =
                """{"schoolId": 7687,"gradeLevels":{"gradeLevelDescriptor": "grade1"},"nameOfInstitution": null}""";

            var frontEndRequest = new FrontendRequest(
                "ed-fi/schools",
                Body: jsonData,
                Headers: [],
                QueryParameters: [],
                TraceId: new TraceId("traceId")
            );
            _requestInfo = Context(frontEndRequest, RequestMethod.POST);
            await Middleware().Execute(_requestInfo, Next());
        }

        [Test]
        public void It_has_a_response()
        {
            _requestInfo.FrontendResponse.Should().NotBe(No.FrontendResponse);
        }

        [Test]
        public void It_returns_status_400()
        {
            _requestInfo.FrontendResponse.StatusCode.Should().Be(400);
        }

        [Test]
        public void It_returns_message_body_with_required_validation_error()
        {
            _requestInfo
                .FrontendResponse.Body?.ToJsonString()
                .Should()
                .ContainAll("nameOfInstitution", "nameOfInstitution is required");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_An_Insert_Request_With_Required_String_Property_Value_Contains_Whitespaces
        : ValidateDocumentMiddlewareTests
    {
        private RequestInfo _requestInfo = No.RequestInfo();

        [SetUp]
        public async Task Setup()
        {
            string jsonData =
                """{"schoolId": 7687,"gradeLevels":{"gradeLevelDescriptor": "grade1"},"nameOfInstitution":"    school name     "}""";

            var frontEndRequest = new FrontendRequest(
                "ed-fi/schools",
                Body: jsonData,
                Headers: [],
                QueryParameters: [],
                TraceId: new TraceId("traceId")
            );
            _requestInfo = Context(frontEndRequest, RequestMethod.POST);
            await Middleware().Execute(_requestInfo, Next());
        }

        [Test]
        public void It_should_not_have_response()
        {
            _requestInfo.FrontendResponse.Should().Be(No.FrontendResponse);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_An_Insert_Request_With_Identity_String_Property_Value_Contains_Only_Whitespaces
        : ValidateDocumentMiddlewareTests
    {
        private RequestInfo _requestInfo = No.RequestInfo();

        [SetUp]
        public async Task Setup()
        {
            string jsonData =
                """{"schoolId": 7687,"gradeLevels":{"gradeLevelDescriptor": "grade1"},"nameOfInstitution":"name", "identityProperty":"        "}""";

            var frontEndRequest = new FrontendRequest(
                "ed-fi/schools",
                Body: jsonData,
                Headers: [],
                QueryParameters: [],
                TraceId: new TraceId("traceId")
            );
            _requestInfo = Context(frontEndRequest, RequestMethod.POST);
            await Middleware().Execute(_requestInfo, Next());
        }

        [Test]
        public void It_has_a_response()
        {
            _requestInfo.FrontendResponse.Should().NotBe(No.FrontendResponse);
        }

        [Test]
        public void It_returns_status_400()
        {
            _requestInfo.FrontendResponse.StatusCode.Should().Be(400);
        }

        [Test]
        public void It_returns_message_body_with_required_validation_error()
        {
            _requestInfo
                .FrontendResponse.Body?.ToJsonString()
                .Should()
                .ContainAll("identityProperty", "cannot contain leading or trailing spaces");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_An_Insert_Request_With_Identity_String_Property_Value_Contains_Whitespaces
        : ValidateDocumentMiddlewareTests
    {
        private RequestInfo _requestInfo = No.RequestInfo();

        [SetUp]
        public async Task Setup()
        {
            string jsonData =
                """{"schoolId": 7687,"gradeLevels":{"gradeLevelDescriptor": "grade1"},"nameOfInstitution":"name", "identityProperty":"identity value    "}""";

            var frontEndRequest = new FrontendRequest(
                "ed-fi/schools",
                Body: jsonData,
                Headers: [],
                QueryParameters: [],
                TraceId: new TraceId("traceId")
            );
            _requestInfo = Context(frontEndRequest, RequestMethod.POST);
            await Middleware().Execute(_requestInfo, Next());
        }

        [Test]
        public void It_has_a_response()
        {
            _requestInfo.FrontendResponse.Should().NotBe(No.FrontendResponse);
        }

        [Test]
        public void It_returns_status_400()
        {
            _requestInfo.FrontendResponse.StatusCode.Should().Be(400);
        }

        [Test]
        public void It_returns_message_body_with_required_validation_error()
        {
            _requestInfo
                .FrontendResponse.Body?.ToJsonString()
                .Should()
                .ContainAll("identityProperty", "cannot contain leading or trailing spaces", "traceId");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_An_Insert_Request_With_Empty_Identity_String_Property : ValidateDocumentMiddlewareTests
    {
        private RequestInfo _requestInfo = No.RequestInfo();

        [SetUp]
        public async Task Setup()
        {
            string jsonData =
                """{"schoolId": 7687,"gradeLevels":{"gradeLevelDescriptor": "grade1"},"nameOfInstitution":"name", "identityProperty":""}""";

            var frontEndRequest = new FrontendRequest(
                "ed-fi/schools",
                Body: jsonData,
                Headers: [],
                QueryParameters: [],
                TraceId: new TraceId("traceId")
            );
            _requestInfo = Context(frontEndRequest, RequestMethod.POST);
            await Middleware().Execute(_requestInfo, Next());
        }

        [Test]
        public void It_has_a_response()
        {
            _requestInfo.FrontendResponse.Should().NotBe(No.FrontendResponse);
        }

        [Test]
        public void It_returns_status_400()
        {
            _requestInfo.FrontendResponse.StatusCode.Should().Be(400);
        }

        [Test]
        public void It_returns_message_body_with_required_validation_error()
        {
            _requestInfo
                .FrontendResponse.Body?.ToJsonString()
                .Should()
                .ContainAll("identityProperty", "is required and should not be left empty.", "traceId");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_An_Insert_Request_With_Optional_String_Property_Value_Contains_Whitespaces
        : ValidateDocumentMiddlewareTests
    {
        private RequestInfo _requestInfo = No.RequestInfo();

        [SetUp]
        public async Task Setup()
        {
            string jsonData =
                """{"schoolId": 7687,"gradeLevels":{"gradeLevelDescriptor": "grade1"},"nameOfInstitution":"name", "optionalProperty":"         "}""";

            var frontEndRequest = new FrontendRequest(
                "ed-fi/schools",
                Body: jsonData,
                Headers: [],
                QueryParameters: [],
                TraceId: new TraceId("traceId")
            );
            _requestInfo = Context(frontEndRequest, RequestMethod.POST);
            await Middleware().Execute(_requestInfo, Next());
        }

        [Test]
        public void It_should_not_have_response()
        {
            _requestInfo.FrontendResponse.Should().Be(No.FrontendResponse);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_An_Insert_Request_With_Empty_NonRequired_Collection : ValidateDocumentMiddlewareTests
    {
        private RequestInfo _requestInfo = No.RequestInfo();

        [SetUp]
        public async Task Setup()
        {
            string jsonData =
                """{"schoolId": 7687,"gradeLevels":{"gradeLevelDescriptor": "grade1"},"collection": [{}]}""";

            var frontEndRequest = new FrontendRequest(
                "ed-fi/schools",
                Body: jsonData,
                Headers: [],
                QueryParameters: [],
                TraceId: new TraceId("traceId")
            );
            _requestInfo = Context(frontEndRequest, RequestMethod.POST);
            await Middleware().Execute(_requestInfo, Next());
        }

        [Test]
        public void It_should_not_have_collection_in_get_response()
        {
            var getContext = Context(
                new FrontendRequest(
                    "ed-fi/schools/7687",
                    Body: null,
                    Headers: [],
                    QueryParameters: [],
                    TraceId: new TraceId("traceId")
                ),
                RequestMethod.GET
            );

            Middleware().Execute(getContext, Next()).Wait();

            getContext.FrontendResponse.Body?.ToJsonString().Should().NotContain("collection");
        }
    }
}
