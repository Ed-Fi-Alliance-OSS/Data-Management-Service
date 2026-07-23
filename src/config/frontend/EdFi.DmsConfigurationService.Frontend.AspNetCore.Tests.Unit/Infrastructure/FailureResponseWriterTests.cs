// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

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
    public class Given_a_node_without_a_status_member
    {
        [Test]
        public async Task It_throws_ArgumentException()
        {
            var context = new DefaultHttpContext();
            context.Response.Body = new MemoryStream();
            JsonNode node = new JsonObject { ["type"] = "urn:ed-fi:api:not-found" };

            Func<Task> act = () => FailureResponseWriter.WriteAsync(context, node);

            await act.Should().ThrowAsync<ArgumentException>();
        }
    }

    [TestFixture]
    public class Given_a_response_that_has_already_started
    {
        [Test]
        public async Task It_does_not_write_or_change_the_status()
        {
            var response = A.Fake<HttpResponse>();
            A.CallTo(() => response.HasStarted).Returns(true);
            var context = A.Fake<HttpContext>();
            A.CallTo(() => context.Response).Returns(response);

            await FailureResponseWriter.WriteAsync(context, FailureResponse.ForUnknown("c"));

            A.CallToSet(() => response.StatusCode).MustNotHaveHappened();
            A.CallToSet(() => response.ContentType).MustNotHaveHappened();
        }
    }
}
