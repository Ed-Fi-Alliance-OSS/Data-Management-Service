// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Infrastructure;

public class FailureResultsTests
{
    private sealed record ExecutedResult(int StatusCode, string? ContentType, JsonNode Body);

    private static async Task<ExecutedResult> ExecuteAsync(IResult result)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var context = new DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider(),
            TraceIdentifier = "test-trace-id",
        };
        context.Response.Body = new MemoryStream();

        await result.ExecuteAsync(context);

        context.Response.Body.Seek(0, SeekOrigin.Begin);
        string bodyText = await new StreamReader(context.Response.Body).ReadToEndAsync();
        JsonNode body = JsonNode.Parse(bodyText)!;
        return new ExecutedResult(context.Response.StatusCode, context.Response.ContentType, body);
    }

    [TestFixture]
    public class Given_a_not_found_failure_result
    {
        private ExecutedResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            _result = await ExecuteAsync(FailureResults.NotFound("Widget not found", "corr-1"));
        }

        [Test]
        public void It_returns_404() => _result.StatusCode.Should().Be(404);

        [Test]
        public void It_uses_the_problem_details_content_type() =>
            _result.ContentType.Should().Be("application/problem+json");

        [Test]
        public void It_has_the_not_found_type() =>
            _result.Body["type"]!.GetValue<string>().Should().Be("urn:ed-fi:api:not-found");

        [Test]
        public void It_has_a_body_status_matching_the_http_status() =>
            _result.Body["status"]!.GetValue<int>().Should().Be(404);

        [Test]
        public void It_includes_the_correlation_id() =>
            _result.Body["correlationId"]!.GetValue<string>().Should().Be("corr-1");

        [Test]
        public void It_includes_an_empty_validation_errors_object() =>
            _result.Body["validationErrors"]!.AsObject().Count.Should().Be(0);

        [Test]
        public void It_includes_an_empty_errors_array() =>
            _result.Body["errors"]!.AsArray().Count.Should().Be(0);
    }

    [TestFixture]
    public class Given_an_unknown_failure_result
    {
        private ExecutedResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            _result = await ExecuteAsync(FailureResults.Unknown("corr-2"));
        }

        [Test]
        public void It_returns_500() => _result.StatusCode.Should().Be(500);

        [Test]
        public void It_uses_the_problem_details_content_type() =>
            _result.ContentType.Should().Be("application/problem+json");

        [Test]
        public void It_has_the_internal_server_error_type() =>
            _result.Body["type"]!.GetValue<string>().Should().Be("urn:ed-fi:api:internal-server-error");

        [Test]
        public void It_has_a_body_status_matching_the_http_status() =>
            _result.Body["status"]!.GetValue<int>().Should().Be(500);

        [Test]
        public void It_does_not_expose_any_detail() =>
            _result.Body["detail"]!.GetValue<string>().Should().BeEmpty();
    }

    [TestFixture]
    public class Given_an_authorization_failure_result
    {
        private ExecutedResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            _result = await ExecuteAsync(
                FailureResults.Authorization("corr-3", ["Registration is disabled."])
            );
        }

        [Test]
        public void It_returns_403() => _result.StatusCode.Should().Be(403);

        [Test]
        public void It_uses_the_problem_details_content_type() =>
            _result.ContentType.Should().Be("application/problem+json");

        [Test]
        public void It_has_the_authorization_type() =>
            _result.Body["type"]!.GetValue<string>().Should().Be("urn:ed-fi:api:security:authorization");

        [Test]
        public void It_has_the_authorization_title() =>
            _result.Body["title"]!.GetValue<string>().Should().Be("Authorization Failed");

        [Test]
        public void It_has_a_body_status_matching_the_http_status() =>
            _result.Body["status"]!.GetValue<int>().Should().Be(403);

        // Verifies D-02: the explicit errors array is passed through verbatim, with NO implicit
        // identity-provider payload parsing (which would prepend "Forbidden. ").
        [Test]
        public void It_exposes_the_explicit_error_verbatim() =>
            _result.Body["errors"]!
                .AsArray()
                .Select(e => e!.GetValue<string>())
                .Should()
                .ContainSingle()
                .Which.Should()
                .Be("Registration is disabled.");

        [Test]
        public void It_includes_an_empty_validation_errors_object() =>
            _result.Body["validationErrors"]!.AsObject().Count.Should().Be(0);
    }
}
