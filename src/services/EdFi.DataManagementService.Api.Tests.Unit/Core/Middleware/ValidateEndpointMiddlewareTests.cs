// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Api.ApiSchema;
using EdFi.DataManagementService.Api.Core.Middleware;
using EdFi.DataManagementService.Api.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Api.Tests.Unit.Core.Middleware;

public class ValidateEndpointMiddlewareTests
{
    public static Func<Task> Next()
    {
        return () => Task.CompletedTask;
    }

    public static ApiSchemaDocument SchemaDocument()
    {
        return new ApiSchemaBuilder()
            .WithProjectStart("Ed-Fi", "5.0.0")
            .WithResourceStart("School")
            .WithResourceEnd()
            .WithProjectEnd()
            .ToApiSchemaDocument();
    }

    public static IPipelineStep Middleware()
    {
        return new ValidateEndpointMiddleware(NullLogger.Instance);
    }

    [TestFixture]
    public class Given_an_invalid_project_namespace : ValidateEndpointMiddlewareTests
    {
        private PipelineContext _context = No.PipelineContext();

        [SetUp]
        public async Task Setup()
        {
            _context.ApiSchemaDocument = SchemaDocument();
            _context.PathComponents = new(
                ProjectNamespace: new("not-ed-fi"),
                EndpointName: new("schools"),
                DocumentUuid: No.DocumentUuid
            );
            await Middleware().Execute(_context, Next());
        }

        [Test]
        public void It_has_a_response()
        {
            _context?.FrontendResponse.Should().NotBe(No.FrontendResponse);
        }

        [Test]
        public void It_returns_status_404()
        {
            _context?.FrontendResponse.StatusCode.Should().Be(404);
        }

        [Test]
        public void It_returns_message_body()
        {
            _context?.FrontendResponse.Body.Should().Contain("Invalid resource");
        }

        [Test]
        public void It_has_no_project_schema()
        {
            _context?.ProjectSchema.Should().Be(No.ProjectSchema);
        }

        [Test]
        public void It_has_no_resource_schema()
        {
            _context?.ResourceSchema.Should().Be(No.ResourceSchema);
        }
    }

    [TestFixture]
    public class Given_a_valid_project_namespace_and_invalid_endpoint : ValidateEndpointMiddlewareTests
    {
        private PipelineContext _context = No.PipelineContext();

        [SetUp]
        public async Task Setup()
        {
            _context.ApiSchemaDocument = SchemaDocument();
            _context.PathComponents = new(
                ProjectNamespace: new("ed-fi"),
                EndpointName: new("notschools"),
                DocumentUuid: No.DocumentUuid
            );
            await Middleware().Execute(_context, Next());
        }

        [Test]
        public void It_has_a_response()
        {
            _context?.FrontendResponse.Should().NotBe(No.FrontendResponse);
        }

        [Test]
        public void It_returns_status_404()
        {
            _context?.FrontendResponse.StatusCode.Should().Be(404);
        }

        [Test]
        public void It_returns_message_body()
        {
            _context?.FrontendResponse.Body.Should().Contain("Invalid resource");
        }

        [Test]
        public void It_has_a_project_schema_for_edfi()
        {
            _context?.ProjectSchema.ProjectName.Value.Should().Be("Ed-Fi");
        }

        [Test]
        public void It_has_no_resource_schema()
        {
            _context?.ResourceSchema.Should().Be(No.ResourceSchema);
        }
    }

    [TestFixture]
    public class Given_a_valid_project_namespace_and_valid_endpoint : ValidateEndpointMiddlewareTests
    {
        private PipelineContext _context = No.PipelineContext();

        [SetUp]
        public async Task Setup()
        {
            _context.ApiSchemaDocument = SchemaDocument();
            _context.PathComponents = new(
                ProjectNamespace: new("ed-fi"),
                EndpointName: new("schools"),
                DocumentUuid: No.DocumentUuid
            );
            await Middleware().Execute(_context, Next());
        }

        [Test]
        public void It_provides_no_response()
        {
            _context?.FrontendResponse.Should().Be(No.FrontendResponse);
        }

        [Test]
        public void It_has_a_project_schema_for_edfi()
        {
            _context?.ProjectSchema.ProjectName.Value.Should().Be("Ed-Fi");
        }

        [Test]
        public void It_has_a_resource_schema_for_schools()
        {
            _context?.ResourceSchema.Should().NotBe(No.ResourceSchema);
            _context?.ResourceSchema.ResourceName.Value.Should().Be("School");
        }
    }
}
