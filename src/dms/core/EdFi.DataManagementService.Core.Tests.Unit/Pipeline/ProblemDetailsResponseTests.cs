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

    [Test]
    public void It_omits_validationErrors_by_default()
    {
        _body["validationErrors"].Should().BeNull();
    }
}

[TestFixture]
public class Given_a_problem_details_response_is_created_with_multiple_errors
{
    private FrontendResponse _response = default!;
    private JsonNode _body = default!;

    [SetUp]
    public void Setup()
    {
        _response = ProblemDetailsResponse.Create(
            400,
            "urn:ed-fi:api:test-type",
            "Test Title",
            "Multiple issues found",
            ["Error one", "Error two"],
            new TraceId("trace-456")
        );
        _body = _response.Body!;
    }

    [Test]
    public void It_includes_all_errors_in_array()
    {
        var errors = _body["errors"]!.AsArray();
        errors.Count.Should().Be(2);
        errors[0]?.GetValue<string>().Should().Be("Error one");
        errors[1]?.GetValue<string>().Should().Be("Error two");
    }

    [Test]
    public void It_includes_detail_from_parameter()
    {
        _body["detail"]?.GetValue<string>().Should().Be("Multiple issues found");
    }

    [Test]
    public void It_omits_validationErrors_by_default()
    {
        _body["validationErrors"].Should().BeNull();
    }
}

[TestFixture]
public class Given_a_problem_details_response_is_created_with_validation_errors_enabled
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
            new TraceId("trace-789"),
            includeValidationErrors: true
        );
        _body = _response.Body!;
    }

    [Test]
    public void It_includes_an_empty_validationErrors_object()
    {
        _body["validationErrors"].Should().NotBeNull();
        _body["validationErrors"]!.AsObject().Count.Should().Be(0);
    }

    [Test]
    public void It_still_returns_problem_json_content_type()
    {
        _response.ContentType.Should().Be("application/problem+json");
    }
}

[TestFixture]
public class Given_a_problem_details_response_with_multiple_errors_is_created_with_validation_errors_enabled
{
    private JsonNode _body = default!;

    [SetUp]
    public void Setup()
    {
        var response = ProblemDetailsResponse.Create(
            400,
            "urn:ed-fi:api:test-type",
            "Test Title",
            "Multiple issues found",
            ["Error one", "Error two"],
            new TraceId("trace-999"),
            includeValidationErrors: true
        );

        _body = response.Body!;
    }

    [Test]
    public void It_includes_an_empty_validationErrors_object()
    {
        _body["validationErrors"].Should().NotBeNull();
        _body["validationErrors"]!.AsObject().Count.Should().Be(0);
    }
}
