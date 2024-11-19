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
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Middleware;

public class ValidateDescriptorMiddlewareTests
{
    public static Func<Task> Next()
    {
        return () => Task.CompletedTask;
    }

    internal static IPipelineStep Middleware()
    {
        var descriptorValidator = new DescriptorValidator();
        return new ValidateDescriptorMiddleware(NullLogger.Instance, descriptorValidator);
    }

    internal static ApiSchemaDocument SchemaDocument()
    {
        return new ApiSchemaBuilder()
            .WithStartProject()
            .WithStartResource("StateAbbreviationDescriptor", isDescriptor: true)
            .WithEndResource()
            .WithEndProject()
            .ToApiSchemaDocument();
    }

    internal PipelineContext Context(FrontendRequest frontendRequest, RequestMethod method)
    {
        PipelineContext _context =
            new(frontendRequest, method)
            {
                ApiSchemaDocument = SchemaDocument(),
                PathComponents = new(
                    ProjectNamespace: new("ed-fi"),
                    EndpointName: new("stateAbbreviationDescriptors"),
                    DocumentUuid: No.DocumentUuid
                ),
            };
        _context.ProjectSchema = new ProjectSchema(
            _context.ApiSchemaDocument.FindProjectSchemaNode(new("ed-fi")) ?? new JsonObject(),
            NullLogger.Instance
        );
        _context.ResourceSchema = new ResourceSchema(
            _context.ProjectSchema.FindResourceSchemaNode(new("stateAbbreviationDescriptors")) ?? new JsonObject()
        );

        if (_context.FrontendRequest.Body != null)
        {
            var body = JsonNode.Parse(_context.FrontendRequest.Body);
            if (body != null)
            {
                _context.ParsedBody = body;
            }
        }
        return _context;
    }

    [TestFixture]
    public class GivenAValidBody : ValidateDescriptorMiddlewareTests
    {
        private PipelineContext _context = No.PipelineContext();

        [SetUp]
        public async Task Setup()
        {
            var jsonData = """
                           {
                               "codeValue": "IL",
                               "description": "Illinois",
                               "namespace": "uri://ed-fi.org/StateAbbreviationDescriptor",
                               "shortDescription": "IL"
                           }
                           """;
            var frontEndRequest = new FrontendRequest(
                "ed-fi/stateAbbreviationDescriptors",
                Body: jsonData,
                QueryParameters: [],
                new TraceId("traceId")
            );
            _context = Context(frontEndRequest, RequestMethod.POST);
            await Middleware().Execute(_context, Next());
        }

        [Test]
        public void It_provides_no_response()
        {
            _context?.FrontendResponse.Should().Be(No.FrontendResponse);
        }
    }

    [TestFixture("ui://ed-fi.org/StateAbbreviationDescriptor")]
    [TestFixture("uri://ed-fi.org/StateAbbreviation")]
    [TestFixture("uri://ed-fi.org/InvalidSuffix")]
    [TestFixture("uri://not a valid uri/StateAbbreviationDescriptor")]
    public class GivenAnInvalidNamespace(string invalidNamespace) : ValidateDescriptorMiddlewareTests
    {
        private PipelineContext _context = No.PipelineContext();

        [SetUp]
        public async Task Setup()
        {
            var jsonData = """
                           {
                               "codeValue": "IL",
                               "description": "Illinois",
                               "namespace": "{invalidNamespace}",
                               "shortDescription": "IL"
                           }
                           """.Replace("{invalidNamespace}", invalidNamespace);
            var frontEndRequest = new FrontendRequest(
                "ed-fi/stateAbbreviationDescriptors",
                Body: jsonData,
                QueryParameters: [],
                new TraceId("traceId")
            );
            _context = Context(frontEndRequest, RequestMethod.POST);
            await Middleware().Execute(_context, Next());
        }

        [Test]
        public void It_returns_status_400()
        {
            _context?.FrontendResponse.StatusCode.Should().Be(400);
        }
    }
}
