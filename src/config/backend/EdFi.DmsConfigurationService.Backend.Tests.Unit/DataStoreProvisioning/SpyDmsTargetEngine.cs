// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning.Packaging;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning;

/// <summary>
/// An engine that records every call with the database name it addresses, scripts the three members the
/// orchestrator shell reaches (lock acquisition, server facts, inspection), and refuses every other
/// member after recording it, so a test can prove which calls were and were not issued.
/// </summary>
internal sealed class SpyDmsTargetEngine : IDmsTargetEngine
{
    public const string AcquireTargetLock = "AcquireTargetLock";
    public const string ReadServerFacts = "ReadServerFacts";
    public const string Inspect = "Inspect";

    public sealed record Call(string Member, string? DatabaseName);

    private readonly Lock _gate = new();
    private readonly List<Call> _calls = [];

    public string Provider => "postgresql";

    public IReadOnlyList<Call> Calls
    {
        get
        {
            lock (_gate)
            {
                return [.. _calls];
            }
        }
    }

    public IReadOnlyList<string> Members => [.. Calls.Select(call => call.Member)];

    public Func<CancellationToken, Task<DmsTargetLockResult>> OnAcquireTargetLock { get; set; } =
        _ => throw NotExpected(AcquireTargetLock);

    public Func<CancellationToken, Task<DmsServerFacts>> OnReadServerFacts { get; set; } =
        _ => Task.FromResult(Facts());

    public Func<CancellationToken, Task<DmsLiveTargetState>> OnInspect { get; set; } =
        _ => throw NotExpected(Inspect);

    public static DmsServerFacts Facts() =>
        new(
            "PostgreSQL 18.1",
            18,
            "UTF8",
            null,
            null,
            new DateTimeOffset(2026, 11, 5, 12, 0, 0, TimeSpan.Zero)
        );

    public Task<DmsEngineEnvironment> ValidateEnvironmentAsync(CancellationToken cancellationToken) =>
        Refuse<DmsEngineEnvironment>(nameof(ValidateEnvironmentAsync), null);

    public Task<DmsServerFacts> ReadServerFactsAsync(CancellationToken cancellationToken)
    {
        Record(ReadServerFacts, null);
        return OnReadServerFacts(cancellationToken);
    }

    public Task<DmsTargetLockResult> AcquireTargetLockAsync(
        DmsTargetDatabaseName name,
        DmsOperationId operation,
        CancellationToken cancellationToken
    )
    {
        Record(AcquireTargetLock, name.Value);
        return OnAcquireTargetLock(cancellationToken);
    }

    public Task<DmsLiveTargetState> InspectAsync(DmsDatabaseName name, CancellationToken cancellationToken)
    {
        Record(Inspect, name.Value);
        return OnInspect(cancellationToken);
    }

    public Task<DmsSessionOpenResult> OpenSessionAsync(
        DmsDatabaseName name,
        DmsInstanceIdentity expectedInstance,
        CancellationToken cancellationToken
    ) => Refuse<DmsSessionOpenResult>(nameof(OpenSessionAsync), name.Value);

    public Task<DmsIdentityRead> ReadIdentityAsync(DmsSession session, CancellationToken cancellationToken) =>
        Refuse<DmsIdentityRead>(nameof(ReadIdentityAsync), null);

    public Task<DmsTargetFacts> ReadTargetFactsAsync(
        DmsSession session,
        CancellationToken cancellationToken
    ) => Refuse<DmsTargetFacts>(nameof(ReadTargetFactsAsync), null);

    public Task<DmsCommandOutcome<DmsInstanceIdentity>> CreateStagingAsync(
        IDmsTargetLockHolder holder,
        DmsDatabaseName staging,
        DmsProvisionClaim claim,
        CancellationToken cancellationToken
    ) => Refuse<DmsCommandOutcome<DmsInstanceIdentity>>(nameof(CreateStagingAsync), staging.Value);

    public Task<DmsCommandOutcome<DmsInstanceIdentity>> RestoreIntoStagingAsync(
        DmsDatabaseName staging,
        StagedArtifact artifact,
        DmsOperationId operation,
        CancellationToken cancellationToken
    ) => Refuse<DmsCommandOutcome<DmsInstanceIdentity>>(nameof(RestoreIntoStagingAsync), staging.Value);

    public Task<DmsCommandOutcome<DmsInstanceIdentity>> RenameIntoTargetAsync(
        IDmsTargetLockHolder holder,
        DmsDatabaseName staging,
        DmsInstanceIdentity stagingInstance,
        DmsTargetDatabaseName target,
        CancellationToken cancellationToken
    ) => Refuse<DmsCommandOutcome<DmsInstanceIdentity>>(nameof(RenameIntoTargetAsync), target.Value);

    public Task<DmsCommandOutcome> AssignMarkerAsync(
        DmsSession session,
        Guid expectedSourceIdentity,
        CancellationToken cancellationToken
    ) => Refuse<DmsCommandOutcome>(nameof(AssignMarkerAsync), null);

    public Task<DmsCommandOutcome> FenceAsync(
        IDmsTargetLockHolder holder,
        DmsTargetDatabaseName target,
        DmsInstanceIdentity instance,
        DmsServerSessionId? keep,
        CancellationToken cancellationToken
    ) => Refuse<DmsCommandOutcome>(nameof(FenceAsync), target.Value);

    public Task<DmsCommandOutcome> UnfenceAsync(
        IDmsTargetLockHolder holder,
        DmsTargetDatabaseName target,
        DmsInstanceIdentity instance,
        CancellationToken cancellationToken
    ) => Refuse<DmsCommandOutcome>(nameof(UnfenceAsync), target.Value);

    public Task<DmsCommandOutcome> DropInstanceAsync(
        IDmsTargetLockHolder holder,
        DmsTargetDatabaseName target,
        DmsInstanceIdentity instance,
        DmsDropReason reason,
        CancellationToken cancellationToken
    ) => Refuse<DmsCommandOutcome>(nameof(DropInstanceAsync), target.Value);

    public Task<DmsCommandOutcome> DropStagingAsync(
        DmsDatabaseName staging,
        DmsInstanceIdentity stagingInstance,
        CancellationToken cancellationToken
    ) => Refuse<DmsCommandOutcome>(nameof(DropStagingAsync), staging.Value);

    public Task<DmsSessionTermination> TerminateSessionAsync(
        DmsServerSessionId session,
        TimeSpan deadline,
        CancellationToken cancellationToken
    ) => Refuse<DmsSessionTermination>(nameof(TerminateSessionAsync), null);

    private Task<T> Refuse<T>(string member, string? databaseName)
    {
        Record(member, databaseName);
        throw NotExpected(member);
    }

    private void Record(string member, string? databaseName)
    {
        lock (_gate)
        {
            _calls.Add(new Call(member, databaseName));
        }
    }

    private static InvalidOperationException NotExpected(string member) =>
        new($"The orchestrator shell must not call {member}.");
}
