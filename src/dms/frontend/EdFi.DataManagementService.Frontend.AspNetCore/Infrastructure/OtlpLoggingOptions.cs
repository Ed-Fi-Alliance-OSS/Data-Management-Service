// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Reflection;
using Serilog.Sinks.OpenTelemetry;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Infrastructure;

public class OtlpLoggingOptions
{
    public const string SectionName = "OtlpLogging";

    public bool Enabled { get; set; }

    public string? Endpoint { get; set; }

    public OtlpProtocol Protocol { get; set; } = OtlpProtocol.HttpProtobuf;

    public string ServiceName { get; set; } = "EdFi.DataManagementService";

    public string ServiceVersion { get; set; } = GetDefaultServiceVersion();

    public string? DeploymentEnvironment { get; set; }

    public string? ServiceInstanceId { get; set; }

    /// <summary>
    /// Builds the OpenTelemetry resource attributes for this service, omitting optional
    /// attributes that have not been configured so that the sink's own defaults apply.
    /// </summary>
    public IReadOnlyDictionary<string, object> ToResourceAttributes()
    {
        var resourceAttributes = new Dictionary<string, object>
        {
            ["service.name"] = ServiceName,
            ["service.version"] = ServiceVersion,
        };

        if (!string.IsNullOrEmpty(DeploymentEnvironment))
        {
            resourceAttributes["deployment.environment"] = DeploymentEnvironment;
        }

        if (!string.IsNullOrEmpty(ServiceInstanceId))
        {
            resourceAttributes["service.instance.id"] = ServiceInstanceId;
        }

        return resourceAttributes;
    }

    private static string GetDefaultServiceVersion()
    {
        Assembly assembly = typeof(OtlpLoggingOptions).Assembly;
        return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? string.Empty;
    }
}
