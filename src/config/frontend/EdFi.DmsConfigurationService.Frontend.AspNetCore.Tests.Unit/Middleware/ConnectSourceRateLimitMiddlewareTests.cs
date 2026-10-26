// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Configuration;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Middleware;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Middleware;

public class ConnectSourceRateLimitMiddlewareTests
{
    private const string RemoteIp = "198.51.100.7";
    private const string TokenRoute = "connect/token/{**contextPath}";

    private static ConnectRateLimiter CreateLimiter(int sourceLimit) =>
        new(
            Options.Create(
                new ConnectRateLimitSettings
                {
                    SourcePermitLimit = sourceLimit,
                    ClientPermitLimit = 100,
                    WindowSeconds = 60,
                }
            )
        );

    private static DefaultHttpContext CreateContext(IServiceProvider services, params object[] metadata)
    {
        var context = new DefaultHttpContext { TraceIdentifier = "trace-limit", RequestServices = services };
        context.Connection.RemoteIpAddress = IPAddress.Parse(RemoteIp);
        context.Response.Body = new MemoryStream();
        context.SetEndpoint(
            new RouteEndpoint(
                _ => Task.CompletedTask,
                RoutePatternFactory.Parse(TokenRoute),
                0,
                new EndpointMetadataCollection(metadata),
                "test"
            )
        );
        return context;
    }

    private static async Task<string> BodyOf(HttpContext context)
    {
        context.Response.Body.Seek(0, SeekOrigin.Begin);
        return await new StreamReader(context.Response.Body).ReadToEndAsync();
    }

    [TestFixture]
    public class Given_requests_to_an_endpoint_without_the_marker
    {
        private int _nextCalls;

        [SetUp]
        public async Task Setup()
        {
            _nextCalls = 0;
            using ServiceProvider services = new ServiceCollection().AddLogging().BuildServiceProvider();
            using ConnectRateLimiter limiter = CreateLimiter(sourceLimit: 1);
            var middleware = new ConnectSourceRateLimitMiddleware(_ =>
            {
                _nextCalls++;
                return Task.CompletedTask;
            });
            for (int i = 0; i < 3; i++)
            {
                await middleware.InvokeAsync(
                    CreateContext(services),
                    limiter,
                    new TestLogger<ConnectSourceRateLimitMiddleware>()
                );
            }
        }

        [Test]
        public void It_passes_every_request_through() => _nextCalls.Should().Be(3);
    }

    [TestFixture]
    public class Given_a_marked_ed_fi_route_over_the_source_limit
    {
        private int _nextCalls;
        private DefaultHttpContext _rejected = null!;
        private JsonObject _body = null!;
        private TestLogger<ConnectSourceRateLimitMiddleware> _logger = null!;

        [SetUp]
        public async Task Setup()
        {
            _nextCalls = 0;
            using ServiceProvider services = new ServiceCollection().AddLogging().BuildServiceProvider();
            using ConnectRateLimiter limiter = CreateLimiter(sourceLimit: 1);
            _logger = new TestLogger<ConnectSourceRateLimitMiddleware>();
            var middleware = new ConnectSourceRateLimitMiddleware(_ =>
            {
                _nextCalls++;
                return Task.CompletedTask;
            });
            await middleware.InvokeAsync(
                CreateContext(services, ConnectRateLimitMetadata.Instance),
                limiter,
                _logger
            );
            _rejected = CreateContext(services, ConnectRateLimitMetadata.Instance);
            await middleware.InvokeAsync(_rejected, limiter, _logger);
            _body = JsonNode.Parse(await BodyOf(_rejected))!.AsObject();
        }

        [Test]
        public void It_admits_the_first_request_only() => _nextCalls.Should().Be(1);

        [Test]
        public void It_answers_429() => _rejected.Response.StatusCode.Should().Be(429);

        [Test]
        public void It_uses_the_problem_details_content_type() =>
            _rejected.Response.ContentType.Should().Be("application/problem+json");

        [Test]
        public void It_writes_the_too_many_requests_type() =>
            _body["type"]!.GetValue<string>().Should().Be("urn:ed-fi:management-api:too-many-requests");

        [Test]
        public void It_uses_the_trace_identifier_as_correlation_id() =>
            _body["correlationId"]!.GetValue<string>().Should().Be("trace-limit");

        [Test]
        public void It_sets_a_whole_second_retry_after()
        {
            int seconds = int.Parse(
                _rejected.Response.Headers.RetryAfter.ToString(),
                CultureInfo.InvariantCulture
            );
            seconds.Should().BeInRange(1, 60);
        }

        [Test]
        public void It_logs_the_gate_and_route_only()
        {
            TestLogger<ConnectSourceRateLimitMiddleware>.LogEntry entry = _logger
                .Entries.Should()
                .ContainSingle()
                .Which;
            entry.Level.Should().Be(LogLevel.Debug);
            string logged = entry.State!.ToString()!;
            logged.Should().Contain("source").And.Contain(TokenRoute).And.NotContain(RemoteIp);
        }
    }

    [TestFixture]
    public class Given_a_marked_oauth_route_over_the_source_limit
    {
        private DefaultHttpContext _rejected = null!;
        private JsonObject _body = null!;

        [SetUp]
        public async Task Setup()
        {
            using ServiceProvider services = new ServiceCollection().AddLogging().BuildServiceProvider();
            using ConnectRateLimiter limiter = CreateLimiter(sourceLimit: 1);
            var middleware = new ConnectSourceRateLimitMiddleware(_ => Task.CompletedTask);
            var logger = new TestLogger<ConnectSourceRateLimitMiddleware>();
            await middleware.InvokeAsync(
                CreateContext(
                    services,
                    ConnectRateLimitMetadata.Instance,
                    OAuthErrorContractMetadata.Instance
                ),
                limiter,
                logger
            );
            _rejected = CreateContext(
                services,
                ConnectRateLimitMetadata.Instance,
                OAuthErrorContractMetadata.Instance
            );
            await middleware.InvokeAsync(_rejected, limiter, logger);
            _body = JsonNode.Parse(await BodyOf(_rejected))!.AsObject();
        }

        [Test]
        public void It_answers_429() => _rejected.Response.StatusCode.Should().Be(429);

        [Test]
        public void It_uses_the_problem_details_content_type() =>
            _rejected.Response.ContentType.Should().Be("application/problem+json");

        [Test]
        public void It_writes_ed_fi_problem_details_despite_the_oauth_route_metadata()
        {
            _body.Count.Should().Be(7);
            _body["type"]!.GetValue<string>().Should().Be("urn:ed-fi:management-api:too-many-requests");
            _body["status"]!.GetValue<int>().Should().Be(429);
            _body["correlationId"]!.GetValue<string>().Should().Be("trace-limit");
            _body["detail"]!
                .GetValue<string>()
                .Should()
                .Be("The number of allowed requests has been exceeded. Retry the request later.");
        }
    }
}
