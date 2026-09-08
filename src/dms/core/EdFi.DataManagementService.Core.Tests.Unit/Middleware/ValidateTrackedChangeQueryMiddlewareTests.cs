// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Middleware;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Middleware;

[TestFixture]
[Parallelizable]
public class ValidateTrackedChangeQueryMiddlewareTests
{
    [TestFixture]
    [Parallelizable]
    public class Given_No_Parsed_Query_Elements : ValidateTrackedChangeQueryMiddlewareTests
    {
        private bool _nextCalled;
        private RequestInfo _requestInfo = No.RequestInfo();

        [SetUp]
        public async Task Setup()
        {
            _requestInfo = No.RequestInfo("tracked-change-query");

            var sut = new ValidateTrackedChangeQueryMiddleware(NullLogger.Instance);
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
        public void It_continues_the_pipeline() => _nextCalled.Should().BeTrue();

        [Test]
        public void It_does_not_replace_the_response() =>
            _requestInfo.FrontendResponse.Should().BeSameAs(No.FrontendResponse);
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Parsed_Resource_Query_Element : ValidateTrackedChangeQueryMiddlewareTests
    {
        private bool _nextCalled;
        private RequestInfo _requestInfo = No.RequestInfo();

        [SetUp]
        public async Task Setup()
        {
            _requestInfo = No.RequestInfo("tracked-change-query");
            _requestInfo.QueryElements =
            [
                new(
                    QueryFieldName: "schoolId",
                    DocumentPaths: [new JsonPath("$.schoolId")],
                    Value: "8118601",
                    Type: "number"
                ),
            ];

            var sut = new ValidateTrackedChangeQueryMiddleware(NullLogger.Instance);
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
        public void It_stops_the_pipeline() => _nextCalled.Should().BeFalse();

        [Test]
        public void It_returns_bad_request()
        {
            _requestInfo.FrontendResponse.StatusCode.Should().Be(400);
            _requestInfo.FrontendResponse.Headers.Should().BeEmpty();
            _requestInfo.FrontendResponse.Body.Should().NotBeNull();
        }

        [Test]
        public void It_reports_that_resource_query_fields_are_not_valid_for_tracked_change_endpoints()
        {
            _requestInfo.FrontendResponse.Body!["errors"]![0]!
                .GetValue<string>()
                .Should()
                .Be("The query field 'schoolId' is not valid for this Change Query endpoint.");
        }
    }
}
