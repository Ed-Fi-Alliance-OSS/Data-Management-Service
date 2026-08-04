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
public class Given_OtlpLogging_With_A_Malformed_Endpoint
{
    // "collector:4317" parses as an absolute URI whose scheme is "collector", exercising the
    // scheme check; "not a valid uri" fails URI parsing outright. Without the endpoint validation
    // the gRPC exporter throws during sink construction for the first shape while HttpProtobuf
    // silently accepts both, so every combination must land on the same warn-and-skip path.
    [TestCase("collector:4317", "Grpc")]
    [TestCase("collector:4317", "HttpProtobuf")]
    [TestCase("not a valid uri", "Grpc")]
    [TestCase("not a valid uri", "HttpProtobuf")]
    public void It_does_not_apply_the_otlp_sink_and_does_not_throw(string endpoint, string protocol)
    {
        IConfigurationRoot configuration = new ConfigurationBuilder()
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

        var sinkApplied = LoggingConfigurator.ApplyOtlpSink(loggerConfiguration, options);

        sinkApplied.Should().BeFalse();
    }
}

[TestFixture]
[Parallelizable]
public class Given_OtlpLogging_With_An_Invalid_Header
{
    // The transport libraries validate headers during sink construction with protocol-specific
    // rules: HttpProtobuf rejects a value with a trailing newline (the shape a mounted secret
    // file produces) and a content header name, while Grpc rejects key characters HTTP accepts.
    // Every shape must land on the warn-and-skip path instead of throwing out of ApplyOtlpSink.
    [TestCase("Authorization", "Bearer abc\n", "HttpProtobuf")]
    [TestCase("Content-Type", "application/json", "HttpProtobuf")]
    [TestCase("X-Api!Key", "v", "Grpc")]
    public void It_does_not_apply_the_otlp_sink_and_does_not_throw(
        string headerName,
        string headerValue,
        string protocol
    )
    {
        IConfigurationRoot configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["OtlpLogging:Enabled"] = "true",
                    ["OtlpLogging:Endpoint"] = "http://otel-collector:4317",
                    ["OtlpLogging:Protocol"] = protocol,
                    [$"OtlpLogging:Headers:{headerName}"] = headerValue,
                }
            )
            .Build();
        var options = LoggingConfigurator.BindOtlpLoggingOptions(configuration);
        var loggerConfiguration = new LoggerConfiguration();

        var sinkApplied = LoggingConfigurator.ApplyOtlpSink(loggerConfiguration, options);

        sinkApplied.Should().BeFalse();
    }
}

[TestFixture]
[NonParallelizable]
public class Given_OtlpLogging_With_An_Invalid_Header_Value_Containing_A_Secret
{
    [Test]
    public void It_reports_the_failure_without_echoing_the_header_value()
    {
        IConfigurationRoot configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["OtlpLogging:Enabled"] = "true",
                    ["OtlpLogging:Endpoint"] = "http://otel-collector:4318",
                    ["OtlpLogging:Protocol"] = "HttpProtobuf",
                    ["OtlpLogging:Headers:Authorization"] = "Bearer SECRET-SENTINEL-VALUE\n",
                }
            )
            .Build();
        var options = LoggingConfigurator.BindOtlpLoggingOptions(configuration);
        var loggerConfiguration = new LoggerConfiguration();

        // The exporter's exception message embeds the offending header value, and stderr is part
        // of the collector contract, so the warning must not echo the exception message.
        var originalError = Console.Error;
        var capturedError = new StringWriter();
        Console.SetError(capturedError);
        bool sinkApplied;
        try
        {
            sinkApplied = LoggingConfigurator.ApplyOtlpSink(loggerConfiguration, options);
        }
        finally
        {
            Console.SetError(originalError);
        }

        sinkApplied.Should().BeFalse();
        capturedError.ToString().Should().Contain("OtlpLogging sink construction failed");
        capturedError
            .ToString()
            .Should()
            .NotContain("SECRET-SENTINEL-VALUE", "the failure report must not leak header secret material");
    }
}

