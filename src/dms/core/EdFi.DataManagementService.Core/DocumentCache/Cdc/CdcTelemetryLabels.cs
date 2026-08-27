// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using System.Text.Json;
using EdFi.DataManagementService.Core.Utilities;

namespace EdFi.DataManagementService.Core.DocumentCache.Cdc;

public sealed record CdcTelemetryLabels
{
    private const int MaximumLabelLength = 128;

    public CdcTelemetryLabels(
        CdcProvider provider,
        CdcReadiness readiness,
        CdcDiagnosticComponent component,
        string? deploymentKey,
        string? instanceKey,
        long generation,
        string? outcome
    )
    {
        Provider = ToLowerCamel(provider);
        Readiness = ToLowerCamel(readiness);
        Component = ToLowerCamel(component);
        DeploymentKey = SafeTokenLabel(deploymentKey);
        InstanceKey = SafeTokenLabel(instanceKey);
        Generation = generation > 0 ? generation.ToString(CultureInfo.InvariantCulture) : "unknown";
        Outcome = SafeTokenLabel(outcome);
    }

    public string Provider { get; }

    public string Readiness { get; }

    public string Component { get; }

    public string DeploymentKey { get; }

    public string InstanceKey { get; }

    public string Generation { get; }

    public string Outcome { get; }

    public IReadOnlyDictionary<string, string> ToDictionary() =>
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["provider"] = Provider,
            ["readiness"] = Readiness,
            ["component"] = Component,
            ["deploymentKey"] = DeploymentKey,
            ["instanceKey"] = InstanceKey,
            ["generation"] = Generation,
            ["outcome"] = Outcome,
        };

    public static CdcTelemetryLabels FromTarget(
        CdcTargetIdentity targetIdentity,
        CdcReadiness readiness,
        CdcDiagnosticComponent component,
        string? outcome
    )
    {
        ArgumentNullException.ThrowIfNull(targetIdentity);

        return new(
            targetIdentity.Provider,
            readiness,
            component,
            targetIdentity.DeploymentKey,
            targetIdentity.InstanceKey,
            targetIdentity.Generation,
            outcome
        );
    }

    private static string SafeTokenLabel(string? value)
    {
        string sanitized = LoggingSanitizer.SanitizeForLogging(value);
        if (!CdcKafkaSafeTokenValidator.IsValid(sanitized))
        {
            return "invalid";
        }

        return sanitized.Length <= MaximumLabelLength ? sanitized : sanitized[..MaximumLabelLength];
    }

    private static string ToLowerCamel<TEnum>(TEnum value)
        where TEnum : struct, Enum => JsonNamingPolicy.CamelCase.ConvertName(value.ToString());
}
