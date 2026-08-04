// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Microsoft.Extensions.Configuration;
using Serilog;
using Serilog.Settings.Configuration;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Infrastructure;

/// <summary>
/// Builds the Serilog logger used by the application, including optional OTLP export.
/// OTLP is configured exclusively through <see cref="OtlpLoggingOptions"/> and cannot be
/// routed through the Serilog:Using/WriteTo configuration section: configuration-driven
/// sink discovery is pinned to the Console and File sink assemblies, so a WriteTo entry
/// naming the OTLP sink is ignored with a SelfLog notice.
/// </summary>
public static class LoggingConfigurator
{
    /// <summary>
    /// Binds the OtlpLogging configuration section. A missing section yields defaults, which
    /// leave OTLP export disabled.
    /// </summary>
    public static OtlpLoggingOptions BindOtlpLoggingOptions(IConfiguration configuration)
    {
        var otlpLoggingOptions = new OtlpLoggingOptions();
        configuration.GetSection(OtlpLoggingOptions.SectionName).Bind(otlpLoggingOptions);
        return otlpLoggingOptions;
    }

    /// <summary>
    /// Adds the OTLP sink to the given logger configuration when enabled. Returns whether the
    /// sink was applied, providing a test-observable seam without reflecting into Serilog internals.
    /// Enabled without an Endpoint is a misconfiguration: a warning is written to stderr and the
    /// sink is not applied, because the sink's built-in default endpoint assumes gRPC conventions
    /// and would silently mismatch the configured protocol.
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

        // Exporter failures (for example, an unreachable collector) would otherwise fail silently.
        // Enabling SelfLog ensures they are diagnosable on stderr.
        Serilog.Debugging.SelfLog.Enable(Console.Error);

        // ignoreEnvironment: true keeps the OtlpLogging section authoritative; otherwise the sink
        // lets OTEL_EXPORTER_OTLP_* environment variables silently override these values.
        loggerConfiguration.WriteTo.OpenTelemetry(
            o =>
            {
                o.Endpoint = options.Endpoint;
                o.Protocol = options.Protocol;
                o.ResourceAttributes = options
                    .ToResourceAttributes()
                    .ToDictionary(attribute => attribute.Key, attribute => attribute.Value);

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

        return true;
    }

    // Cap on a single OTLP export attempt, applied to both protocols through the message handler.
    internal static readonly TimeSpan ExportAttemptTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Builds the application's Serilog logger from configuration, applying the OTLP sink when enabled.
    /// </summary>
    public static Serilog.ILogger ConfigureLogging(IConfiguration configuration)
    {
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
}
