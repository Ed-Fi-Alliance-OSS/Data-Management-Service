// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning.Packaging;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning;

/// <summary>
/// An engine that records every call with the database name it addresses, scripts the members the
/// orchestrator reaches through a create (lock acquisition, server facts, inspection, staging create and
/// drop, restore, session open and reads, termination, rename, marker, compensation drop) and a delete
/// (fence, unfence, owned drop), and refuses every other member after recording it, so a test can prove
/// which calls were and were not issued and in what order.
/// </summary>
internal sealed class SpyDmsTargetEngine : IDmsTargetEngine
{
    public const string AcquireTargetLock = "AcquireTargetLock";
    public const string ReadServerFacts = "ReadServerFacts";
    public const string Inspect = "Inspect";
    public const string CreateStaging = "CreateStaging";
    public const string RestoreIntoStaging = "RestoreIntoStaging";
    public const string DropStaging = "DropStaging";
    public const string OpenSession = "OpenSession";
    public const string ReadIdentity = "ReadIdentity";
    public const string ReadTargetFacts = "ReadTargetFacts";
    public const string TerminateSession = "TerminateSession";
    public const string RenameIntoTarget = "RenameIntoTarget";
    public const string AssignMarker = "AssignMarker";
    public const string DropInstance = "DropInstance";
    public const string Fence = "Fence";
    public const string Unfence = "Unfence";
    public const string ReadIdentityOnHolder = "ReadIdentityOnHolder";

    public sealed record Call(string Member, string? DatabaseName);

    private readonly Lock _gate = new();
    private readonly List<Call> _calls = [];
    private readonly List<DmsServerSessionId> _terminated = [];
    private readonly List<string> _restoreTargets = [];

    /// <summary><c>postgresql</c> unless a test drives the SQL Server branch of the orchestrator.</summary>
    public string Provider { get; set; } = DmsDataStoreProvisioningOptions.PostgresqlProvider;

    /// <summary>The target name each restore was handed (never addressed; SQL Server compares the backup's recorded name with it).</summary>
    public IReadOnlyList<string> RestoreTargets
    {
        get
        {
            lock (_gate)
            {
                return [.. _restoreTargets];
            }
        }
    }

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

    /// <summary>Invoked synchronously in the caller's flow for every recorded call, after it is recorded.</summary>
    public Action<string>? OnCall { get; set; }

    public Func<CancellationToken, Task<DmsTargetLockResult>> OnAcquireTargetLock { get; set; } =
        _ => throw NotExpected(AcquireTargetLock);

    public Func<CancellationToken, Task<DmsServerFacts>> OnReadServerFacts { get; set; } =
        _ => Task.FromResult(Facts());

    public Func<CancellationToken, Task<DmsLiveTargetState>> OnInspect { get; set; } =
        _ => throw NotExpected(Inspect);

    /// <summary>Scripted per name: the staging name and the target name are inspected at different points.</summary>
    public Func<DmsDatabaseName, CancellationToken, Task<DmsLiveTargetState>>? OnInspectByName { get; set; }

    public delegate Task<DmsCommandOutcome<DmsInstanceIdentity>> CreateStagingScript(
        DmsDatabaseName staging,
        DmsProvisionClaim claim,
        CancellationToken cancellationToken
    );

    public delegate Task<DmsCommandOutcome<DmsInstanceIdentity>> RestoreScript(
        DmsDatabaseName staging,
        StagedArtifact artifact,
        CancellationToken cancellationToken
    );

    public delegate Task<DmsCommandOutcome> DropStagingScript(
        DmsDatabaseName staging,
        DmsInstanceIdentity stagingInstance,
        CancellationToken cancellationToken
    );

    public delegate Task<DmsSessionOpenResult> OpenSessionScript(
        DmsDatabaseName name,
        DmsInstanceIdentity expectedInstance,
        CancellationToken cancellationToken
    );

    public delegate Task<DmsSessionTermination> TerminateScript(
        DmsServerSessionId session,
        TimeSpan deadline,
        CancellationToken cancellationToken
    );

