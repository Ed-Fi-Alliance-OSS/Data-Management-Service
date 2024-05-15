// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.Middleware;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Validation;
using EdFi.DataManagementService.Core.Pipeline;
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

    internal static ApiSchemaDocument SchemaDocument()
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
                            ("gradeLevelDescriptor", new JsonSchemaBuilder().Type(SchemaValueType.String))
                        )
                        .Required("gradeLevelDescriptor")
                        .AdditionalProperties(false)
                ),
                ("nameOfInstitution", new JsonSchemaBuilder().Type(SchemaValueType.String)),
                ("webSite", new JsonSchemaBuilder().Type(SchemaValueType.String).MinLength(5).MaxLength(10))
            )
            .Required("schoolId", "gradeLevels", "nameOfInstitution");

        return new ApiSchemaBuilder()
            .WithStartProject("Ed-Fi", "5.0.0")
            .WithStartResource("School")
            .WithJsonSchemaForInsert(builder.Build()!)
            .WithEndResource()
            .WithEndProject()
            .ToApiSchemaDocument();
    }

    internal static IPipelineStep Middleware()
    {
        var documentValidator = new DocumentValidator();
        return new ValidateDocumentMiddleware(NullLogger.Instance, documentValidator);
    }

    internal PipelineContext Context(FrontendRequest frontendRequest, RequestMethod method)
    {
        PipelineContext _context =
            new(frontendRequest, method)
            {
                ApiSchemaDocument = SchemaDocument(),
                PathComponents = new(
                    ProjectNamespace: new("ed-fi"),
                    EndpointName: new("schools"),
                    DocumentUuid: No.DocumentUuid
                )
            };
        _context.ProjectSchema = new ProjectSchema(
            _context.ApiSchemaDocument.FindProjectSchemaNode(new("ed-fi")) ?? new JsonObject(),
            NullLogger.Instance
        );
        _context.ResourceSchema = new ResourceSchema(
            _context.ProjectSchema.FindResourceSchemaNode(new("schools")) ?? new JsonObject(),
            NullLogger.Instance
        );
        return _context;
    }

    [TestFixture]
    public class Given_an_empty_body : ValidateDocumentMiddlewareTests
    {
        private PipelineContext _context = No.PipelineContext();

        [SetUp]
        public async Task Setup()
        {
            var frontEndRequest = new FrontendRequest(
                "ed-fi/schools",
                Body: null,
                QueryParameters: [],
                "traceId"
            );
            _context = Context(frontEndRequest, RequestMethod.POST);
            await Middleware().Execute(_context, Next());
        }

        [Test]
        public void It_has_a_response()
        {
            _context?.FrontendResponse.Should().NotBe(No.FrontendResponse);
        }

        [Test]
        public void It_returns_status_400()
        {
            _context?.FrontendResponse.StatusCode.Should().Be(400);
        }

        [Test]
        public void It_returns_message_body()
        {
            _context?.FrontendResponse.Body.Should().Contain("A non-empty request body is required");
        }
    }

    [TestFixture]
    public class Given_a_request_with_overposted_property : ValidateDocumentMiddlewareTests
    {
        private PipelineContext _context = No.PipelineContext();

        [SetUp]
        public async Task Setup()
        {
            string jsonData =
                """{"schoolId": 989, "gradeLevels":{"gradeLevelDescriptor": "grade1"},"nameOfInstitution":"school12", "propertyOverPost": "overpostedvalue"}""";

            var frontEndRequest = new FrontendRequest(
                "ed-fi/schools",
                Body: JsonNode.Parse(jsonData),
                QueryParameters: [],
               "traceId"
            );
            _context = Context(frontEndRequest, RequestMethod.POST);
            await Middleware().Execute(_context, Next());
        }

        [Test]
        public void It_should_not_have_response()
        {
            _context?.FrontendResponse.Should().Be(No.FrontendResponse);
        }
    }

    [TestFixture]
    public class Given_a_request_with_overposted_nested_property : ValidateDocumentMiddlewareTests
    {
        private PipelineContext _context = No.PipelineContext();

        [SetUp]
        public async Task Setup()
        {
            string jsonData =
                """{"schoolId": 989, "gradeLevels":{"gradeLevelDescriptor": "grade1", "gradeLevelOverPost": "overPostedValue"},"nameOfInstitution":"school12", "propertyOverPost": "overPostedValue"}""";

            var frontEndRequest = new FrontendRequest(
                "ed-fi/schools",
                Body: JsonNode.Parse(jsonData),
                QueryParameters: [],
                "traceId"
            );
            _context = Context(frontEndRequest, RequestMethod.POST);
            await Middleware().Execute(_context, Next());
        }

        [Test]
        public void It_should_not_have_response()
        {
            _context?.FrontendResponse.Should().Be(No.FrontendResponse);
        }
    }

    [TestFixture]
    public class Given_a_request_with_no_required_property : ValidateDocumentMiddlewareTests
    {
        private PipelineContext _context = No.PipelineContext();

        [SetUp]
        public async Task Setup()
        {
            string jsonData =
                """{"gradeLevels":{"gradeLevelDescriptor": "grade1"},"nameOfInstitution":"school12"}""";

            var frontEndRequest = new FrontendRequest(
                "ed-fi/schools",
                Body: JsonNode.Parse(jsonData),
                QueryParameters: [],
                "traceId"
            );
            _context = Context(frontEndRequest, RequestMethod.POST);
            await Middleware().Execute(_context, Next());
        }

        [Test]
        public void It_has_a_response()
        {
            _context?.FrontendResponse.Should().NotBe(No.FrontendResponse);
        }

        [Test]
        public void It_returns_status_400()
        {
            _context?.FrontendResponse.StatusCode.Should().Be(400);
        }

        [Test]
        public void It_returns_message_body_with_required_validation_error()
        {
            _context?.FrontendResponse.Body.Should().Contain("is required");
            _context?.FrontendResponse.Body.Should().Contain("schoolId");
        }
    }

    [TestFixture]
    public class Given_a_request_with_wrong_type_property_value : ValidateDocumentMiddlewareTests
    {
        private PipelineContext _context = No.PipelineContext();

        [SetUp]
        public async Task Setup()
        {
            string jsonData =
                """{"schoolId": "wrongvalue","gradeLevels":{"gradeLevelDescriptor": "grade1"},"nameOfInstitution":"school12"}""";

            var frontEndRequest = new FrontendRequest(
                "ed-fi/schools",
                Body: JsonNode.Parse(jsonData),
                QueryParameters: [],
                "traceId"
            );
            _context = Context(frontEndRequest, RequestMethod.POST);
            await Middleware().Execute(_context, Next());
        }

        [Test]
        public void It_has_a_response()
        {
            _context?.FrontendResponse.Should().NotBe(No.FrontendResponse);
        }

        [Test]
        public void It_returns_status_400()
        {
            _context?.FrontendResponse.StatusCode.Should().Be(400);
        }

        [Test]
        public void It_returns_message_body_with_wrong_data_type_validation_error()
        {
            _context?.FrontendResponse.Body.Should().Contain("schoolId : Value is");
            _context?.FrontendResponse.Body.Should().Contain("integer");
        }
    }

    [TestFixture]
    public class Given_a_update_request_with_no_id_property : ValidateDocumentMiddlewareTests
    {
        private PipelineContext _context = No.PipelineContext();

        [SetUp]
        public async Task Setup()
        {
            string jsonData =
                """{"schoolId": 7687,"gradeLevels":{"gradeLevelDescriptor": "grade1"},"nameOfInstitution":"school12"}""";

            var frontEndRequest = new FrontendRequest(
                "ed-fi/schools",
                Body: JsonNode.Parse(jsonData),
                QueryParameters: [],
                "traceId"
            );
            _context = Context(frontEndRequest, RequestMethod.PUT);
            await Middleware().Execute(_context, Next());
        }

        [Test]
        public void It_has_a_response()
        {
            _context?.FrontendResponse.Should().NotBe(No.FrontendResponse);
        }

        [Test]
        public void It_returns_status_400()
        {
            _context?.FrontendResponse.StatusCode.Should().Be(400);
        }

        [Test]
        public void It_returns_message_body_with_required_validation_error()
        {
            _context?.FrontendResponse.Body.Should().Contain("is required");
            _context?.FrontendResponse.Body.Should().Contain("id");
        }
    }
}
