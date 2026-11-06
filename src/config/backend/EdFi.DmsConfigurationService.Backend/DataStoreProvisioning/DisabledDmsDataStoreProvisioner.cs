// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;

/// <summary>
/// The provisioner registered whenever no orchestrator can run (4.4): every call returns
/// <c>Failure(NotReady)</c> derived from the current readiness and touches nothing. It serves a disabled
/// feature (<c>FeatureDisabled</c>) and, until Step 1.4 delivers the orchestrator, an enabled feature that
/// is not ready (<c>AdapterUnavailable</c> or <c>StartupValidationFailed</c>). Readiness can never be
/// <c>Ready</c> while this class is the registered provisioner; if it were, that is a registration error
/// and the call throws rather than pretending to provision.
/// </summary>
public sealed class DisabledDmsDataStoreProvisioner(IDmsDataStoreProvisionerReadiness readiness)
    : IDmsDataStoreProvisioner
{
    public const string Operation = "Readiness";

    /// <summary>The <c>NotReady</c> failure a call receives under the given readiness (D-2).</summary>
    public static DmsProvisioningFailure RefusalFor(DmsProvisionerReadiness readiness)
    {
        ArgumentNullException.ThrowIfNull(readiness);
        DmsProvisioningFailureCode code = readiness switch
        {
            DmsProvisionerReadiness.Disabled => DmsProvisioningFailureCode.FeatureDisabled,
            DmsProvisionerReadiness.NotReady notReady => notReady.Reasons.Any(reason =>
                reason.Code == DmsProvisioningFailureCode.AdapterUnavailable
            )
                ? DmsProvisioningFailureCode.AdapterUnavailable
                : DmsProvisioningFailureCode.StartupValidationFailed,
            DmsProvisionerReadiness.Ready => throw new InvalidOperationException(
                "DMS data store provisioning readiness is Ready, but no provisioner orchestrator is registered in this build."
            ),
            _ => throw new ArgumentOutOfRangeException(nameof(readiness), "Unknown readiness state."),
        };

        return DmsProvisioningFailure.Create(code, DmsProvisioningDiagnostic.ForOperation(Operation));
    }

    public Task<DmsArtifactResolution> ResolveArtifactAsync(
        DmsDatabaseTemplate template,
        CancellationToken cancellationToken
    ) =>
        Task.FromResult<DmsArtifactResolution>(
            new DmsArtifactResolution.Failure(RefusalFor(readiness.Current), DmsCleanupReport.Clean)
        );

    public Task<DmsDataStoreCreateResult> CreateAsync(
        DmsDataStoreCreateRequest request,
        CancellationToken cancellationToken
    ) =>
        Task.FromResult<DmsDataStoreCreateResult>(
            new DmsDataStoreCreateResult.Failure(RefusalFor(readiness.Current), DmsCleanupReport.Clean)
        );

    public Task<DmsDataStoreDeleteResult> DeleteAsync(
        DmsDataStoreDeleteRequest request,
        CancellationToken cancellationToken
    ) =>
        Task.FromResult<DmsDataStoreDeleteResult>(
            new DmsDataStoreDeleteResult.Failure(RefusalFor(readiness.Current), DmsCleanupReport.Clean)
        );
}
