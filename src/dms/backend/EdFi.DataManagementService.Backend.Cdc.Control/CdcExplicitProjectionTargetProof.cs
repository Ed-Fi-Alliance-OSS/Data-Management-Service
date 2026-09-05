// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache.Cdc;
using Microsoft.Extensions.Configuration;

namespace EdFi.DataManagementService.Backend.Cdc.Control;

/// <summary>
/// What the raw projector configuration says about the target CDC is being enabled for.
/// </summary>
public enum CdcExplicitProjectionTargetState
{
    /// <summary>The pair is one of the projector's own configured DocumentCache targets.</summary>
    Configured,

    /// <summary>The section is present and readable, and does not name this pair.</summary>
    NotConfigured,

    /// <summary>The DocumentCache targets section is absent from the configuration.</summary>
    SectionMissing,

    /// <summary>The configuration source could not be read.</summary>
    Unreadable,
}

public sealed record CdcExplicitProjectionTargetProofResult(
    CdcExplicitProjectionTargetState State,
    IReadOnlyList<CdcDiagnostic> Diagnostics
)
{
    public bool Succeeded => State == CdcExplicitProjectionTargetState.Configured && Diagnostics.Count == 0;
}

/// <summary>
/// Proves that the target being enabled is one the operator configured the DMS projector with, by
/// reading <c>DataManagement:DocumentCache:Targets</c> from the original configuration.
/// </summary>
/// <remarks>
/// The proof deliberately reads raw configuration rather than resolving through the bound
/// <c>DocumentCacheOptions</c> or the target registry. An administrative host replaces the bound
/// target list with a single entry synthesized from its own invocation arguments, so resolving
/// through it would prove only that the operator named a target on the command line — the question
/// the proof exists to answer is whether the projector itself is configured to project it.
/// </remarks>
public sealed class CdcExplicitProjectionTargetProof(IConfiguration configuration)
{
    public const string TargetsSectionName = $"{DocumentCacheOptions.SectionName}:Targets";

    private const string TenantKeyName = "TenantKey";
    private const string DataStoreIdName = "DataStoreId";

    public CdcExplicitProjectionTargetProofResult Prove(CdcValidatedTarget target, DateTimeOffset observedAt)
    {
        ArgumentNullException.ThrowIfNull(target);

        IReadOnlyList<ConfiguredTarget>? configuredTargets;
        try
        {
            configuredTargets = ReadConfiguredTargets();
        }
        catch (Exception exception) when (exception is InvalidOperationException or FormatException)
        {
            // Only the failure is reported. A configuration provider's message quotes the source it
            // could not read, which is a file path or a connection.
            return Failed(
                CdcExplicitProjectionTargetState.Unreadable,
                "unreadable",
                "CDC enablement could not read the DMS projection target configuration.",
                retryable: true,
                observedAt
            );
        }

        if (configuredTargets is null)
        {
            return Failed(
                CdcExplicitProjectionTargetState.SectionMissing,
                "absent",
                $"CDC enablement requires the target to be a configured {TargetsSectionName} entry, and "
                    + "that section is not configured.",
                retryable: false,
                observedAt
            );
        }

        return configuredTargets.Any(configured => Matches(configured, target))
            ? new(CdcExplicitProjectionTargetState.Configured, [])
            : Failed(
                CdcExplicitProjectionTargetState.NotConfigured,
                $"{configuredTargets.Count.ToString(CultureInfo.InvariantCulture)} configured targets",
                $"CDC enablement requires the target to be a configured {TargetsSectionName} entry of the "
                    + "DMS projector itself.",
                retryable: false,
                observedAt
            );
    }

    /// <summary>
    /// Reads the configured pairs, or null when the section is absent. An entry the section cannot be
    /// read as a target pair is simply not a configured target: it can never match, and a malformed
    /// entry must not be mistaken for the one being enabled.
    /// </summary>
    private IReadOnlyList<ConfiguredTarget>? ReadConfiguredTargets()
    {
        IConfigurationSection section = configuration.GetSection(TargetsSectionName);
        if (!section.Exists())
        {
            return null;
        }

        List<ConfiguredTarget> configuredTargets = [];
        foreach (IConfigurationSection entry in section.GetChildren())
        {
            if (
                long.TryParse(
                    entry[DataStoreIdName],
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out long dataStoreId
                )
            )
            {
                configuredTargets.Add(new(entry[TenantKeyName] ?? string.Empty, dataStoreId));
            }
        }

        return configuredTargets;
    }

    /// <summary>
    /// The configured pair is written in the projector's own terms, where the default tenant is the
    /// empty key, so it is compared after the same mapping the shared target validator applies.
    /// </summary>
    private static bool Matches(ConfiguredTarget configured, CdcValidatedTarget target) =>
        string.Equals(
            CdcTargetValidator.MapE18TenantKeyToBindingTenantKey(configured.TenantKey),
            target.TenantKey,
            StringComparison.Ordinal
        )
        && string.Equals(
            configured.DataStoreId.ToString(CultureInfo.InvariantCulture),
            target.DataStoreId,
            StringComparison.Ordinal
        );

    private static CdcExplicitProjectionTargetProofResult Failed(
        CdcExplicitProjectionTargetState state,
        string observed,
        string message,
        bool retryable,
        DateTimeOffset observedAt
    ) =>
        new(
            state,
            [
                new CdcDiagnostic(
                    "projectionTargetNotConfigured",
                    CdcDiagnosticCategory.TargetMismatch,
                    CdcDiagnosticSeverity.Error,
                    CdcDiagnosticComponent.Projection,
                    observedAt,
                    message,
                    retryable,
                    artifactKind: "documentCacheProjectionTarget",
                    artifactName: TargetsSectionName,
                    expected: "the enabled target",
                    observed: observed
                ).WithPath("$.targetIdentity"),
            ]
        );

    private sealed record ConfiguredTarget(string TenantKey, long DataStoreId);
}
