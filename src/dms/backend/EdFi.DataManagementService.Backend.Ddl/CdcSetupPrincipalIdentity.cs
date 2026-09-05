// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;

namespace EdFi.DataManagementService.Backend.Ddl;

/// <summary>
/// Proves that a provider-setup pass is actually running as the configured setup principal.
/// </summary>
/// <remarks>
/// The setup principal and the connector principal are separate identities on purpose: setup creates
/// capture artifacts and issues grants, and the connector reads them under least privilege. The
/// connector principal is enforced by construction — every grant names it — but the setup principal
/// arrives as configuration and is otherwise only carried into the request, never checked against the
/// identity the connection actually authenticated as.
///
/// Without this step a deployment can pass any setup-principal name it likes while the pass runs on
/// whatever connection the host supplied, and the two failure modes are opposite and both bad: a
/// connection with too little privilege fails partway through artifact creation, and a connection with
/// more privilege than the named principal quietly makes the configured value a fiction. Reading the
/// session's own identity and comparing it settles which one is in play before any artifact is touched.
///
/// The comparison ignores case because neither engine treats a principal name as case-sensitive in the
/// way a literal comparison would imply, and a case difference is not the mismatch this guards against.
/// </remarks>
public static class CdcSetupPrincipalIdentity
{
    internal static readonly CdcSafeName SafeArtifactName = new("cdc.SetupPrincipal");

    /// <summary>
    /// The session's own authenticated identity. <c>SESSION_USER</c> and <c>SUSER_SNAME()</c> report the
    /// identity the connection authenticated as rather than one a <c>SET ROLE</c> or an executing
    /// context switched to, which is the identity the deployment configured.
    /// </summary>
    internal const string PostgresqlSql = """
        /* cdc:postgresql:setup-principal */
        SELECT SESSION_USER::text AS setup_principal;
        """;

    internal const string SqlServerSql = """
        /* cdc:sqlserver:setup-principal */
        SELECT CONVERT(nvarchar(128), SUSER_SNAME()) AS setup_principal;
        """;

    internal static async Task<CdcProviderSetupStepResult> VerifyAsync(
        ICdcProviderDatabaseExecutor executor,
        string sql,
        CdcSafeName expectedPrincipal,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);

        try
        {
            var rows = await executor.QueryAsync(sql, cancellationToken).ConfigureAwait(false);
            return FromRows(rows, expectedPrincipal);
        }
        catch (DbException exception)
        {
            return Unavailable(executor, expectedPrincipal, exception);
        }
        catch (InvalidOperationException exception)
        {
            return Unavailable(executor, expectedPrincipal, exception);
        }
    }

    private static CdcProviderSetupStepResult FromRows(
        IReadOnlyList<IReadOnlyDictionary<string, string?>> rows,
        CdcSafeName expectedPrincipal
    )
    {
        if (rows.Count != 1)
        {
            return Mismatched(expectedPrincipal, $"row_count:{rows.Count}");
        }

        string? observedPrincipal = rows[0].TryGetValue("setup_principal", out var value) ? value : null;
        if (string.IsNullOrWhiteSpace(observedPrincipal))
        {
            return Mismatched(expectedPrincipal, "blank");
        }

        if (
            !string.Equals(
                observedPrincipal.Trim(),
                expectedPrincipal.Value,
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            return Mismatched(expectedPrincipal, observedPrincipal.Trim());
        }

        return new CdcProviderSetupStepResult(
            artifactInventory:
            [
                new CdcProviderArtifactObservation(
                    CdcProviderArtifactKind.SetupPrincipalIdentity,
                    SafeArtifactName,
                    CdcProviderArtifactState.Matched,
                    new Dictionary<string, string> { ["setup_principal"] = expectedPrincipal.Value }
                ),
            ]
        );
    }

    private static CdcProviderSetupStepResult Mismatched(
        CdcSafeName expectedPrincipal,
        string observedValue
    ) =>
        new(
            artifactInventory:
            [
                new CdcProviderArtifactObservation(
                    CdcProviderArtifactKind.SetupPrincipalIdentity,
                    SafeArtifactName,
                    CdcProviderArtifactState.Mismatched,
                    new Dictionary<string, string> { ["setup_principal_status"] = "mismatched" }
                ),
            ],
            diagnostics:
            [
                new CdcProviderDiagnostic(
                    Code: "CDC_SETUP_PRINCIPAL_MISMATCH",
                    Category: CdcProviderDiagnosticCategory.SetupPrincipalFailure,
                    Severity: CdcProviderDiagnosticSeverity.Error,
                    PrincipalKind: CdcPrincipalKind.SetupPrincipal,
                    ArtifactKind: CdcProviderArtifactKind.SetupPrincipalIdentity,
                    SafeName: SafeArtifactName,
                    ExpectedValue: expectedPrincipal.Value,
                    ObservedValue: observedValue,
                    ProviderErrorClass: null,
                    Classification: CdcProviderRetryContinuityClassification.FailClosed
                ),
            ]
        );

    private static CdcProviderSetupStepResult Unavailable(
        ICdcProviderDatabaseExecutor executor,
        CdcSafeName expectedPrincipal,
        Exception exception
    )
    {
        var providerErrorIdentity = executor.TryMapProviderErrorIdentity(exception);

        return new CdcProviderSetupStepResult(
            artifactInventory:
            [
                new CdcProviderArtifactObservation(
                    CdcProviderArtifactKind.SetupPrincipalIdentity,
                    SafeArtifactName,
                    CdcProviderArtifactState.Unavailable,
                    new Dictionary<string, string> { ["setup_principal_status"] = "unavailable" }
                ),
            ],
            diagnostics:
            [
                new CdcProviderDiagnostic(
                    Code: "CDC_SETUP_PRINCIPAL_UNAVAILABLE",
                    Category: CdcProviderDiagnosticCategory.SetupPrincipalFailure,
                    Severity: CdcProviderDiagnosticSeverity.Error,
                    PrincipalKind: CdcPrincipalKind.SetupPrincipal,
                    ArtifactKind: CdcProviderArtifactKind.SetupPrincipalIdentity,
                    SafeName: SafeArtifactName,
                    ExpectedValue: expectedPrincipal.Value,
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
}
