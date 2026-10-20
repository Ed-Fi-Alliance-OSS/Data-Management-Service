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
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Middleware;

public class NormalizePutIdMiddlewareTests
{
    internal const string UrlId = "e0dca35a-9fe4-4711-9b18-c0a6c55876d7";

    internal static RequestInfo Context(string body)
    {
        RequestInfo request = No.RequestInfo();
        request.Method = RequestMethod.PUT;
        request.ParsedBody = JsonNode.Parse(body)!;
        request.PathComponents = new(
            ProjectEndpointName: new("ed-fi"),
            EndpointName: new("schools"),
            Operation: new ResourcePathOperation.ById(new DocumentUuid(Guid.Parse(UrlId)))
        );
        return request;
    }

    [TestFixture("{}")]
    [TestFixture("{\"id\":null}")]
    [TestFixture("{\"id\":\"" + UrlId + "\"}")]
    [TestFixture("{\"id\":\"abcdabcd-abcd-abcd-abcd-abcdabcdabcd\"}")]
    [TestFixture("{\"id\":\"00000000-0000-0000-0000-000000000000\"}")]
    [TestFixture("{\"id\":\"{ABCDABCD-ABCD-ABCD-ABCD-ABCDABCDABCD}\"}")]
    [TestFixture("{\"id\":\"abcdabcdabcdabcdabcdabcdabcdabcd\"}")]
    public class Given_An_Acceptable_Body_Id(string body)
    {
        private RequestInfo _request = No.RequestInfo();
        private bool _continued;

        [SetUp]
        public async Task Setup()
        {
            _request = Context(body);
            _continued = false;
            await new NormalizePutIdMiddleware(NullLogger.Instance).Execute(
                _request,
                () =>
                {
                    _continued = true;
                    return Task.CompletedTask;
                }
            );
        }

        [Test]
        public void It_uses_the_url_id() => _request.ParsedBody["id"]!.GetValue<string>().Should().Be(UrlId);

        [Test]
        public void It_continues_the_pipeline() => _continued.Should().BeTrue();
    }

    [TestFixture("\"invalid-id\"")]
    [TestFixture("\"\"")]
    [TestFixture("\" \"")]
    [TestFixture("12345678901234567890123456789012")]
    [TestFixture("true")]
    [TestFixture("[]")]
    [TestFixture("{}")]
    public class Given_An_Invalid_Body_Id(string id)
    {
        private RequestInfo _request = No.RequestInfo();
        private bool _continued;

        [SetUp]
        public async Task Setup()
        {
            _request = Context("{\"id\":" + id + "}");
            _continued = false;
            await new NormalizePutIdMiddleware(NullLogger.Instance).Execute(
                _request,
                () =>
                {
                    _continued = true;
                    return Task.CompletedTask;
                }
            );
        }

        [Test]
        public void It_returns_bad_request() => _request.FrontendResponse.StatusCode.Should().Be(400);

        [Test]
        public void It_identifies_the_invalid_field() =>
            _request.FrontendResponse.Body!["validationErrors"]!["$.id"]![0]!
                .GetValue<string>()
                .Should()
                .Be("id must be a valid GUID string.");

        [Test]
        public void It_stops_the_pipeline() => _continued.Should().BeFalse();

        [Test]
        public void It_preserves_the_invalid_value() =>
            JsonNode.DeepEquals(_request.ParsedBody["id"], JsonNode.Parse(id)).Should().BeTrue();
    }

    [TestFixture("[]")]
    [TestFixture("123")]
    [TestFixture("true")]
    [TestFixture("\"text\"")]
    public class Given_A_Non_Object_Body(string body)
    {
        [Test]
        public async Task It_leaves_the_body_for_existing_validation()
        {
            RequestInfo request = Context(body);
            bool continued = false;
            await new NormalizePutIdMiddleware(NullLogger.Instance).Execute(
                request,
                () =>
                {
                    continued = true;
                    return Task.CompletedTask;
                }
            );
            continued.Should().BeTrue();
            JsonNode.DeepEquals(request.ParsedBody, JsonNode.Parse(body)).Should().BeTrue();
        }
    }
}
