// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning.Packaging;

namespace EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;

/// <summary>
/// Registered for a provider whose adapter does not exist in this build (4.4). Environmental validation
/// always reports <c>NotReady(AdapterUnavailable)</c>, so readiness can never become <c>Ready</c> and the
/// orchestrator refuses every call before reaching the engine; every other member throws because no call
/// should ever arrive.
/// </summary>
public sealed class UnavailableDmsTargetEngine : IDmsTargetEngine
{
    public const string Probe = "AdapterResolution";

    public UnavailableDmsTargetEngine(string provider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        Provider = provider;
    }

    public string Provider { get; }

    public Task<DmsEngineEnvironment> ValidateEnvironmentAsync(CancellationToken cancellationToken) =>
        Task.FromResult<DmsEngineEnvironment>(
            new DmsEngineEnvironment.NotReady([
                new DmsReadinessReason(DmsProvisioningFailureCode.AdapterUnavailable, Probe),
            ])
        );

    public Task<DmsServerFacts> ReadServerFactsAsync(CancellationToken cancellationToken) =>
        throw Unavailable();

    public Task<DmsTargetLockResult> AcquireTargetLockAsync(
        DmsTargetDatabaseName name,
        DmsOperationId operation,
        CancellationToken cancellationToken
    ) => throw Unavailable();

    public Task<DmsLiveTargetState> InspectAsync(DmsDatabaseName name, CancellationToken cancellationToken) =>
        throw Unavailable();

    public Task<DmsSessionOpenResult> OpenSessionAsync(
        DmsDatabaseName name,
        DmsInstanceIdentity expectedInstance,
        CancellationToken cancellationToken
    ) => throw Unavailable();

    public Task<DmsIdentityRead> ReadIdentityAsync(DmsSession session, CancellationToken cancellationToken) =>
        throw Unavailable();

    public Task<DmsTargetFacts> ReadTargetFactsAsync(
        DmsSession session,
        CancellationToken cancellationToken
    ) => throw Unavailable();

    public Task<DmsCommandOutcome<DmsInstanceIdentity>> CreateStagingAsync(
        IDmsTargetLockHolder holder,
        DmsDatabaseName staging,
        DmsProvisionClaim claim,
        CancellationToken cancellationToken
    ) => throw Unavailable();

    public Task<DmsCommandOutcome<DmsInstanceIdentity>> RestoreIntoStagingAsync(
        DmsDatabaseName staging,
        DmsTargetDatabaseName target,
        StagedArtifact artifact,
        DmsOperationId operation,
        CancellationToken cancellationToken
    ) => throw Unavailable();

    public Task<DmsCommandOutcome<DmsInstanceIdentity>> RenameIntoTargetAsync(
        IDmsTargetLockHolder holder,
        DmsDatabaseName staging,
        DmsInstanceIdentity stagingInstance,
        DmsTargetDatabaseName target,
        CancellationToken cancellationToken
    ) => throw Unavailable();

    public Task<DmsCommandOutcome> AssignMarkerAsync(
        DmsSession session,
        Guid expectedSourceIdentity,
        CancellationToken cancellationToken
    ) => throw Unavailable();

    public Task<DmsCommandOutcome> FenceAsync(
        IDmsTargetLockHolder holder,
        DmsTargetDatabaseName target,
        DmsInstanceIdentity instance,
        DmsServerSessionId? keep,
        CancellationToken cancellationToken
    ) => throw Unavailable();

    public Task<DmsCommandOutcome> UnfenceAsync(
        IDmsTargetLockHolder holder,
        DmsTargetDatabaseName target,
        DmsInstanceIdentity instance,
        CancellationToken cancellationToken
    ) => throw Unavailable();

    public Task<DmsIdentityRead> ReadIdentityOnHolderAsync(
        IDmsTargetLockHolder holder,
        DmsTargetDatabaseName target,
        CancellationToken cancellationToken
    ) => throw Unavailable();

    public Task<DmsCommandOutcome> DropInstanceAsync(
        IDmsTargetLockHolder holder,
        DmsTargetDatabaseName target,
        DmsInstanceIdentity instance,
        DmsDropReason reason,
        CancellationToken cancellationToken
    ) => throw Unavailable();

    public Task<DmsCommandOutcome> DropStagingAsync(
        DmsDatabaseName staging,
        DmsInstanceIdentity stagingInstance,
        CancellationToken cancellationToken
    ) => throw Unavailable();

    public Task<DmsSessionTermination> TerminateSessionAsync(
        DmsServerSessionId session,
        TimeSpan deadline,
        CancellationToken cancellationToken
    ) => throw Unavailable();

    private static InvalidOperationException Unavailable() =>
        new(
            "No DMS target engine adapter exists for the configured provider; the provisioner is not ready and must not reach the engine."
        );
}
