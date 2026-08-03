// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using EdFi.DataManagementService.Frontend.AspNetCore.Infrastructure;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using NUnit.Framework;
using Serilog;
using Serilog.Sinks.OpenTelemetry;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit.Infrastructure;

[TestFixture]
[Parallelizable]
public class Given_No_OtlpLogging_Configuration
{
    private OtlpLoggingOptions _options = null!;

    [SetUp]
    public void Setup()
    {
        IConfigurationRoot configuration = new ConfigurationBuilder().Build();
        _options = LoggingConfigurator.BindOtlpLoggingOptions(configuration);
    }

    [Test]
    public void It_binds_disabled_with_default_values()
    {
        _options.Enabled.Should().BeFalse();
        _options.Endpoint.Should().BeNull();
        _options.Protocol.Should().Be(OtlpProtocol.HttpProtobuf);
        _options.ServiceName.Should().Be("EdFi.DataManagementService");
        _options.ServiceVersion.Should().NotBeNullOrEmpty();
        _options.DeploymentEnvironment.Should().BeNull();
        _options.ServiceInstanceId.Should().BeNull();
        _options.Headers.Should().BeEmpty();
    }

    [Test]
    public void It_does_not_apply_the_otlp_sink()
    {
        var loggerConfiguration = new LoggerConfiguration();

        var sinkApplied = LoggingConfigurator.ApplyOtlpSink(loggerConfiguration, _options);

        sinkApplied.Should().BeFalse();
    }

    [Test]
    public void It_configures_a_working_logger_from_an_empty_configuration()
    {
        IConfigurationRoot configuration = new ConfigurationBuilder().Build();

        var logger = LoggingConfigurator.ConfigureLogging(configuration);
        try
        {
            Action act = () => logger.Information("OTLP logging disabled smoke test");

            act.Should().NotThrow();
        }
        finally
        {
            (logger as IDisposable)?.Dispose();
        }
    }
}

[TestFixture]
[Parallelizable]
public class Given_OtlpLogging_Enabled_Via_Configuration
{
    [Test]
    public void It_applies_the_otlp_sink()
    {
        IConfigurationRoot configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["OtlpLogging:Enabled"] = "true",
                    ["OtlpLogging:Endpoint"] = "http://127.0.0.1:59999",
                }
            )
            .Build();
        var options = LoggingConfigurator.BindOtlpLoggingOptions(configuration);
        var loggerConfiguration = new LoggerConfiguration();

        var sinkApplied = LoggingConfigurator.ApplyOtlpSink(loggerConfiguration, options);

        sinkApplied.Should().BeTrue();
    }
}

[TestFixture]
[Parallelizable]
public class Given_OtlpLogging_Enabled_Without_An_Endpoint
{
    [Test]
    public void It_does_not_apply_the_otlp_sink()
    {
        IConfigurationRoot configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["OtlpLogging:Enabled"] = "true" })
            .Build();
        var options = LoggingConfigurator.BindOtlpLoggingOptions(configuration);
        var loggerConfiguration = new LoggerConfiguration();

        // Without an Endpoint the sink's built-in gRPC-convention default endpoint would silently
        // mismatch the HttpProtobuf protocol default, so the configurator refuses to apply the sink.
        var sinkApplied = LoggingConfigurator.ApplyOtlpSink(loggerConfiguration, options);

        sinkApplied.Should().BeFalse();
    }

    [Test]
    public void It_does_not_apply_the_otlp_sink_for_a_whitespace_endpoint()
    {
        IConfigurationRoot configuration = new ConfigurationBuilder()
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

        // A whitespace-only endpoint would otherwise be normalized away by the sink, silently
        // installing an inert exporter instead of surfacing the misconfiguration warning.
        var sinkApplied = LoggingConfigurator.ApplyOtlpSink(loggerConfiguration, options);

        sinkApplied.Should().BeFalse();
    }
}

[TestFixture]
[Parallelizable]
public class Given_OtlpLogging_Keys_Are_Fully_Configured
{
    private static IConfigurationRoot BuildConfiguration(string protocol) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["OtlpLogging:Enabled"] = "true",
                    ["OtlpLogging:Endpoint"] = "http://otel-collector:4317",
                    ["OtlpLogging:Protocol"] = protocol,
                    ["OtlpLogging:ServiceName"] = "custom-service",
                    ["OtlpLogging:ServiceVersion"] = "9.9.9",
                    ["OtlpLogging:DeploymentEnvironment"] = "production",
                    ["OtlpLogging:ServiceInstanceId"] = "instance-42",
                    ["OtlpLogging:Headers:Authorization"] = "Bearer test-token",
                    ["OtlpLogging:Headers:X-Api-Key"] = "k-123",
                }
            )
            .Build();

    [Test]
    public void It_binds_every_key_with_the_grpc_protocol()
    {
        var options = LoggingConfigurator.BindOtlpLoggingOptions(BuildConfiguration("Grpc"));

        options.Enabled.Should().BeTrue();
        options.Endpoint.Should().Be("http://otel-collector:4317");
        options.Protocol.Should().Be(OtlpProtocol.Grpc);
        options.ServiceName.Should().Be("custom-service");
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

    [Test]
    public void It_binds_every_key_with_the_http_protobuf_protocol()
    {
        var options = LoggingConfigurator.BindOtlpLoggingOptions(BuildConfiguration("HttpProtobuf"));

        options.Enabled.Should().BeTrue();
        options.Endpoint.Should().Be("http://otel-collector:4317");
        options.Protocol.Should().Be(OtlpProtocol.HttpProtobuf);
        options.ServiceName.Should().Be("custom-service");
        options.ServiceVersion.Should().Be("9.9.9");
        options.DeploymentEnvironment.Should().Be("production");
        options.ServiceInstanceId.Should().Be("instance-42");
    }

    [Test]
    public void It_includes_every_configured_resource_attribute()
    {
        var options = LoggingConfigurator.BindOtlpLoggingOptions(BuildConfiguration("Grpc"));

        var resourceAttributes = options.ToResourceAttributes();

        resourceAttributes
            .Should()
            .BeEquivalentTo(
                new Dictionary<string, object>
                {
                    ["service.name"] = "custom-service",
                    ["service.version"] = "9.9.9",
                    ["deployment.environment"] = "production",
                    ["deployment.environment.name"] = "production",
                    ["service.instance.id"] = "instance-42",
                }
            );
    }
}

