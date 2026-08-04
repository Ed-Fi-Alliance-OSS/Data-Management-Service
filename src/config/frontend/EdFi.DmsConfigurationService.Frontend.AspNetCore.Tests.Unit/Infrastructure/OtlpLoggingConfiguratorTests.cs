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
        options.Headers.Should().BeEmpty();
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

    // "collector:4317" parses as an absolute URI whose scheme is "collector", exercising the
    // scheme check; "not a valid uri" fails URI parsing outright. Without the endpoint validation
    // the gRPC exporter throws during sink construction for the first shape while HttpProtobuf
    // silently accepts both, so every combination must land on the same warn-and-skip path.
    [TestCase("collector:4317", "Grpc")]
    [TestCase("collector:4317", "HttpProtobuf")]
    [TestCase("not a valid uri", "Grpc")]
    [TestCase("not a valid uri", "HttpProtobuf")]
    public void ApplyOtlpSink_Returns_False_For_A_Malformed_Endpoint_Without_Throwing(
        string endpoint,
        string protocol
    )
    {
        // Arrange
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["OtlpLogging:Enabled"] = "true",
                    ["OtlpLogging:Endpoint"] = endpoint,
                    ["OtlpLogging:Protocol"] = protocol,
                }
            )
            .Build();
        var options = LoggingConfigurator.BindOtlpLoggingOptions(configuration);
        var loggerConfiguration = new LoggerConfiguration();

        // Act
        var sinkApplied = LoggingConfigurator.ApplyOtlpSink(loggerConfiguration, options);

        // Assert
        sinkApplied.Should().BeFalse();
    }

    // Binding fails even when the section is otherwise disabled: a Protocol typo takes the
    // application down at startup instead of leaving export silently misconfigured. This pins
    // the fail-fast behavior documented in docs/CONFIGURATION.md.
    [TestCase("true")]
    [TestCase("false")]
    public void Binding_Throws_For_An_Unparseable_Protocol_Regardless_Of_The_Enabled_Flag(string enabled)
    {
        // Arrange
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["OtlpLogging:Enabled"] = enabled,
                    ["OtlpLogging:Protocol"] = "http/protobuf",
                }
            )
            .Build();

        // Act
        Action act = () => LoggingConfigurator.BindOtlpLoggingOptions(configuration);

        // Assert
        act.Should().Throw<InvalidOperationException>();
    }

    [Test]
    public void ApplyOtlpSink_Constructs_The_Grpc_Exporter_When_Protocol_Is_Grpc()
    {
        // Arrange
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["OtlpLogging:Enabled"] = "true",
                    ["OtlpLogging:Endpoint"] = "http://otel-collector:4317",
                    ["OtlpLogging:Protocol"] = "Grpc",
                }
            )
            .Build();
        var options = LoggingConfigurator.BindOtlpLoggingOptions(configuration);
        var loggerConfiguration = new LoggerConfiguration();

        // Act
        // Exporter construction happens inside the WriteTo call, so this covers the gRPC
        // channel + bounded-handler construction path that the HttpProtobuf tests never reach.
        var sinkApplied = LoggingConfigurator.ApplyOtlpSink(loggerConfiguration, options);

        // Assert
        sinkApplied.Should().BeTrue();
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
                    ["OtlpLogging:Headers:Authorization"] = "Bearer test-token",
                    ["OtlpLogging:Headers:X-Api-Key"] = "k-123",
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
        options
            .Headers.Should()
            .BeEquivalentTo(
                new Dictionary<string, string>
                {
                    ["Authorization"] = "Bearer test-token",
                    ["X-Api-Key"] = "k-123",
                }
            );
    }

    private sealed class NeverCompletingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HttpResponseMessage();
        }
    }

    private sealed class ImmediateOkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
    }

    [Test]
    public async Task BoundedExportTimeoutHandler_Cancels_A_Request_That_Exceeds_The_Timeout()
    {
        // Arrange
        using var handler = new BoundedExportTimeoutHandler(
            TimeSpan.FromMilliseconds(100),
            new NeverCompletingHandler()
        );
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };

        // Act
        var act = async () => await client.GetAsync("http://127.0.0.1:9/never-reached");

        // Assert
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Test]
    public async Task BoundedExportTimeoutHandler_Passes_Through_A_Request_That_Completes_In_Time()
    {
        // Arrange
        using var handler = new BoundedExportTimeoutHandler(
            TimeSpan.FromSeconds(5),
            new ImmediateOkHandler()
        );
        using var client = new HttpClient(handler);

        // Act
        var response = await client.GetAsync("http://127.0.0.1:9/never-reached");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
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
    private const string OtelEndpointEnv = "OTEL_EXPORTER_OTLP_ENDPOINT";

    [TearDown]
    public void TearDown()
    {
        Serilog.Debugging.SelfLog.Disable();
        Environment.SetEnvironmentVariable(OtlpEnabledEnv, null);
        Environment.SetEnvironmentVariable(OtlpEndpointEnv, null);
        Environment.SetEnvironmentVariable(OtelEndpointEnv, null);
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

        // Decoy listener at the address of the standard OTEL env var: the exporter is configured
        // with ignoreEnvironment: true, so this listener must never receive a connection.
        var decoy = new TcpListener(IPAddress.Loopback, 0);
        decoy.Start();
        int decoyPort = ((IPEndPoint)decoy.LocalEndpoint).Port;
        var decoyConnections = new ConcurrentQueue<TcpClient>();
        _ = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    decoyConnections.Enqueue(await decoy.AcceptTcpClientAsync());
                }
            }
            catch
            {
                // Listener stopped at the end of the test.
            }
        });
        Environment.SetEnvironmentVariable(OtelEndpointEnv, $"http://127.0.0.1:{decoyPort}");

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

        // The failed export above proves an attempt happened; had OTEL_EXPORTER_OTLP_ENDPOINT
        // been honored, that attempt would have connected to the decoy instead.
        decoyConnections.Should().BeEmpty("OTEL_EXPORTER_OTLP_ENDPOINT must be ignored by the exporter");
        decoy.Stop();
    }
}