[TestFixture]
[Parallelizable]
public class Given_An_Otlp_Protocol_Value_The_Binder_Cannot_Parse
{
    // Binding fails even when the section is otherwise disabled: a Protocol typo takes the
    // application down at startup instead of leaving export silently misconfigured. This pins
    // the fail-fast behavior documented in docs/CONFIGURATION.md.
    [TestCase("true")]
    [TestCase("false")]
    public void It_fails_binding_regardless_of_the_enabled_flag(string enabled)
    {
        IConfigurationRoot configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["OtlpLogging:Enabled"] = enabled,
                    ["OtlpLogging:Protocol"] = "http/protobuf",
                }
            )
            .Build();

        Action act = () => LoggingConfigurator.BindOtlpLoggingOptions(configuration);

        act.Should().Throw<InvalidOperationException>();
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
    public void It_applies_the_otlp_sink_with_the_grpc_protocol()
    {
        var options = LoggingConfigurator.BindOtlpLoggingOptions(BuildConfiguration("Grpc"));
        var loggerConfiguration = new LoggerConfiguration();

        // Exporter construction happens inside the WriteTo call, so this covers the gRPC
        // channel + bounded-handler construction path that the HttpProtobuf tests never reach.
        var sinkApplied = LoggingConfigurator.ApplyOtlpSink(loggerConfiguration, options);

        sinkApplied.Should().BeTrue();
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
    private const string OtelEndpointEnv = "OTEL_EXPORTER_OTLP_ENDPOINT";

    [TearDown]
    public void TearDown()
    {
        Serilog.Debugging.SelfLog.Disable();
        Environment.SetEnvironmentVariable(EnabledEnv, null);
        Environment.SetEnvironmentVariable(EndpointEnv, null);
        Environment.SetEnvironmentVariable(OtelEndpointEnv, null);
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

        // The failed export above proves an attempt happened; had OTEL_EXPORTER_OTLP_ENDPOINT
        // been honored, that attempt would have connected to the decoy instead.
        decoyConnections.Should().BeEmpty("OTEL_EXPORTER_OTLP_ENDPOINT must be ignored by the exporter");
        decoy.Stop();
    }
}

[TestFixture]
[NonParallelizable]
public class Given_Raw_Serilog_Configuration_Naming_The_Otlp_Sink
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
    public void It_skips_the_sink_through_pinned_discovery()
    {
        IConfigurationRoot configuration = BuildRawOtlpSinkConfiguration();
        var selfLogLines = new ConcurrentQueue<string>();
        Serilog.Debugging.SelfLog.Enable(message => selfLogLines.Enqueue(message));

        // Discovery is pinned to the Console and File sink assemblies, so the raw WriteTo entry
        // must not activate the compiled-in OTLP sink. The SelfLog capture pins the skip
        // mechanism; the operator-visible report is the stderr warning covered below.
        var logger = LoggingConfigurator.ConfigureLogging(configuration);
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
    public void It_warns_on_stderr_without_selflog_enabled()
    {
        IConfigurationRoot configuration = BuildRawOtlpSinkConfiguration();

        // In production nothing enables SelfLog before the configuration is read, so the reader's
        // own skip notice is dropped. The warning must reach stderr without any SelfLog listener.
        var originalError = Console.Error;
        var capturedError = new StringWriter();
        Console.SetError(capturedError);
        try
        {
            var logger = LoggingConfigurator.ConfigureLogging(configuration);
            (logger as IDisposable)?.Dispose();
        }
        finally
        {
            Console.SetError(originalError);
        }

        capturedError
            .ToString()
            .Should()
            .Contain(
                "Serilog:WriteTo names the OpenTelemetry sink",
                "the ignored raw sink entry must be reported where an operator can see it"
            );
    }
}
