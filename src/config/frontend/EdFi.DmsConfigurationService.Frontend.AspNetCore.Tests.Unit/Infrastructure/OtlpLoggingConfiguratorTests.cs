// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using NUnit.Framework;
using Serilog;
using Serilog.Sinks.OpenTelemetry;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Infrastructure;

/// <summary>
/// Unit tests for <see cref="LoggingConfigurator"/> and <see cref="OtlpLoggingOptions"/>, covering the
/// disabled-by-default posture, the "OtlpLogging" configuration section's key-by-key binding, the
/// <see cref="LoggingConfigurator.ApplyOtlpSink"/> bool seam, and - through a
/// <see cref="WebApplicationFactory{TEntryPoint}"/>-based fixture - resilience to an unreachable OTLP
/// collector.
/// </summary>
[TestFixture]
public class OtlpLoggingConfiguratorTests
{
    [Test]
    public void Binding_An_Empty_Configuration_Yields_Disabled_Defaults()
    {
        // Arrange
        var configuration = new ConfigurationBuilder().Build();

        // Act
        var options = LoggingConfigurator.BindOtlpLoggingOptions(configuration);

        // Assert
        options.Enabled.Should().BeFalse("operators must opt in to OTLP export");
        options.ServiceName.Should().Be("EdFi.DmsConfigurationService");
        options.Protocol.Should().Be(OtlpProtocol.HttpProtobuf);
        options.ServiceVersion.Should().NotBeNullOrWhiteSpace();
        options.Endpoint.Should().BeNull();
        options.DeploymentEnvironment.Should().BeNull();
        options.ServiceInstanceId.Should().BeNull();
    }

    [Test]
    public void ApplyOtlpSink_Returns_False_And_Adds_No_Sink_When_Disabled()
    {
        // Arrange
        var configuration = new ConfigurationBuilder().Build();
        var options = LoggingConfigurator.BindOtlpLoggingOptions(configuration);
        var loggerConfiguration = new LoggerConfiguration();

        // Act
        var sinkApplied = LoggingConfigurator.ApplyOtlpSink(loggerConfiguration, options);

        // Assert
        sinkApplied.Should().BeFalse();
    }

    [Test]
    public void ConfigureLogging_Returns_A_Working_Logger_For_An_Empty_Configuration()
    {
        // Arrange
        var configuration = new ConfigurationBuilder().Build();
        var logger = LoggingConfigurator.ConfigureLogging(configuration);

        try
        {
            // Act
            Action act = () => logger.Information("Disabled-OTLP smoke test message");

            // Assert
            act.Should().NotThrow("a disabled OTLP configuration must still yield a usable logger");
        }
        finally
        {
            (logger as IDisposable)?.Dispose();
        }
    }

    [Test]
    public void ApplyOtlpSink_Returns_True_When_Enabled_Through_Configuration()
    {
        // Arrange
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["OtlpLogging:Enabled"] = "true",
                    ["OtlpLogging:Endpoint"] = "http://otel-collector:4318",
                }
            )
            .Build();
        var options = LoggingConfigurator.BindOtlpLoggingOptions(configuration);
        var loggerConfiguration = new LoggerConfiguration();

        // Act
        // This observes the sink being added through the bool return seam only - it never inspects
        // Serilog's internal sink list, which ApplyOtlpSink's doc comment calls out as the point of
        // returning a bool in the first place.
        var sinkApplied = LoggingConfigurator.ApplyOtlpSink(loggerConfiguration, options);

        // Assert
        sinkApplied.Should().BeTrue();
    }

    [Test]
    public void ApplyOtlpSink_Returns_False_When_Enabled_Without_An_Endpoint()
    {
        // Arrange
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["OtlpLogging:Enabled"] = "true" })
            .Build();
        var options = LoggingConfigurator.BindOtlpLoggingOptions(configuration);
        var loggerConfiguration = new LoggerConfiguration();

        // Act
        // Without an Endpoint the sink's built-in gRPC-convention default endpoint would silently
        // mismatch the HttpProtobuf protocol default, so the configurator refuses to apply the sink.
        var sinkApplied = LoggingConfigurator.ApplyOtlpSink(loggerConfiguration, options);

        // Assert
        sinkApplied.Should().BeFalse();
    }

    [Test]
    public void ApplyOtlpSink_Returns_False_When_Endpoint_Is_Whitespace()
    {
        // Arrange
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["OtlpLogging:Enabled"] = "true",
                    ["OtlpLogging:Endpoint"] = "   ",
                }
            )
            .Build();
        var options = LoggingConfigurator.BindOtlpLoggingOptions(configuration);
        var loggerConfiguration = new LoggerConfiguration();

        // Act
        var sinkApplied = LoggingConfigurator.ApplyOtlpSink(loggerConfiguration, options);

        // Assert
        sinkApplied
            .Should()
            .BeFalse("a whitespace-only endpoint would otherwise install a silently inert sink");
    }

    [TestCase("Grpc", OtlpProtocol.Grpc)]
    [TestCase("HttpProtobuf", OtlpProtocol.HttpProtobuf)]
    public void Binding_Maps_Every_OtlpLogging_Key_From_Configuration(
        string protocolConfigurationValue,
        OtlpProtocol expectedProtocol
    )
    {
        // Arrange
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["OtlpLogging:Enabled"] = "true",
                    ["OtlpLogging:Endpoint"] = "http://otel-collector:4317",
                    ["OtlpLogging:Protocol"] = protocolConfigurationValue,
                    ["OtlpLogging:ServiceName"] = "test-service",
                    ["OtlpLogging:ServiceVersion"] = "9.9.9",
                    ["OtlpLogging:DeploymentEnvironment"] = "production",
                    ["OtlpLogging:ServiceInstanceId"] = "instance-42",
                }
            )
            .Build();

        // Act
        var options = LoggingConfigurator.BindOtlpLoggingOptions(configuration);

        // Assert
        options.Enabled.Should().BeTrue();
        options.Endpoint.Should().Be("http://otel-collector:4317");
        options.Protocol.Should().Be(expectedProtocol);
        options.ServiceName.Should().Be("test-service");
        options.ServiceVersion.Should().Be("9.9.9");
        options.DeploymentEnvironment.Should().Be("production");
        options.ServiceInstanceId.Should().Be("instance-42");
    }

    [Test]
    public void ToResourceAttributes_Includes_Deployment_And_Instance_Attributes_When_Set()
    {
        // Arrange
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["OtlpLogging:ServiceName"] = "test-service",
                    ["OtlpLogging:ServiceVersion"] = "9.9.9",
                    ["OtlpLogging:DeploymentEnvironment"] = "production",
                    ["OtlpLogging:ServiceInstanceId"] = "instance-42",
                }
            )
            .Build();
        var options = LoggingConfigurator.BindOtlpLoggingOptions(configuration);

        // Act
        var attributes = options.ToResourceAttributes();

        // Assert
        attributes
            .Should()
            .BeEquivalentTo(
                new Dictionary<string, object>
                {
                    ["service.name"] = "test-service",
                    ["service.version"] = "9.9.9",
                    ["deployment.environment"] = "production",
                    ["deployment.environment.name"] = "production",
                    ["service.instance.id"] = "instance-42",
                }
            );
    }

    [Test]
    public void ToResourceAttributes_Omits_Deployment_And_Instance_Attributes_When_Unset()
    {
        // Arrange
        var configuration = new ConfigurationBuilder().Build();
        var options = LoggingConfigurator.BindOtlpLoggingOptions(configuration);

        // Act
        var attributes = options.ToResourceAttributes();

        // Assert
        attributes.Keys.Should().BeEquivalentTo("service.name", "service.version");
        attributes["service.name"].Should().Be("EdFi.DmsConfigurationService");
        attributes["service.version"].Should().Be(options.ServiceVersion);
    }
}