[TestFixture]
[NonParallelizable]
public class Given_raw_serilog_configuration_naming_the_otlp_sink
{
    [TearDown]
    public void TearDown()
    {
        Serilog.Debugging.SelfLog.Disable();
    }

    private static IConfigurationRoot BuildRawOtlpSinkConfiguration() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Serilog:Using:0"] = "Serilog.Sinks.OpenTelemetry",
                    ["Serilog:WriteTo:0:Name"] = "OpenTelemetry",
                    ["Serilog:WriteTo:0:Args:endpoint"] = "http://127.0.0.1:59999",
                }
            )
            .Build();

    [Test]
    public void ConfigureLogging_Ignores_The_Sink_Through_Pinned_Discovery()
    {
        // Arrange
        var configuration = BuildRawOtlpSinkConfiguration();
        var selfLogLines = new ConcurrentQueue<string>();
        Serilog.Debugging.SelfLog.Enable(message => selfLogLines.Enqueue(message));

        // Act
        // Discovery is pinned to the Console and File sink assemblies, so the raw WriteTo entry
        // must not activate the compiled-in OTLP sink. The SelfLog capture pins the skip
        // mechanism; the operator-visible report is the stderr warning covered below.
        var logger = LoggingConfigurator.ConfigureLogging(configuration);

        // Assert
        try
        {
            selfLogLines
                .Should()
                .Contain(
                    line => line.Contains("Unable to find a method called OpenTelemetry"),
                    "the pinned configuration reader must skip the OTLP sink method"
                );
        }
        finally
        {
            (logger as IDisposable)?.Dispose();
        }
    }

    [Test]
    public void ConfigureLogging_Warns_On_Stderr_Without_SelfLog_Enabled()
    {
        // Arrange
        var configuration = BuildRawOtlpSinkConfiguration();

        // In production nothing enables SelfLog before the configuration is read, so the reader's
        // own skip notice is dropped. The warning must reach stderr without any SelfLog listener.
        var originalError = Console.Error;
        var capturedError = new StringWriter();
        Console.SetError(capturedError);

        // Act
        try
        {
            var logger = LoggingConfigurator.ConfigureLogging(configuration);
            (logger as IDisposable)?.Dispose();
        }
        finally
        {
            Console.SetError(originalError);
        }

        // Assert
        capturedError
            .ToString()
            .Should()
            .Contain(
                "Serilog:WriteTo names the OpenTelemetry sink",
                "the ignored raw sink entry must be reported where an operator can see it"
            );
    }
}
