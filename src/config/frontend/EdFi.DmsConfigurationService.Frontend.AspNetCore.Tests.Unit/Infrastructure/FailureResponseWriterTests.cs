// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text;
using System.Text.Json.Nodes;
using EdFi.DmsConfigurationService.DataModel.Infrastructure;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;
using FakeItEasy;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using NUnit.Framework;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Infrastructure;

public class FailureResponseWriterTests
{
    private static async Task<(int StatusCode, string? ContentType, JsonNode Body)> WriteAndReadAsync(
        HttpContext context,
        JsonNode node
    )
    {
        context.Response.Body = new MemoryStream();
        await FailureResponseWriter.WriteAsync(context, node);

        context.Response.Body.Seek(0, SeekOrigin.Begin);
        string bodyText = await new StreamReader(context.Response.Body).ReadToEndAsync();
        JsonNode body = JsonNode.Parse(bodyText)!;
        return (context.Response.StatusCode, context.Response.ContentType, body);
    }

    [TestFixture]
    public class Given_a_not_found_node
    {
        private int _status;
        private string? _contentType;
        private JsonNode _body = null!;

        [SetUp]
        public async Task Setup()
        {
            var context = new DefaultHttpContext { TraceIdentifier = "trace-xyz" };
            // A deliberately different correlationId in the node proves the writer overwrites it
            // with the request's TraceIdentifier.
            JsonNode node = FailureResponse.ForNotFound("Gone", "some-other-correlation-id");
            (_status, _contentType, _body) = await WriteAndReadAsync(context, node);
        }

        [Test]
        public void It_derives_the_http_status_from_the_node() => _status.Should().Be(404);

        [Test]
        public void It_uses_the_problem_details_content_type() =>
            _contentType.Should().Be("application/problem+json");

        [Test]
        public void It_keeps_the_body_status_in_sync_with_the_http_status() =>
            _body["status"]!.GetValue<int>().Should().Be(404);

        [Test]
        public void It_has_the_not_found_type() =>
            _body["type"]!.GetValue<string>().Should().Be("urn:ed-fi:api:not-found");

        [Test]
        public void It_sets_the_correlation_id_from_the_trace_identifier() =>
            _body["correlationId"]!.GetValue<string>().Should().Be("trace-xyz");
    }

    [TestFixture]
    public class Given_an_internal_server_error_node
    {
        private int _status;
        private JsonNode _body = null!;

        [SetUp]
        public async Task Setup()
        {
            var context = new DefaultHttpContext { TraceIdentifier = "trace-500" };
            (_status, _, _body) = await WriteAndReadAsync(context, FailureResponse.ForUnknown("c"));
        }

        [Test]
        public void It_derives_500_from_the_node() => _status.Should().Be(500);

        [Test]
        public void It_keeps_the_body_status_in_sync() => _body["status"]!.GetValue<int>().Should().Be(500);
    }

    [TestFixture]
    public class Given_a_response_with_a_stale_zero_content_length
    {
        private DefaultHttpContext _context = null!;
        private string _bodyText = null!;
        private JsonNode _body = null!;

        [SetUp]
        public async Task Setup()
        {
            _context = new DefaultHttpContext { TraceIdentifier = "trace-zero" };
            _context.Response.Body = new MemoryStream();
            // Simulate a framework bodiless error that already declared Content-Length: 0.
            _context.Response.ContentLength = 0;

            await FailureResponseWriter.WriteAsync(_context, FailureResponse.ForNotFound("Gone", "c"));

            _context.Response.Body.Seek(0, SeekOrigin.Begin);
            _bodyText = await new StreamReader(_context.Response.Body).ReadToEndAsync();
            _body = JsonNode.Parse(_bodyText)!;
        }

        [Test]
        public void It_replaces_the_stale_zero_content_length_with_the_body_byte_length() =>
            _context.Response.ContentLength.Should().Be(Encoding.UTF8.GetByteCount(_bodyText));

        [Test]
        public void It_writes_the_full_body() =>
            _body["type"]!.GetValue<string>().Should().Be("urn:ed-fi:api:not-found");
    }

    [TestFixture]
    public class Given_a_node_without_a_status_member
    {
        private Exception? _exception;

        [SetUp]
        public async Task Setup()
        {
            var context = new DefaultHttpContext();
            context.Response.Body = new MemoryStream();
            JsonNode node = new JsonObject { ["type"] = "urn:ed-fi:api:not-found" };
            try
            {
                await FailureResponseWriter.WriteAsync(context, node);
            }
            catch (Exception ex)
            {
                _exception = ex;
            }
        }

        [Test]
        public void It_throws_an_argument_exception() => _exception.Should().BeOfType<ArgumentException>();
    }

    [TestFixture]
    public class Given_a_response_that_has_already_started
    {
        private HttpResponse _response = null!;

        [SetUp]
        public async Task Setup()
        {
            _response = A.Fake<HttpResponse>();
            A.CallTo(() => _response.HasStarted).Returns(true);
            var context = A.Fake<HttpContext>();
            A.CallTo(() => context.Response).Returns(_response);

            await FailureResponseWriter.WriteAsync(context, FailureResponse.ForUnknown("c"));
        }

        [Test]
        public void It_does_not_set_the_status_code() =>
            A.CallToSet(() => _response.StatusCode).MustNotHaveHappened();

        [Test]
        public void It_does_not_set_the_content_type() =>
            A.CallToSet(() => _response.ContentType).MustNotHaveHappened();
    }
}
