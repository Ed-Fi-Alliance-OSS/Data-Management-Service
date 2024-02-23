// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Api.Core.Middleware;
using EdFi.DataManagementService.Api.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Api.Tests.Core.Middleware;

[TestFixture]
public class ParsePathMiddlewareTests
{
    private static readonly Func<Task> _nullNext = () => Task.CompletedTask;

    public static IPipelineStep Middleware()
    {
        return new ParsePathMiddleware(NullLogger.Instance);
    }

    [TestFixture]
    public class Given_an_empty_path : ParsePathMiddlewareTests
    {
        private PipelineContext _context = No.PipelineContext();

        [SetUp]
        public async Task Setup()
        {
            FrontendRequest frontendRequest =
                new(Method: RequestMethod.POST, Body: "{}", Path: "", TraceId: new(""));
            _context = new(frontendRequest);
            await Middleware().Execute(_context, _nullNext);
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
    public class Given_an_invalid_path : ParsePathMiddlewareTests
    {
        private PipelineContext _context = No.PipelineContext();

        [SetUp]
        public async Task Setup()
        {
            FrontendRequest frontendRequest =
                new(Method: RequestMethod.POST, Body: "{}", Path: "badpath", TraceId: new(""));
            _context = new(frontendRequest);
            await Middleware().Execute(_context, _nullNext);
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
    public class Given_a_valid_path_without_resourceId : ParsePathMiddlewareTests
    {
        private PipelineContext _context = No.PipelineContext();

        [SetUp]
        public async Task Setup()
        {
            FrontendRequest frontendRequest =
                new(
                    Method: RequestMethod.POST,
                    Body: "{}",
                    Path: "/ed-fi/endpointName",
                    TraceId: new("")
                );
            _context = new(frontendRequest);
            await Middleware().Execute(_context, _nullNext);
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
    public class Given_a_valid_path_with_valid_resourceId : ParsePathMiddlewareTests
    {
        private PipelineContext _context = No.PipelineContext();
        private readonly string documentUuid = "7825fba8-0b3d-4fc9-ae72-5ad8194d3ce2";

        [SetUp]
        public async Task Setup()
        {
            FrontendRequest frontendRequest =
                new(
                    Method: RequestMethod.POST,
                    Body: "{}",
                    Path: $"/ed-fi/endpointName/{documentUuid}",
                    TraceId: new("")
                );
            _context = new(frontendRequest);
            await Middleware().Execute(_context, _nullNext);
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
    public class Given_a_valid_path_with_invalid_resourceId : ParsePathMiddlewareTests
    {
        private PipelineContext _context = No.PipelineContext();

        [SetUp]
        public async Task Setup()
        {
            FrontendRequest frontendRequest =
                new(
                    Method: RequestMethod.POST,
                    Body: "{}",
                    Path: "/ed-fi/endpointName/invalidId",
                    TraceId: new("")
                );
            _context = new(frontendRequest);
            await Middleware().Execute(_context, _nullNext);
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
}
