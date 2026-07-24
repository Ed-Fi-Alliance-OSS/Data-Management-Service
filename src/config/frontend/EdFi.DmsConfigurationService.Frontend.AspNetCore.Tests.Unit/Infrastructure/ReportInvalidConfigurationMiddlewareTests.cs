// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Middleware;
using FakeItEasy;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using NUnit.Framework;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Infrastructure;

public class ReportInvalidConfigurationMiddlewareTests
{
    [TestFixture]
    public class Given_invalid_configuration
    {
        private const string Sentinel = "SENTINEL_CONFIG_9d1e_must_not_leak";

        private RequestDelegate _next = null!;
        private TestLogger<ReportInvalidConfigurationMiddleware> _logger = null!;
        private DefaultHttpContext _context = null!;
        private string _content = null!;
        private JsonObject _body = null!;

        [SetUp]
        public async Task Setup()
        {
            _next = A.Fake<RequestDelegate>();
            _logger = new TestLogger<ReportInvalidConfigurationMiddleware>();
            var errors = new List<string> { Sentinel, "another configuration failure" };
            var middleware = new ReportInvalidConfigurationMiddleware(_next, errors);

            _context = new DefaultHttpContext { TraceIdentifier = "trace-config" };
            _context.Response.Body = new MemoryStream();

            await middleware.Invoke(_context, _logger);

            _context.Response.Body.Seek(0, SeekOrigin.Begin);
            _content = await new StreamReader(_context.Response.Body).ReadToEndAsync();
            _body = JsonNode.Parse(_content)!.AsObject();
        }

        [Test]
        public void It_returns_500() => _context.Response.StatusCode.Should().Be(500);

        [Test]
        public void It_uses_the_problem_details_content_type() =>
            _context.Response.ContentType.Should().Be("application/problem+json");

        [Test]
        public void It_returns_the_internal_server_error_type() =>
            _body["type"]!.GetValue<string>().Should().Be("urn:ed-fi:api:internal-server-error");

        [Test]
        public void It_has_the_internal_server_error_title() =>
            _body["title"]!.GetValue<string>().Should().Be("Internal Server Error");

        [Test]
        public void It_has_an_empty_detail() => _body["detail"]!.GetValue<string>().Should().BeEmpty();

        [Test]
        public void It_has_a_body_status_of_500() => _body["status"]!.GetValue<int>().Should().Be(500);

        [Test]
        public void It_uses_the_trace_identifier_as_correlation_id() =>
            _body["correlationId"]!.GetValue<string>().Should().Be("trace-config");

        [Test]
        public void It_includes_empty_extension_members()
        {
            _body["validationErrors"]!.AsObject().Count.Should().Be(0);
            _body["errors"]!.AsArray().Count.Should().Be(0);
        }

        [Test]
        public void It_does_not_include_legacy_error_or_message()
        {
            _body.ContainsKey("error").Should().BeFalse();
            _body.ContainsKey("message").Should().BeFalse();
        }

        [Test]
        public void It_does_not_leak_the_configuration_failure_text() =>
            _content.Should().NotContain(Sentinel);

        [Test]
        public void It_logs_each_configuration_failure_at_critical_level()
        {
            _logger.Entries.FindAll(entry => entry.Level == LogLevel.Critical).Should().HaveCount(2);
            _logger
                .Entries.Exists(entry =>
                    entry.State is not null && entry.State.ToString()!.Contains(Sentinel)
                )
                .Should()
                .BeTrue();
        }

        [Test]
        public void It_does_not_invoke_the_next_delegate() =>
            A.CallTo(() => _next(A<HttpContext>._)).MustNotHaveHappened();
    }
}
