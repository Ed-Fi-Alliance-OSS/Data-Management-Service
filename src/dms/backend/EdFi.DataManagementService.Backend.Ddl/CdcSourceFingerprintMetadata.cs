// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.Security.Cryptography;
using System.Text;

namespace EdFi.DataManagementService.Backend.Ddl;

public static class CdcSourceFingerprintMetadata
{
    public const string Version = "dms-source-fingerprint-v1";
    private const string PayloadDomain = "ed-fi-dms-source-v1";
    internal static readonly CdcSafeName SafeArtifactName = new("dms.DataStoreIdentity");

    internal static async Task<CdcProviderSetupStepResult> ReadAsync(
        ICdcProviderDatabaseExecutor executor,
        string sql,
        CdcProvider provider,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);

        try
        {
            var rows = await executor.QueryAsync(sql, cancellationToken).ConfigureAwait(false);
            return FromRows(rows, provider);
        }
        catch (DbException exception)
        {
            return Unavailable(executor, exception);
        }
        catch (InvalidOperationException exception)
        {
            return Unavailable(executor, exception);
        }
    }

    public static CdcSourceFingerprint Compute(CdcProvider provider, string sourceIdentity)
    {
        if (!TryNormalizeSourceIdentity(sourceIdentity, out var normalizedSourceIdentity, out var reason))
        {
            throw new ArgumentException(
                $"CDC source identity must be a non-zero UUID in D format; observed {reason}.",
                nameof(sourceIdentity)
            );
        }

        var providerToken = ProviderToken(provider);
        var payload = Encoding.UTF8.GetBytes($"{PayloadDomain}\0{providerToken}\0{normalizedSourceIdentity}");
        var hash = SHA256.HashData(payload);

        return new CdcSourceFingerprint(Version, $"sha256:{Convert.ToHexString(hash).ToLowerInvariant()}");
    }

    public static CdcSourceFingerprint Validate(CdcSourceFingerprint sourceFingerprint, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(sourceFingerprint);

        CdcBindingContractValidation.ValidateRequiredSafeText(
            sourceFingerprint.Version,
            $"{parameterName}.{nameof(sourceFingerprint.Version)}"
        );
        if (!string.Equals(sourceFingerprint.Version, Version, StringComparison.Ordinal))
        {
            throw new ArgumentException($"CDC source fingerprint version must be {Version}.", parameterName);
        }

        string value = CdcBindingContractValidation.ValidateRequiredSafeText(
            sourceFingerprint.Value,
            $"{parameterName}.{nameof(sourceFingerprint.Value)}"
        );
        const string prefix = "sha256:";
        if (!value.StartsWith(prefix, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "CDC source fingerprint value must use sha256 prefix.",
                parameterName
            );
        }

        string hash = value[prefix.Length..];
        if (hash.Length != 64 || hash.Any(character => !IsLowerHex(character)))
        {
            throw new ArgumentException(
                "CDC source fingerprint value must contain a lowercase SHA-256 value.",
                parameterName
            );
        }

        return sourceFingerprint;
    }

    private static bool IsLowerHex(char character) => character is >= '0' and <= '9' or >= 'a' and <= 'f';

    private static CdcProviderSetupStepResult FromRows(
        IReadOnlyList<IReadOnlyDictionary<string, string?>> rows,
        CdcProvider provider
    )
    {
        if (rows.Count != 1)
        {
            return Missing(rows.Count.ToString());
        }

        var sourceIdentity = ReadOptional(rows[0], "source_identity");
        if (!TryNormalizeSourceIdentity(sourceIdentity, out _, out var invalidReason))
        {
            return Invalid(invalidReason);
        }

        var fingerprint = Compute(provider, sourceIdentity!);
        var observedValues = new Dictionary<string, string>
        {
            ["source_fingerprint_version"] = fingerprint.Version,
            ["physical_source_fingerprint"] = fingerprint.Value,
            ["provider_token"] = ProviderToken(provider),
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
                    new Dictionary<string, string> { ["source_identity_status"] = "missing" }
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
                    ObservedValue: $"row_count:{observedValue}",
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
                    new Dictionary<string, string> { ["source_identity_status"] = observedValue }
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
                    ExpectedValue: "non-zero UUID source identity",
                    ObservedValue: observedValue,
                    ProviderErrorClass: null,
                    Classification: CdcProviderRetryContinuityClassification.FailClosed
                ),
            ]
        );

    private static CdcProviderSetupStepResult Unavailable(
        ICdcProviderDatabaseExecutor executor,
        Exception exception
    )
    {
        var providerErrorIdentity = executor.TryMapProviderErrorIdentity(exception);

        return new CdcProviderSetupStepResult(
            artifactInventory:
            [
                new CdcProviderArtifactObservation(
                    CdcProviderArtifactKind.SourceFingerprint,
                    SafeArtifactName,
                    CdcProviderArtifactState.Unavailable,
                    new Dictionary<string, string> { ["source_identity_status"] = "unavailable" }
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
                )
                {
                    ProviderErrorCode = providerErrorIdentity?.ProviderErrorCode,
                    ProviderErrorState = providerErrorIdentity?.ProviderErrorState,
                },
            ]
        );
    }

    private static string? ReadOptional(IReadOnlyDictionary<string, string?> row, string key) =>
        row.TryGetValue(key, out var value) ? value : null;

    private static bool TryNormalizeSourceIdentity(
        string? sourceIdentity,
        out string normalizedSourceIdentity,
        out string invalidReason
    )
    {
        if (string.IsNullOrWhiteSpace(sourceIdentity))
        {
            normalizedSourceIdentity = "";
            invalidReason = "blank_source_identity";
            return false;
        }

        if (!Guid.TryParse(sourceIdentity, out var sourceGuid))
        {
            normalizedSourceIdentity = "";
            invalidReason = "malformed_source_identity";
            return false;
        }

        if (sourceGuid == Guid.Empty)
        {
            normalizedSourceIdentity = "";
            invalidReason = "zero_source_identity";
            return false;
        }

        normalizedSourceIdentity = sourceGuid.ToString("D").ToLowerInvariant();
        invalidReason = "";
        return true;
    }

    private static string ProviderToken(CdcProvider provider) =>
        provider switch
        {
            CdcProvider.Postgresql => "postgresql",
            CdcProvider.SqlServer => "sqlserver",
            _ => throw new ArgumentOutOfRangeException(
                nameof(provider),
                provider,
                "Unsupported CDC provider."
            ),
        };
}