[TestFixture]
[Parallelizable]
public class Given_A_Bounded_Export_Timeout_Handler
{
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
    public async Task It_cancels_a_request_that_exceeds_the_timeout()
    {
        using var handler = new BoundedExportTimeoutHandler(
            TimeSpan.FromMilliseconds(100),
            new NeverCompletingHandler()
        );
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };

        var act = async () => await client.GetAsync("http://127.0.0.1:9/never-reached");

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Test]
    public async Task It_passes_through_a_request_that_completes_in_time()
    {
        using var handler = new BoundedExportTimeoutHandler(
            TimeSpan.FromSeconds(5),
            new ImmediateOkHandler()
        );
        using var client = new HttpClient(handler);

        var response = await client.GetAsync("http://127.0.0.1:9/never-reached");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}

[TestFixture]
[Parallelizable]
public class Given_OtlpLogging_Optional_Keys_Are_Unset
{
    [Test]
    public void It_omits_deployment_environment_and_service_instance_id_from_resource_attributes()
    {
        IConfigurationRoot configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["OtlpLogging:Enabled"] = "true",
                    ["OtlpLogging:Endpoint"] = "http://otel-collector:4317",
                }
            )
            .Build();
        var options = LoggingConfigurator.BindOtlpLoggingOptions(configuration);

        var resourceAttributes = options.ToResourceAttributes();

        resourceAttributes
            .Should()
            .BeEquivalentTo(
                new Dictionary<string, object>
                {
                    ["service.name"] = "EdFi.DataManagementService",
                    ["service.version"] = options.ServiceVersion,
                }
            );
        resourceAttributes.Should().NotContainKey("deployment.environment");
        resourceAttributes.Should().NotContainKey("deployment.environment.name");
        resourceAttributes.Should().NotContainKey("service.instance.id");
    }
}

/// <summary>
/// The OtlpLogging:Enabled/Endpoint keys are read eagerly by <c>LoggingConfigurator.ConfigureLogging</c>
/// during <c>WebApplicationBuilderExtensions.AddServices</c>, which runs before <c>builder.Build()</c>.
/// Overrides added through <c>ConfigureAppConfiguration</c> are only applied at build time (see the
/// precedent in <see cref="ConfigurationTests"/> and <see cref="Given_A_Reverse_Proxy_Configuration"/>),
/// so this fixture supplies the override through process environment variables, which are visible from
/// <c>WebApplication.CreateBuilder(args)</c> onward.
/// </summary>
[TestFixture]
[NonParallelizable]
public class Given_An_Unreachable_Otlp_Export_Target
{
    private const string EnabledEnv = "OtlpLogging__Enabled";
    private const string EndpointEnv = "OtlpLogging__Endpoint";

    [TearDown]
    public void TearDown()
    {
        Serilog.Debugging.SelfLog.Disable();
        Environment.SetEnvironmentVariable(EnabledEnv, null);
        Environment.SetEnvironmentVariable(EndpointEnv, null);
    }

    [Test]
    public async Task It_starts_and_serves_requests_without_being_blocked_by_export_failures()
    {
        // Reserve and immediately release a loopback port so the endpoint is genuinely closed,
        // instead of hoping a hard-coded port is unoccupied.
        var portReservation = new TcpListener(IPAddress.Loopback, 0);
        portReservation.Start();
        int closedPort = ((IPEndPoint)portReservation.LocalEndpoint).Port;
        portReservation.Stop();

        Environment.SetEnvironmentVariable(EnabledEnv, "true");
        Environment.SetEnvironmentVariable(EndpointEnv, $"http://127.0.0.1:{closedPort}");

        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureServices(
                (collection) =>
                {
                    TestMockHelper.AddEssentialMocks(collection);
                }
            );
        });
        using var client = factory.CreateClient();

        // ApplyOtlpSink enabled SelfLog to stderr at boot; redirect it to a capture buffer so the
        // export attempt is observable. This also proves the OTLP sink was actually installed -
        // without it the test would pass trivially if the configuration never reached the logger.
        var selfLogLines = new ConcurrentQueue<string>();
        Serilog.Debugging.SelfLog.Enable(message => selfLogLines.Enqueue(message));

        var response = await client.GetAsync("/health");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        for (int i = 0; i < 30 && !selfLogLines.Any(l => l.Contains("failed emitting a batch")); i++)
        {
            await client.GetAsync("/health");
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
