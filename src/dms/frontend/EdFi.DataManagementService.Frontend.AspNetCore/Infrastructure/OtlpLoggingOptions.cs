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

    // Header values are secret material: source them from a secret store or environment
    // variable, never a committed configuration file.
    public Dictionary<string, string> Headers { get; set; } = [];

    /// <summary>
    /// Builds the OpenTelemetry resource attributes for this service. Optional attributes that
    /// have not been configured are simply absent from the exported resource. The deployment
    /// environment is emitted under both the legacy "deployment.environment" key and its stable
    /// semantic-convention replacement "deployment.environment.name" so consumers of either
    /// convention can find it.
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
            resourceAttributes["deployment.environment.name"] = DeploymentEnvironment;
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
