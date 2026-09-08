// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Frontend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Middleware;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Middleware;

[TestFixture]
[Parallelizable]
public class ParseTrackedChangePathMiddlewareTests
{
    private static RequestInfo CreateRequestInfo(string path) =>
        new(
            new FrontendRequest(
                Path: path,
                Body: null,
                Form: null,
                Headers: [],
                QueryParameters: [],
                TraceId: new TraceId("trace"),
                RouteQualifiers: []
            ),
            RequestMethod.GET,
            No.ServiceProvider
        );

    [TestFixture]
    [Parallelizable]
    public class Given_A_Valid_Deletes_Path : ParseTrackedChangePathMiddlewareTests
    {
        private RequestInfo _requestInfo = No.RequestInfo();
        private bool _nextCalled;

        [SetUp]
        public async Task Setup()
        {
            var sut = new ParseTrackedChangePathMiddleware(A.Fake<ILogger>());
            _requestInfo = CreateRequestInfo("/ed-fi/schools/deletes");

            await sut.Execute(
                _requestInfo,
                () =>
                {
                    _nextCalled = true;
                    return Task.CompletedTask;
                }
            );
        }

        [Test]
        public void It_calls_next()
        {
            _nextCalled.Should().BeTrue();
        }

        [Test]
        public void It_sets_path_components()
        {
            _requestInfo.PathComponents.ProjectEndpointName.Value.Should().Be("ed-fi");
            _requestInfo.PathComponents.EndpointName.Value.Should().Be("schools");
            _requestInfo.PathComponents.DocumentUuid.Should().Be(No.DocumentUuid);
            _requestInfo.PathComponents.HasDocumentUuidSegment.Should().BeFalse();
        }

        [Test]
        public void It_sets_the_change_query_operation()
        {
            _requestInfo.ChangeQueryOperation.Should().Be(ChangeQueryEndpointOperation.Deletes);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Valid_KeyChanges_Path : ParseTrackedChangePathMiddlewareTests
    {
        private RequestInfo _requestInfo = No.RequestInfo();
        private bool _nextCalled;

        [SetUp]
        public async Task Setup()
        {
            var sut = new ParseTrackedChangePathMiddleware(A.Fake<ILogger>());
            _requestInfo = CreateRequestInfo("/ed-fi/schools/keyChanges");

            await sut.Execute(
                _requestInfo,
                () =>
                {
                    _nextCalled = true;
                    return Task.CompletedTask;
                }
            );
        }

        [Test]
        public void It_calls_next()
        {
            _nextCalled.Should().BeTrue();
        }

        [Test]
        public void It_sets_path_components()
        {
            _requestInfo.PathComponents.ProjectEndpointName.Value.Should().Be("ed-fi");
            _requestInfo.PathComponents.EndpointName.Value.Should().Be("schools");
            _requestInfo.PathComponents.DocumentUuid.Should().Be(No.DocumentUuid);
            _requestInfo.PathComponents.HasDocumentUuidSegment.Should().BeFalse();
        }

        [Test]
        public void It_sets_the_change_query_operation()
        {
            _requestInfo.ChangeQueryOperation.Should().Be(ChangeQueryEndpointOperation.KeyChanges);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Mixed_Case_Operation_And_Project_Path : ParseTrackedChangePathMiddlewareTests
    {
        private RequestInfo _requestInfo = No.RequestInfo();
        private bool _nextCalled;

        [SetUp]
        public async Task Setup()
        {
            var sut = new ParseTrackedChangePathMiddleware(A.Fake<ILogger>());
            _requestInfo = CreateRequestInfo("/ED-FI/schools/KEYCHANGES");

            await sut.Execute(
                _requestInfo,
                () =>
                {
                    _nextCalled = true;
                    return Task.CompletedTask;
                }
            );
        }

        [Test]
        public void It_calls_next()
        {
            _nextCalled.Should().BeTrue();
        }

        [Test]
        public void It_lowercases_the_project_endpoint_name()
        {
            _requestInfo.PathComponents.ProjectEndpointName.Value.Should().Be("ed-fi");
        }

        [Test]
        public void It_sets_the_change_query_operation_case_insensitively()
        {
            _requestInfo.ChangeQueryOperation.Should().Be(ChangeQueryEndpointOperation.KeyChanges);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Malformed_Two_Segment_Path : ParseTrackedChangePathMiddlewareTests
    {
        [Test]
        public async Task It_returns_not_found_and_does_not_call_next()
        {
            await AssertNotFound("/ed-fi/schools");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_An_Unknown_Operation_Path : ParseTrackedChangePathMiddlewareTests
    {
        [Test]
        public async Task It_returns_not_found_and_does_not_call_next()
        {
            await AssertNotFound("/ed-fi/schools/updates");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Path_With_An_Extra_Segment : ParseTrackedChangePathMiddlewareTests
    {
        [Test]
        public async Task It_returns_not_found_and_does_not_call_next()
        {
            await AssertNotFound("/ed-fi/schools/deletes/extra");
        }
    }

    private static async Task AssertNotFound(string path)
    {
        var sut = new ParseTrackedChangePathMiddleware(A.Fake<ILogger>());
        RequestInfo requestInfo = CreateRequestInfo(path);
        var nextCalled = false;

        await sut.Execute(
            requestInfo,
            () =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            }
        );

        nextCalled.Should().BeFalse();
        requestInfo.FrontendResponse.StatusCode.Should().Be(404);
        requestInfo.FrontendResponse.Body.Should().NotBeNull();
        requestInfo.FrontendResponse.Body!["detail"]!
            .GetValue<string>()
            .Should()
            .Be("The specified data could not be found.");
        requestInfo.FrontendResponse.Headers.Should().BeEmpty();
        requestInfo.FrontendResponse.ContentType.Should().Be("application/problem+json");
        requestInfo.PathComponents.Should().Be(No.PathComponents);
        requestInfo.ChangeQueryOperation.Should().BeNull();
    }
}