    public delegate Task<DmsCommandOutcome<DmsInstanceIdentity>> RenameScript(
        DmsDatabaseName staging,
        DmsInstanceIdentity stagingInstance,
        DmsTargetDatabaseName target,
        CancellationToken cancellationToken
    );

    public delegate Task<DmsCommandOutcome> AssignMarkerScript(
        DmsSession session,
        Guid expectedSourceIdentity,
        CancellationToken cancellationToken
    );

    public delegate Task<DmsCommandOutcome> DropInstanceScript(
        DmsTargetDatabaseName target,
        DmsInstanceIdentity instance,
        DmsDropReason reason,
        CancellationToken cancellationToken
    );

    public delegate Task<DmsCommandOutcome> FenceScript(
        DmsTargetDatabaseName target,
        DmsInstanceIdentity instance,
        DmsServerSessionId? keep,
        CancellationToken cancellationToken
    );

    public delegate Task<DmsCommandOutcome> UnfenceScript(
        DmsTargetDatabaseName target,
        DmsInstanceIdentity instance,
        CancellationToken cancellationToken
    );

    public delegate Task<DmsIdentityRead> HolderIdentityScript(
        DmsTargetDatabaseName target,
        CancellationToken cancellationToken
    );

    public HolderIdentityScript OnReadIdentityOnHolder { get; set; } =
        (_, _) => throw NotExpected(ReadIdentityOnHolder);

    public CreateStagingScript OnCreateStaging { get; set; } = (_, _, _) => throw NotExpected(CreateStaging);

    public RestoreScript OnRestoreIntoStaging { get; set; } =
        (_, _, _) => throw NotExpected(RestoreIntoStaging);

    public DropStagingScript OnDropStaging { get; set; } = (_, _, _) => throw NotExpected(DropStaging);

    public OpenSessionScript OnOpenSession { get; set; } = (_, _, _) => throw NotExpected(OpenSession);

    public Func<DmsSession, CancellationToken, Task<DmsIdentityRead>> OnReadIdentity { get; set; } =
        (_, _) => throw NotExpected(ReadIdentity);

    public Func<DmsSession, CancellationToken, Task<DmsTargetFacts>> OnReadTargetFacts { get; set; } =
        (_, _) => throw NotExpected(ReadTargetFacts);

    public TerminateScript OnTerminateSession { get; set; } =
        (_, _, _) => throw NotExpected(TerminateSession);

    public RenameScript OnRenameIntoTarget { get; set; } =
        (_, _, _, _) => throw NotExpected(RenameIntoTarget);

    public AssignMarkerScript OnAssignMarker { get; set; } = (_, _, _) => throw NotExpected(AssignMarker);

    public DropInstanceScript OnDropInstance { get; set; } = (_, _, _, _) => throw NotExpected(DropInstance);

    public FenceScript OnFence { get; set; } = (_, _, _, _) => throw NotExpected(Fence);

    public UnfenceScript OnUnfence { get; set; } = (_, _, _) => throw NotExpected(Unfence);

