// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Api.ApiSchema;
using EdFi.DataManagementService.Api.Core.Middleware;
using EdFi.DataManagementService.Api.Core.Model;
using EdFi.DataManagementService.Api.Core.Validation;
using EdFi.DataManagementService.Core.Pipeline;
using FluentAssertions;
using Json.Schema;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Api.Tests.Core.Middleware;

public class ValidateDocumentMiddlewareTests
{
    public static Func<Task> Next()
    {
        return () => Task.CompletedTask;
    }

    public static ApiSchemaDocument SchemaDocument()
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
                ),
                ("nameOfInstitution", new JsonSchemaBuilder().Type(SchemaValueType.String)),
                ("webSite", new JsonSchemaBuilder().Type(SchemaValueType.String).MinLength(5).MaxLength(10))
            )
            .Required("schoolId", "gradeLevels", "nameOfInstitution");

        return new ApiSchemaBuilder()
            .WithProjectStart("Ed-Fi", "5.0.0")
            .WithResourceStart("School")
            .WithJsonSchemaForInsert(builder.Build()!)
            .WithResourceEnd()
            .WithProjectEnd()
            .ToApiSchemaDocument();
    }

    public static IPipelineStep Middleware()
    {
        var schemaValidator = new SchemaValidator();
        var documentValidator = new DocumentValidator(schemaValidator);
        return new ValidateDocumentMiddleware(NullLogger.Instance, documentValidator);
    }

    public PipelineContext Context(FrontendRequest frontendRequest)
    {
        PipelineContext _context =
            new(frontendRequest)
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
                RequestMethod.POST,
                "ed-fi/schools",
                Body: null,
                new TraceId("traceId")
            );
            _context = Context(frontEndRequest);
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
    public class Given_a_request_with_not_existing_property : ValidateDocumentMiddlewareTests
    {
        private PipelineContext _context = No.PipelineContext();

        [SetUp]
        public async Task Setup()
        {
            string jsonData =
                """{"schoolId": 989, "gradeLevels":{"gradeLevelDescriptor": "grade1"},"nameOfInstitution":"school12", "propertyOverPost": "overpostedvalue"}""";

            var frontEndRequest = new FrontendRequest(
                RequestMethod.POST,
                "ed-fi/schools",
                Body: JsonNode.Parse(jsonData),
                new TraceId("traceId")
            );
            _context = Context(frontEndRequest);
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
        public void It_returns_message_body_with_overpost_validation_error()
        {
            _context?.FrontendResponse.Body.Should().Contain("propertyOverPost : Overpost");
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
                RequestMethod.POST,
                "ed-fi/schools",
                Body: JsonNode.Parse(jsonData),
                new TraceId("traceId")
            );
            _context = Context(frontEndRequest);
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
            _context?.FrontendResponse.Body.Should().Contain("Required properties");
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
                RequestMethod.POST,
                "ed-fi/schools",
                Body: JsonNode.Parse(jsonData),
                new TraceId("traceId")
            );
            _context = Context(frontEndRequest);
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
                RequestMethod.PUT,
                "ed-fi/schools",
                Body: JsonNode.Parse(jsonData),
                new TraceId("traceId")
            );
            _context = Context(frontEndRequest);
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
            _context?.FrontendResponse.Body.Should().Contain("Required properties");
            _context?.FrontendResponse.Body.Should().Contain("id");
        }
    }
}
