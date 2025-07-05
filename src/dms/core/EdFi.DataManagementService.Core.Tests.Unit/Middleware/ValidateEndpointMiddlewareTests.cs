// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.Middleware;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Middleware;

public class ValidateEndpointMiddlewareTests
{
    public static Func<Task> Next()
    {
        return () => Task.CompletedTask;
    }

    internal static ApiSchemaDocuments SchemaDocuments()
    {
        return new ApiSchemaBuilder()
            .WithStartProject("Ed-Fi", "5.0.0")
            .WithStartResource("School")
            .WithEndResource()
            .WithEndProject()
            .ToApiSchemaDocuments();
    }

    internal static IPipelineStep Middleware()
    {
        return new ValidateEndpointMiddleware(NullLogger.Instance);
    }

    [TestFixture]
    [Parallelizable]
    public class Given_An_Invalid_Project_Namespace : ValidateEndpointMiddlewareTests
    {
        private readonly RequestInfo _requestInfo = No.RequestInfo();

        [SetUp]
        public async Task Setup()
        {
            _requestInfo.ApiSchemaDocuments = SchemaDocuments();
            _requestInfo.PathComponents = new(
                ProjectNamespace: new("not-ed-fi"),
                EndpointName: new("schools"),
                DocumentUuid: No.DocumentUuid
            );
            await Middleware().Execute(_requestInfo, Next());
        }

        [Test]
        public void It_has_a_response()
        {
            _requestInfo?.FrontendResponse.Should().NotBe(No.FrontendResponse);
        }

        [Test]
        public void It_returns_status_404()
        {
            _requestInfo?.FrontendResponse.StatusCode.Should().Be(404);
        }

        [Test]
        public void It_returns_message_body()
        {
            _requestInfo.FrontendResponse.Body?.ToJsonString().Should().Contain("Invalid resource");
        }

        [Test]
        public void It_has_no_project_schema()
        {
            _requestInfo?.ProjectSchema.Should().Be(No.ProjectSchema);
        }

        [Test]
        public void It_has_no_resource_schema()
        {
            _requestInfo?.ResourceSchema.Should().Be(No.ResourceSchema);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Valid_Project_Namespace_And_Invalid_Endpoint : ValidateEndpointMiddlewareTests
    {
        private readonly RequestInfo _requestInfo = No.RequestInfo();

        [SetUp]
        public async Task Setup()
        {
            _requestInfo.ApiSchemaDocuments = SchemaDocuments();
            _requestInfo.PathComponents = new(
                ProjectNamespace: new("ed-fi"),
                EndpointName: new("notschools"),
                DocumentUuid: No.DocumentUuid
            );
            await Middleware().Execute(_requestInfo, Next());
        }

        [Test]
        public void It_has_a_response()
        {
            _requestInfo?.FrontendResponse.Should().NotBe(No.FrontendResponse);
        }

        [Test]
        public void It_returns_status_404()
        {
            _requestInfo?.FrontendResponse.StatusCode.Should().Be(404);
        }

        [Test]
        public void It_returns_message_body()
        {
            _requestInfo
                .FrontendResponse.Body?.ToJsonString()
                .Should()
                .Contain("The specified data could not be found.");
        }

        [Test]
        public void It_returns_content_type_problem_json()
        {
            _requestInfo.FrontendResponse.ContentType.Should().Be("application/problem+json");
        }

        [Test]
        public void It_has_a_project_schema_for_edfi()
        {
            _requestInfo?.ProjectSchema.ProjectName.Value.Should().Be("Ed-Fi");
        }

        [Test]
        public void It_has_no_resource_schema()
        {
            _requestInfo?.ResourceSchema.Should().Be(No.ResourceSchema);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Valid_Project_Namespace_And_Valid_Endpoint : ValidateEndpointMiddlewareTests
    {
        private readonly RequestInfo _requestInfo = No.RequestInfo();

        [SetUp]
        public async Task Setup()
        {
            _requestInfo.ApiSchemaDocuments = SchemaDocuments();
            _requestInfo.PathComponents = new(
                ProjectNamespace: new("ed-fi"),
                EndpointName: new("schools"),
                DocumentUuid: No.DocumentUuid
            );
            await Middleware().Execute(_requestInfo, Next());
        }

        [Test]
        public void It_provides_no_response()
        {
            _requestInfo?.FrontendResponse.Should().Be(No.FrontendResponse);
        }

        [Test]
        public void It_has_a_project_schema_for_edfi()
        {
            _requestInfo?.ProjectSchema.ProjectName.Value.Should().Be("Ed-Fi");
        }

        [Test]
        public void It_has_a_resource_schema_for_schools()
        {
            _requestInfo?.ResourceSchema.Should().NotBe(No.ResourceSchema);
            _requestInfo?.ResourceSchema.ResourceName.Value.Should().Be("School");
        }
    }
}
