// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Microsoft.Extensions.Configuration;
using Serilog;
using Serilog.Settings.Configuration;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;

/// <summary>
/// Builds the Serilog logger used by the application, including optional export of log events
/// to an OTLP (OpenTelemetry Protocol) endpoint.
/// </summary>
public static class LoggingConfigurator
{
    /// <summary>
    /// Binds the "OtlpLogging" configuration section to <see cref="OtlpLoggingOptions"/>. A missing
    /// section yields the type's defaults, i.e. <see cref="OtlpLoggingOptions.Enabled"/> is false.
    /// </summary>
    public static OtlpLoggingOptions BindOtlpLoggingOptions(IConfiguration configuration)
    {
        var options = new OtlpLoggingOptions();
        configuration.GetSection(OtlpLoggingOptions.SectionName).Bind(options);
        return options;
    }

    /// <summary>
    /// Adds the OTLP sink to <paramref name="loggerConfiguration"/> when <paramref name="options"/>
    /// is enabled. Returns whether the sink was applied, providing a test-observable seam without
    /// requiring reflection into Serilog internals. Enabled without an Endpoint is a
    /// misconfiguration: a warning is written to stderr and the sink is not applied, because the
    /// sink's built-in default endpoint assumes gRPC conventions and would silently mismatch the
    /// configured protocol. An Endpoint that is not an absolute http or https URL is likewise
    /// warned and skipped, so both protocols reject a malformed endpoint the same way, and so is
    /// any sink construction failure such as an invalid header name or value.
    /// </summary>
    public static bool ApplyOtlpSink(LoggerConfiguration loggerConfiguration, OtlpLoggingOptions options)
    {
        if (!options.Enabled)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(options.Endpoint))
        {
            Console.Error.WriteLine(
                "OtlpLogging is enabled but no Endpoint is configured; OTLP export is not applied."
            );
            return false;
        }

        // Validate the endpoint before handing it to the sink; otherwise the two protocols
        // diverge on the same bad value. A host:port endpoint like "collector:4317" parses as an
        // absolute URI whose scheme is the host name: the gRPC exporter throws for it during sink
        // construction, taking down startup, while HttpProtobuf accepts it and fails on every
        // export attempt.
        if (
            !Uri.TryCreate(options.Endpoint, UriKind.Absolute, out var endpointUri)
            || (endpointUri.Scheme != Uri.UriSchemeHttp && endpointUri.Scheme != Uri.UriSchemeHttps)
        )
        {
            Console.Error.WriteLine(
                $"OtlpLogging Endpoint '{options.Endpoint}' is not an absolute http or https URL; OTLP export is not applied."
            );
            return false;
        }

        // Sink and exporter construction happen inside the WriteTo call, and they validate the
        // remaining options with rules that differ by protocol and belong to the transport
        // libraries: the HTTP exporter rejects, for example, a header value with a trailing
        // newline or a content header name, while the gRPC exporter rejects header key characters
        // HTTP accepts. Rather than replicating those rules, any construction failure degrades to
        // the same warn-and-skip posture as the guards above.
        try
        {
            // ignoreEnvironment: true keeps the OtlpLogging section authoritative; otherwise the sink
            // lets OTEL_EXPORTER_OTLP_* environment variables silently override these values.
            loggerConfiguration.WriteTo.OpenTelemetry(
                o =>
                {
                    o.Endpoint = options.Endpoint;
                    o.Protocol = options.Protocol;
                    o.ResourceAttributes = new Dictionary<string, object>(options.ToResourceAttributes());

                    foreach (var header in options.Headers)
                    {
                        if (!string.IsNullOrWhiteSpace(header.Key))
                        {
                            o.Headers[header.Key] = header.Value;
                        }
                    }

                    // Bound every export attempt: the sink's gRPC calls carry no deadline and would
                    // otherwise hang indefinitely on a stalled collector, wedging the batch worker
                    // and blocking logger disposal at shutdown.
                    o.HttpMessageHandler = new BoundedExportTimeoutHandler(
                        ExportAttemptTimeout,
                        new SocketsHttpHandler()
                    );
                },
                ignoreEnvironment: true
            );
        }
        catch (Exception exception)
        {
            // The exception message is deliberately not logged: it can embed a configured header
            // value, and header values are secret material.
            Console.Error.WriteLine(
                $"OtlpLogging sink construction failed ({exception.GetType().Name}); check the OtlpLogging section, including Headers entries; OTLP export is not applied."
            );
            return false;
        }

        // Exporter failures (e.g. an unreachable collector) do not throw and are otherwise silent.
        // Wire SelfLog to stderr so they remain diagnosable. Enabled only after successful
        // construction, so a skipped sink leaves stderr behavior unchanged.
        Serilog.Debugging.SelfLog.Enable(Console.Error);

        return true;
    }

    // Cap on a single OTLP export attempt, applied to both protocols through the message handler.
    internal static readonly TimeSpan ExportAttemptTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Configures the application's Serilog logger from <paramref name="configuration"/>, including
    /// optional OTLP export. OTLP is configured exclusively through the "OtlpLogging" section and
    /// cannot be routed through the "Serilog" section's "Using"/"WriteTo" configuration:
    /// configuration-driven sink discovery is pinned to the Console and File sink assemblies, so a
    /// WriteTo entry naming the OTLP sink is ignored, and a warning is written to stderr when one
    /// is present.
    /// </summary>
    public static Serilog.ILogger ConfigureLogging(IConfiguration configuration)
    {
        WarnIfRawSerilogConfigurationNamesOtlpSink(configuration);

        // Pin configuration-driven sink discovery to the sinks supported through the Serilog
        // section (Console and File). Without this, the compiled-in OTLP sink is reachable from
        // raw Serilog:Using/WriteTo configuration, bypassing every OtlpLogging safeguard.
        var configurationReaderOptions = new ConfigurationReaderOptions(
            typeof(ConsoleLoggerConfigurationExtensions).Assembly,
            typeof(FileLoggerConfigurationExtensions).Assembly
        );

        var loggerConfiguration = new LoggerConfiguration()
            .ReadFrom.Configuration(configuration, configurationReaderOptions)
            .Enrich.FromLogContext();

        ApplyOtlpSink(loggerConfiguration, BindOtlpLoggingOptions(configuration));

        return loggerConfiguration.CreateLogger();
    }

    /// <summary>
    /// Writes a startup warning to stderr when a Serilog:WriteTo entry names the OTLP sink, which
    /// the pinned configuration reader ignores. The reader's own SelfLog notice cannot carry this
    /// warning: it is emitted before any SelfLog listener is installed, and SelfLog does not buffer.
    /// </summary>
    private static void WarnIfRawSerilogConfigurationNamesOtlpSink(IConfiguration configuration)
    {
        var writeToSection = configuration.GetSection("Serilog:WriteTo");

        // Cover both configuration shapes for a sink entry: a bare sink name as the entry's value,
        // and an object entry carrying the sink name under "Name".
        bool rawOtlpSinkConfigured = writeToSection
            .GetChildren()
            .Any(sinkEntry =>
                string.Equals(
                    sinkEntry.Value ?? sinkEntry["Name"],
                    "OpenTelemetry",
                    StringComparison.OrdinalIgnoreCase
                )
            );

        if (rawOtlpSinkConfigured)
        {
            Console.Error.WriteLine(
                "Serilog:WriteTo names the OpenTelemetry sink, which cannot be configured through the Serilog section; the entry is ignored. Configure OTLP export through the OtlpLogging section instead."
            );
        }
    }
}
