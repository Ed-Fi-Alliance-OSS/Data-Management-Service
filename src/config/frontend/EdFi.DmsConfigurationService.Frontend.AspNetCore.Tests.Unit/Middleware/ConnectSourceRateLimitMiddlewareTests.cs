// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
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
    public class Given_a_marked_route_over_the_source_limit
    {
        private int _nextCalls;
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
            await middleware.InvokeAsync(
                CreateContext(services, ConnectRateLimitMetadata.Instance),
                limiter,
                _logger
            );
        }

        [Test]
        public void It_admits_the_first_request_only() => _nextCalls.Should().Be(1);

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
}
