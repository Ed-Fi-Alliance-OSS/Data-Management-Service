// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Pipeline;

[TestFixture]
public class Given_a_problem_details_response_is_created
{
    private FrontendResponse _response = default!;
    private JsonNode _body = default!;

    [SetUp]
    public void Setup()
    {
        _response = ProblemDetailsResponse.Create(
            503,
            "urn:ed-fi:api:test-type",
            "Test Title",
            "Something went wrong",
            new TraceId("trace-123")
        );
        _body = _response.Body!;
    }

    [Test]
    public void It_returns_the_explicit_type_not_derived_from_title()
    {
        _body["type"]?.GetValue<string>().Should().Be("urn:ed-fi:api:test-type");
    }

    [Test]
    public void It_returns_correct_status_code()
    {
        _response.StatusCode.Should().Be(503);
    }

    [Test]
    public void It_includes_error_detail_in_errors_array()
    {
        _body["errors"]?[0]?.GetValue<string>().Should().Be("Something went wrong");
    }

    [Test]
    public void It_includes_correlationId_from_traceId()
    {
        _body["correlationId"]?.GetValue<string>().Should().Be("trace-123");
    }

    [Test]
    public void It_returns_problem_json_content_type()
    {
        _response.ContentType.Should().Be("application/problem+json");
    }

    [Test]
    public void It_includes_title_in_body()
    {
        _body["title"]?.GetValue<string>().Should().Be("Test Title");
    }

    [Test]
    public void It_includes_detail_in_body()
    {
        _body["detail"]?.GetValue<string>().Should().Be("Something went wrong");
    }
}
