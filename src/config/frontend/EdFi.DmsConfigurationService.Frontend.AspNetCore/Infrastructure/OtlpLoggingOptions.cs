// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Reflection;
using Serilog.Sinks.OpenTelemetry;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;

/// <summary>
/// Configuration options for exporting Serilog log events to an OTLP (OpenTelemetry Protocol) endpoint.
/// Bound from the top-level "OtlpLogging" configuration section.
/// </summary>
public class OtlpLoggingOptions
{
    public const string SectionName = "OtlpLogging";

    /// <summary>
    /// When true, log events are also exported to the configured OTLP endpoint.
    /// Disabled by default so operators must opt in.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// The OTLP collector endpoint, e.g. "http://collector:4318". Required when <see cref="Enabled"/>
    /// is true: when missing, the sink is not applied and a warning is written to stderr.
    /// </summary>
    public string? Endpoint { get; set; }

    /// <summary>
    /// The OTLP wire protocol to use when sending log records.
    /// </summary>
    public OtlpProtocol Protocol { get; set; } = OtlpProtocol.HttpProtobuf;

    /// <summary>
    /// The value reported as the "service.name" resource attribute.
    /// </summary>
    public string ServiceName { get; set; } = "EdFi.DmsConfigurationService";

    /// <summary>
    /// The value reported as the "service.version" resource attribute. Defaults to this assembly's
    /// informational version, falling back to its assembly version.
    /// </summary>
    public string ServiceVersion { get; set; } =
        typeof(OtlpLoggingOptions)
            .Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
        ?? typeof(OtlpLoggingOptions).Assembly.GetName().Version?.ToString()
        ?? string.Empty;

    /// <summary>
    /// Optional value reported as the "deployment.environment" resource attribute. Omitted when unset.
    /// </summary>
    public string? DeploymentEnvironment { get; set; }

    /// <summary>
    /// Optional value reported as the "service.instance.id" resource attribute. Omitted when unset.
    /// </summary>
    public string? ServiceInstanceId { get; set; }

    /// <summary>
    /// Builds the OTLP resource attributes for this configuration. "service.name" and "service.version"
    /// are always included; "deployment.environment" and "service.instance.id" are included only when set,
    /// so the sink's own defaults apply otherwise.
    /// </summary>
    public IReadOnlyDictionary<string, object> ToResourceAttributes()
    {
        var attributes = new Dictionary<string, object>
        {
            ["service.name"] = ServiceName,
            ["service.version"] = ServiceVersion,
        };

        if (!string.IsNullOrEmpty(DeploymentEnvironment))
        {
            attributes["deployment.environment"] = DeploymentEnvironment;
        }

        if (!string.IsNullOrEmpty(ServiceInstanceId))
        {
            attributes["service.instance.id"] = ServiceInstanceId;
        }

        return attributes;
    }
}