/// <summary>
/// Resilience proof: an OTLP collector that cannot be reached must not block application
/// startup or request handling. <see cref="LoggingConfigurator.ConfigureLogging"/> runs inside
/// <c>AddServices()</c>, ahead of <c>WebApplicationBuilder.Build()</c>, so - exactly like
/// AppSettings:ReverseProxy in <c>ForwardedHeadersTests</c>' <c>Given_A_Reverse_Proxy_Configuration</c> -
/// the OtlpLogging section must be supplied via environment variables (visible to CreateBuilder) rather
/// than ConfigureAppConfiguration (applied later, at build time) for the override to actually reach the
/// real Serilog pipeline.
/// </summary>
[TestFixture]
[NonParallelizable]
public class Given_an_unreachable_otlp_collector_endpoint
{
    private const string OtlpEnabledEnv = "OtlpLogging__Enabled";
    private const string OtlpEndpointEnv = "OtlpLogging__Endpoint";

    [TearDown]
    public void TearDown()
    {
        Serilog.Debugging.SelfLog.Disable();
        Environment.SetEnvironmentVariable(OtlpEnabledEnv, null);
        Environment.SetEnvironmentVariable(OtlpEndpointEnv, null);
    }

    [Test]
    public async Task It_starts_the_application_and_serves_a_request_despite_the_delivery_failure()
    {
        // Arrange
        // Reserve and immediately release a loopback port so the endpoint is genuinely closed,
        // instead of hoping a hard-coded port is unoccupied.
        var portReservation = new TcpListener(IPAddress.Loopback, 0);
        portReservation.Start();
        int closedPort = ((IPEndPoint)portReservation.LocalEndpoint).Port;
        portReservation.Stop();

        Environment.SetEnvironmentVariable(OtlpEnabledEnv, "true");
        Environment.SetEnvironmentVariable(OtlpEndpointEnv, $"http://127.0.0.1:{closedPort}");

        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
        });
        using var client = factory.CreateClient();

        // ApplyOtlpSink enabled SelfLog to stderr at boot; redirect it to a capture buffer so the
        // export attempt is observable. This also proves the OTLP sink was actually installed -
        // without it the test would pass trivially if the configuration never reached the logger.
        var selfLogLines = new ConcurrentQueue<string>();
        Serilog.Debugging.SelfLog.Enable(message => selfLogLines.Enqueue(message));

        // Act
        var response = await client.GetAsync("/openapi/v1.json");

        // Assert
        response
            .StatusCode.Should()
            .Be(
                HttpStatusCode.OK,
                "an unreachable OTLP collector must not block application startup or request handling"
            );

        for (int i = 0; i < 30 && !selfLogLines.Any(l => l.Contains("failed emitting a batch")); i++)
        {
            await client.GetAsync("/openapi/v1.json");
            await Task.Delay(500);
        }

        selfLogLines
            .Should()
            .Contain(
                line => line.Contains("failed emitting a batch"),
                "the export attempt against the closed port must be observable through SelfLog"
            );
    }
}
