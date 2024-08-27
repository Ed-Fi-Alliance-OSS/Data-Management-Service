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
using System.Text.Json;
using NUnit.Framework;
using static EdFi.DataManagementService.Core.Tests.Unit.TestHelper;
using static EdFi.DataManagementService.Core.UtilityService;

namespace EdFi.DataManagementService.Core.Tests.Unit.Middleware;

[TestFixture]
public class ParsePathMiddlewareTests
{
    internal static IPipelineStep Middleware()
    {
        return new ParsePathMiddleware(NullLogger.Instance);
    }

    [TestFixture]
    public class Given_An_Empty_Path : ParsePathMiddlewareTests
    {
        private PipelineContext _context = No.PipelineContext();

        [SetUp]
        public async Task Setup()
        {
            FrontendRequest frontendRequest =
                new(Body: "{}", Path: "", QueryParameters: [], TraceId: new TraceId(""));
            _context = new(frontendRequest, RequestMethod.POST);
            await Middleware().Execute(_context, NullNext);
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
    }

    [TestFixture]
    public class Given_An_Invalid_Path : ParsePathMiddlewareTests
    {
        private PipelineContext _context = No.PipelineContext();

        [SetUp]
        public async Task Setup()
        {
            FrontendRequest frontendRequest =
                new(Body: "{}", Path: "badpath", QueryParameters: [], TraceId: new TraceId(""));
            _context = new(frontendRequest, RequestMethod.POST);
            await Middleware().Execute(_context, NullNext);
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
    }

    [TestFixture]
    public class Given_A_Valid_Path_Without_ResourceId : ParsePathMiddlewareTests
    {
        private PipelineContext _context = No.PipelineContext();

        [SetUp]
        public async Task Setup()
        {
            FrontendRequest frontendRequest =
                new(Body: "{}", Path: "/ed-fi/endpointName", QueryParameters: [], TraceId: new TraceId(""));
            _context = new(frontendRequest, RequestMethod.POST);
            await Middleware().Execute(_context, NullNext);
        }

        [Test]
        public void It_provides_no_response()
        {
            _context?.FrontendResponse.Should().Be(No.FrontendResponse);
        }

        [Test]
        public void It_provides_correct_path_components()
        {
            _context?.PathComponents.Should().NotBe(No.PathComponents);

            _context?.PathComponents.ProjectNamespace.Value.Should().Be("ed-fi");
            _context?.PathComponents.EndpointName.Value.Should().Be("endpointName");
        }
    }

    [TestFixture]
    public class Given_A_Valid_Path_With_Valid_ResourceId : ParsePathMiddlewareTests
    {
        private PipelineContext _context = No.PipelineContext();
        private readonly string documentUuid = "7825fba8-0b3d-4fc9-ae72-5ad8194d3ce2";

        [SetUp]
        public async Task Setup()
        {
            FrontendRequest frontendRequest =
                new(
                    Body: "{}",
                    Path: $"/ed-fi/endpointName/{documentUuid}",
                    QueryParameters: [],
                    TraceId: new TraceId("")
                );
            _context = new(frontendRequest, RequestMethod.POST);
            await Middleware().Execute(_context, NullNext);
        }

        [Test]
        public void It_provides_no_response()
        {
            _context?.FrontendResponse.Should().Be(No.FrontendResponse);
        }

        [Test]
        public void It_provides_correct_path_components()
        {
            _context?.PathComponents.Should().NotBe(No.PathComponents);

            _context?.PathComponents.ProjectNamespace.Value.Should().Be("ed-fi");
            _context?.PathComponents.EndpointName.Value.Should().Be("endpointName");
            _context?.PathComponents.DocumentUuid.Value.Should().Be(documentUuid);
        }
    }

    [TestFixture]
    public class Given_A_Valid_Path_With_Invalid_ResourceId : ParsePathMiddlewareTests
    {
        private PipelineContext _context = No.PipelineContext();

        [SetUp]
        public async Task Setup()
        {
            FrontendRequest frontendRequest =
                new(
                    Body: "{}",
                    Path: "/ed-fi/endpointName/invalidId",
                    QueryParameters: [],
                    TraceId: new TraceId("")
                );
            _context = new(frontendRequest, RequestMethod.POST);
            await Middleware().Execute(_context, NullNext);
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
        public void It_returns_invalid_Id_message()
        {
            string response = JsonSerializer.Serialize(_context.FrontendResponse.Body, SerializerOptions);

            response
                .Should()
                .Contain("\"validationErrors\":{\"$.id\":[\"The value 'invalidId' is not valid.\"]}");
        }
    }
}
