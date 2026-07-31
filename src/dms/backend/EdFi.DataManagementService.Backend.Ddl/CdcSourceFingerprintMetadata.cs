// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;

namespace EdFi.DataManagementService.Backend.Ddl;

internal static class CdcSourceFingerprintMetadata
{
    internal const string Version = "dms-source-fingerprint-v1";
    internal static readonly CdcSafeName SafeArtifactName = new("dms.DataStoreIdentity");

    internal static async Task<CdcProviderSetupStepResult> ReadAsync(
        ICdcProviderDatabaseExecutor executor,
        string sql,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);

        try
        {
            var rows = await executor.QueryAsync(sql, cancellationToken).ConfigureAwait(false);
            return FromRows(rows);
        }
        catch (DbException exception)
        {
            return Unavailable(exception);
        }
        catch (InvalidOperationException exception)
        {
            return Unavailable(exception);
        }
    }

    private static CdcProviderSetupStepResult FromRows(
        IReadOnlyList<IReadOnlyDictionary<string, string?>> rows
    )
    {
        if (rows.Count != 1)
        {
            return Missing(rows.Count.ToString());
        }

        var sourceIdentity = ReadOptional(rows[0], "source_identity");
        if (string.IsNullOrWhiteSpace(sourceIdentity))
        {
            return Missing("empty");
        }

        if (sourceIdentity == "00000000-0000-0000-0000-000000000000")
        {
            return Invalid(sourceIdentity);
        }

        var fingerprint = new CdcSourceFingerprint(Version, SafeText(sourceIdentity));
        var observedValues = new Dictionary<string, string>
        {
            ["source_fingerprint_version"] = fingerprint.Version,
            ["source_identity"] = fingerprint.Value,
        };

        return new CdcProviderSetupStepResult(
            observedSourceFingerprint: fingerprint,
            artifactInventory:
            [
                new CdcProviderArtifactObservation(
                    CdcProviderArtifactKind.SourceFingerprint,
                    SafeArtifactName,
                    CdcProviderArtifactState.Matched,
                    observedValues
                ),
            ]
        );
    }

    private static CdcProviderSetupStepResult Missing(string observedValue) =>
        new(
            artifactInventory:
            [
                new CdcProviderArtifactObservation(
                    CdcProviderArtifactKind.SourceFingerprint,
                    SafeArtifactName,
                    CdcProviderArtifactState.Missing,
                    new Dictionary<string, string> { ["source_identity"] = "missing" }
                ),
            ],
            diagnostics:
            [
                new CdcProviderDiagnostic(
                    Code: "CDC_SOURCE_FINGERPRINT_MISSING",
                    Category: CdcProviderDiagnosticCategory.MissingRequiredSourceObject,
                    Severity: CdcProviderDiagnosticSeverity.Error,
                    PrincipalKind: CdcPrincipalKind.None,
                    ArtifactKind: CdcProviderArtifactKind.SourceFingerprint,
                    SafeName: SafeArtifactName,
                    ExpectedValue: "DataStoreIdentity singleton source identity",
                    ObservedValue: observedValue,
                    ProviderErrorClass: null,
                    Classification: CdcProviderRetryContinuityClassification.FailClosed
                ),
            ]
        );

    private static CdcProviderSetupStepResult Invalid(string observedValue) =>
        new(
            artifactInventory:
            [
                new CdcProviderArtifactObservation(
                    CdcProviderArtifactKind.SourceFingerprint,
                    SafeArtifactName,
                    CdcProviderArtifactState.Mismatched,
                    new Dictionary<string, string> { ["source_identity"] = observedValue }
                ),
            ],
            diagnostics:
            [
                new CdcProviderDiagnostic(
                    Code: "CDC_SOURCE_FINGERPRINT_INVALID",
                    Category: CdcProviderDiagnosticCategory.ValidationMismatch,
                    Severity: CdcProviderDiagnosticSeverity.Error,
                    PrincipalKind: CdcPrincipalKind.None,
                    ArtifactKind: CdcProviderArtifactKind.SourceFingerprint,
                    SafeName: SafeArtifactName,
                    ExpectedValue: "non-zero source identity",
                    ObservedValue: observedValue,
                    ProviderErrorClass: null,
                    Classification: CdcProviderRetryContinuityClassification.FailClosed
                ),
            ]
        );

    private static CdcProviderSetupStepResult Unavailable(Exception exception) =>
        new(
            artifactInventory:
            [
                new CdcProviderArtifactObservation(
                    CdcProviderArtifactKind.SourceFingerprint,
                    SafeArtifactName,
                    CdcProviderArtifactState.Unavailable,
                    new Dictionary<string, string> { ["source_identity"] = "unavailable" }
                ),
            ],
            diagnostics:
            [
                new CdcProviderDiagnostic(
                    Code: "CDC_SOURCE_FINGERPRINT_UNAVAILABLE",
                    Category: CdcProviderDiagnosticCategory.SetupPrincipalFailure,
                    Severity: CdcProviderDiagnosticSeverity.Error,
                    PrincipalKind: CdcPrincipalKind.SetupPrincipal,
                    ArtifactKind: CdcProviderArtifactKind.SourceFingerprint,
                    SafeName: SafeArtifactName,
                    ExpectedValue: "readable DataStoreIdentity source identity",
                    ObservedValue: "unavailable",
                    ProviderErrorClass: exception.GetType().Name,
                    Classification: CdcProviderRetryContinuityClassification.FailClosed
                ),
            ]
        );

    private static string? ReadOptional(IReadOnlyDictionary<string, string?> row, string key) =>
        row.TryGetValue(key, out var value) ? value : null;

    private static string SafeText(string value)
    {
        if (value.Any(char.IsControl))
        {
            return new string(value.Where(character => !char.IsControl(character)).ToArray());
        }

        return value;
    }
}