    /// <summary>The server session id of every termination request, in order.</summary>
    public IReadOnlyList<DmsServerSessionId> TerminatedSessions
    {
        get
        {
            lock (_gate)
            {
                return [.. _terminated];
            }
        }
    }

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
        return OnInspectByName is { } byName ? byName(name, cancellationToken) : OnInspect(cancellationToken);
    }

    public Task<DmsSessionOpenResult> OpenSessionAsync(
        DmsDatabaseName name,
        DmsInstanceIdentity expectedInstance,
        CancellationToken cancellationToken
    )
    {
        Record(OpenSession, name.Value);
        return OnOpenSession(name, expectedInstance, cancellationToken);
    }

    public Task<DmsIdentityRead> ReadIdentityAsync(DmsSession session, CancellationToken cancellationToken)
    {
        Record(ReadIdentity, null);
        return OnReadIdentity(session, cancellationToken);
    }

    public Task<DmsTargetFacts> ReadTargetFactsAsync(DmsSession session, CancellationToken cancellationToken)
    {
        Record(ReadTargetFacts, null);
        return OnReadTargetFacts(session, cancellationToken);
    }

    public Task<DmsCommandOutcome<DmsInstanceIdentity>> CreateStagingAsync(
        IDmsTargetLockHolder holder,
        DmsDatabaseName staging,
        DmsProvisionClaim claim,
        CancellationToken cancellationToken
    )
    {
        Record(CreateStaging, staging.Value);
        return OnCreateStaging(staging, claim, cancellationToken);
    }

    public Task<DmsCommandOutcome<DmsInstanceIdentity>> RestoreIntoStagingAsync(
        DmsDatabaseName staging,
        DmsTargetDatabaseName target,
        StagedArtifact artifact,
        DmsOperationId operation,
        CancellationToken cancellationToken
    )
    {
        Record(RestoreIntoStaging, staging.Value);
        lock (_gate)
        {
            _restoreTargets.Add(target.Value);
        }

        return OnRestoreIntoStaging(staging, artifact, cancellationToken);
    }

    public Task<DmsCommandOutcome<DmsInstanceIdentity>> RenameIntoTargetAsync(
        IDmsTargetLockHolder holder,
        DmsDatabaseName staging,
        DmsInstanceIdentity stagingInstance,
        DmsTargetDatabaseName target,
        CancellationToken cancellationToken
    )
    {
        Record(RenameIntoTarget, target.Value);
        return OnRenameIntoTarget(staging, stagingInstance, target, cancellationToken);
    }

    public Task<DmsCommandOutcome> AssignMarkerAsync(
        DmsSession session,
        Guid expectedSourceIdentity,
        CancellationToken cancellationToken
    )
    {
        Record(AssignMarker, null);
        return OnAssignMarker(session, expectedSourceIdentity, cancellationToken);
    }

    public Task<DmsCommandOutcome> FenceAsync(
        IDmsTargetLockHolder holder,
        DmsTargetDatabaseName target,
        DmsInstanceIdentity instance,
        DmsServerSessionId? keep,
        CancellationToken cancellationToken
    )
    {
        Record(Fence, target.Value);
        return OnFence(target, instance, keep, cancellationToken);
    }

    public Task<DmsCommandOutcome> UnfenceAsync(
        IDmsTargetLockHolder holder,
        DmsTargetDatabaseName target,
        DmsInstanceIdentity instance,
        CancellationToken cancellationToken
    )
    {
        Record(Unfence, target.Value);
        return OnUnfence(target, instance, cancellationToken);
    }

    public Task<DmsIdentityRead> ReadIdentityOnHolderAsync(
        IDmsTargetLockHolder holder,
        DmsTargetDatabaseName target,
        CancellationToken cancellationToken
    )
    {
        Record(ReadIdentityOnHolder, target.Value);
        return OnReadIdentityOnHolder(target, cancellationToken);
    }

    public Task<DmsCommandOutcome> DropInstanceAsync(
        IDmsTargetLockHolder holder,
        DmsTargetDatabaseName target,
        DmsInstanceIdentity instance,
        DmsDropReason reason,
        CancellationToken cancellationToken
    )
    {
        Record(DropInstance, target.Value);
        return OnDropInstance(target, instance, reason, cancellationToken);
    }

    public Task<DmsCommandOutcome> DropStagingAsync(
        DmsDatabaseName staging,
        DmsInstanceIdentity stagingInstance,
        CancellationToken cancellationToken
    )
    {
        Record(DropStaging, staging.Value);
        return OnDropStaging(staging, stagingInstance, cancellationToken);
    }

    public Task<DmsSessionTermination> TerminateSessionAsync(
        DmsServerSessionId session,
        TimeSpan deadline,
        CancellationToken cancellationToken
    )
    {
        Record(TerminateSession, null);
        lock (_gate)
        {
            _terminated.Add(session);
        }

        return OnTerminateSession(session, deadline, cancellationToken);
    }

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

        OnCall?.Invoke(member);
    }

    private static InvalidOperationException NotExpected(string member) =>
        new($"The orchestrator shell must not call {member}.");
}
