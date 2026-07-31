// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Microsoft.Extensions.Configuration;
using Serilog;

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
    /// configured protocol.
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

        // Exporter failures (e.g. an unreachable collector) do not throw and are otherwise silent.
        // Wire SelfLog to stderr so they remain diagnosable.
        Serilog.Debugging.SelfLog.Enable(Console.Error);

        // ignoreEnvironment: true keeps the OtlpLogging section authoritative; otherwise the sink
        // lets OTEL_EXPORTER_OTLP_* environment variables silently override these values.
        loggerConfiguration.WriteTo.OpenTelemetry(
            o =>
            {
                o.Endpoint = options.Endpoint;
                o.Protocol = options.Protocol;
                o.ResourceAttributes = new Dictionary<string, object>(options.ToResourceAttributes());
            },
            ignoreEnvironment: true
        );

        return true;
    }

    /// <summary>
    /// Configures the application's Serilog logger from <paramref name="configuration"/>, including
    /// optional OTLP export. OTLP is configured exclusively through the "OtlpLogging" section - never
    /// through the "Serilog" section's "Using"/"WriteTo" configuration.
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
