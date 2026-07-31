// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Microsoft.Extensions.Configuration;
using Serilog;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Infrastructure;

/// <summary>
/// Builds the Serilog logger used by the application, including optional OTLP export.
/// OTLP is configured exclusively through <see cref="OtlpLoggingOptions"/> and must never
/// be routed through the Serilog:Using/WriteTo configuration section.
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
            },
            ignoreEnvironment: true
        );

        return true;
    }

    /// <summary>
    /// Builds the application's Serilog logger from configuration, applying the OTLP sink when enabled.
    /// </summary>
    public static Serilog.ILogger ConfigureLogging(IConfiguration configuration)
    {
        var loggerConfiguration = new LoggerConfiguration()
            .ReadFrom.Configuration(configuration)
            .Enrich.FromLogContext();

        ApplyOtlpSink(loggerConfiguration, BindOtlpLoggingOptions(configuration));

        return loggerConfiguration.CreateLogger();
    }
}
