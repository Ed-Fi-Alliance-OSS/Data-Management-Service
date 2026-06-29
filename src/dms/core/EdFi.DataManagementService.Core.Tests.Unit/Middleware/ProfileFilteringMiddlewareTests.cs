// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.External.Frontend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Middleware;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Middleware;

[TestFixture]
[Parallelizable]
public class Given_Profile_Filtering_Middleware
{
    private RequestInfo _requestInfo = null!;
    private bool _nextCalled;

    [SetUp]
    public async Task Setup()
    {
        _requestInfo = new RequestInfo(
            new FrontendRequest(
                Path: "/ed-fi/students",
                Body: "{}",
                Form: null,
                Headers: [],
                QueryParameters: [],
                TraceId: new TraceId("test-trace-id"),
                RouteQualifiers: []
            ),
            RequestMethod.GET,
            No.ServiceProvider
        )
        {
            FrontendResponse = new FrontendResponse(
                StatusCode: 200,
                Body: new JsonObject
                {
                    ["id"] = "12345",
                    ["studentUniqueId"] = "STU001",
                    ["firstName"] = "John",
                    ["lastName"] = "Doe",
                },
                Headers: [],
                ContentType: "application/vnd.ed-fi.student.testprofile.readable+json"
            ),
        };

        _nextCalled = false;

        await new ProfileFilteringMiddleware().Execute(
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
    public void It_leaves_readable_projection_and_content_type_to_the_relational_read_path()
    {
        _requestInfo.FrontendResponse.Body!["lastName"]?.GetValue<string>().Should().Be("Doe");
        _requestInfo
            .FrontendResponse.ContentType.Should()
            .Be("application/vnd.ed-fi.student.testprofile.readable+json");
    }
}
