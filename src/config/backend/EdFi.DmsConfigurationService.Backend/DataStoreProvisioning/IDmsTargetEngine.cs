// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning.Packaging;

namespace EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;

/// <summary>
/// The engine port the orchestrator drives (spec 4.2). One adapter per target provider lives in the
/// provider project and is selected by <c>DmsDataStoreProvisioning:Provider</c>, independently of the CMS
/// catalog provider. Every mutating member returns a <see cref="DmsCommandOutcome"/> whose unknown case
/// names the session to prove terminated (D-7b); every selected-target mutation takes the lock holder
/// and executes on the holder session (D-12); every mutation re-verifies the instance it was given
/// immediately before executing (D-13). Read members throw on connection failure; the orchestrator
/// classifies the exception.
/// </summary>
public interface IDmsTargetEngine
{
    /// <summary><c>postgresql</c> or <c>mssql</c> (D-17).</summary>
    string Provider { get; }

    /// <summary>The engine's share of the readiness probes (7.3 items 4 to 7); no lock is held.</summary>
    Task<DmsEngineEnvironment> ValidateEnvironmentAsync(CancellationToken cancellationToken);

    /// <summary>Version, encoding or default paths, and server clock (6.1 step 5).</summary>
    Task<DmsServerFacts> ReadServerFactsAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Opens the dedicated holder session on the maintenance database and takes the session-level
    /// exclusive lock for <paramref name="name"/>, waiting at most <c>LockAcquireTimeout</c> (D-12). The
    /// current holder is never identified or terminated.
    /// </summary>
    Task<DmsTargetLockResult> AcquireTargetLockAsync(
        DmsTargetDatabaseName name,
        DmsOperationId operation,
        CancellationToken cancellationToken
    );

    /// <summary>What is under <paramref name="name"/> right now: absent, protected, fenced, or present with its instance identity.</summary>
    Task<DmsLiveTargetState> InspectAsync(DmsDatabaseName name, CancellationToken cancellationToken);

    /// <summary>
    /// Opens a dedicated connection to <paramref name="name"/>, verifies at open that it is still
    /// <paramref name="expectedInstance"/>, and captures the server session id. Refuses, keeping nothing open,
    /// when the name no longer resolves to that instance.
    /// </summary>
    Task<DmsSessionOpenResult> OpenSessionAsync(
        DmsDatabaseName name,
        DmsInstanceIdentity expectedInstance,
        CancellationToken cancellationToken
    );

    /// <summary><c>dms.DataStoreIdentity</c> with the delivered rule (<c>LIMIT 2</c> / <c>TOP (2)</c>).</summary>
    Task<DmsIdentityRead> ReadIdentityAsync(DmsSession session, CancellationToken cancellationToken);

    /// <summary>The catalog facts the validator judges (6.9c).</summary>
    Task<DmsTargetFacts> ReadTargetFactsAsync(DmsSession session, CancellationToken cancellationToken);

    /// <summary>
    /// On the holder session: PostgreSQL creates the staging database and writes <paramref name="claim"/> as
    /// its comment and returns the staging instance identity; SQL Server only reserves the name, because the
    /// restore creates the database, and returns a <see cref="DmsInstanceIdentity.MssqlReservation"/> naming the
    /// primary file the restored database will carry.
    /// </summary>
    Task<DmsCommandOutcome<DmsInstanceIdentity>> CreateStagingAsync(
        IDmsTargetLockHolder holder,
        DmsDatabaseName staging,
        DmsProvisionClaim claim,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Restores <paramref name="artifact"/> into the staging database on a work session addressable by
    /// <paramref name="operation"/> (<c>psql</c> replay or <c>RESTORE DATABASE … WITH MOVE</c>, never
    /// <c>REPLACE</c>), bounded by <c>RestoreTimeout</c>. Returns the staging instance identity (SQL Server:
    /// first captured here). <paramref name="target"/> is never addressed: SQL Server refuses a backup whose
    /// recorded database name equals it (D-11 hygiene).
    /// </summary>
    /// <param name="restoreStarting">
    /// Invoked once, after the engine's preparation and immediately before the restore itself is dispatched, so
    /// the caller can restart the step's deadline there and the preparation does not consume
    /// <c>RestoreTimeout</c>. SQL Server reports it before the <c>RESTORE</c>, after staging and planning the
    /// backup; PostgreSQL before the <c>psql</c> replay, after its directory, authentication and role checks. A
    /// call that fails before then does not report it.
    /// </param>
    Task<DmsCommandOutcome<DmsInstanceIdentity>> RestoreIntoStagingAsync(
        DmsDatabaseName staging,
        DmsTargetDatabaseName target,
        StagedArtifact artifact,
        DmsOperationId operation,
        CancellationToken cancellationToken,
        Action? restoreStarting = null
    );

    /// <summary>
    /// On the holder session: verifies <paramref name="stagingInstance"/> under <paramref name="staging"/>, then
    /// renames it atomically into <paramref name="target"/> (D-6). Fails with
    /// <see cref="DmsCommandFailureClass.DuplicateName"/> when any database holds the target name, leaving
    /// that database untouched. Returns the post-rename instance snapshot (SQL Server: with
    /// <c>create_date</c> re-captured).
    /// </summary>
    Task<DmsCommandOutcome<DmsInstanceIdentity>> RenameIntoTargetAsync(
        IDmsTargetLockHolder holder,
        DmsDatabaseName staging,
        DmsInstanceIdentity stagingInstance,
        DmsTargetDatabaseName target,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// On the instance-bound <paramref name="session"/>: assigns <paramref name="expectedSourceIdentity"/> to
    /// <c>dms.DataStoreIdentity.SourceIdentity</c> with an in-statement instance predicate, then re-reads it
    /// on the same session (6.9b). The last mutation of a create; the sole commit marker (D-7).
    /// </summary>
    Task<DmsCommandOutcome> AssignMarkerAsync(
        DmsSession session,
        Guid expectedSourceIdentity,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// On the holder session: disallows connections (PostgreSQL) or sets single user (SQL Server) on
    /// <paramref name="target"/>, terminates every other session with verification, and proves exclusivity
    /// (D-22). <paramref name="keep"/> is the one session that may stay (PostgreSQL only).
    /// </summary>
    Task<DmsCommandOutcome> FenceAsync(
        IDmsTargetLockHolder holder,
        DmsTargetDatabaseName target,
        DmsInstanceIdentity instance,
        DmsServerSessionId? keep,
        CancellationToken cancellationToken
    );

    /// <summary>On the holder session: reverses <see cref="FenceAsync"/>.</summary>
    Task<DmsCommandOutcome> UnfenceAsync(
        IDmsTargetLockHolder holder,
        DmsTargetDatabaseName target,
        DmsInstanceIdentity instance,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// On the holder session, under its fence: the delivered identity read of <paramref name="target"/> (6.6 SQL Server
    /// step 4). On SQL Server the session that set single-user mode holds the database's one slot until it reverses the
    /// fence, so no other session can read it; the holder reads it through a three-part name. Same outcomes as
    /// <see cref="ReadIdentityAsync"/>. PostgreSQL keeps its own session through the fence and does not support it.
    /// </summary>
    Task<DmsIdentityRead> ReadIdentityOnHolderAsync(
        IDmsTargetLockHolder holder,
        DmsTargetDatabaseName target,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// On the holder session: drops the selected target after re-verifying <paramref name="instance"/> under
    /// <paramref name="target"/>. Two callers: owned deletion (6.6 step 5), which by its nature drops a target
    /// found by inspection whose marker equals the expected identity, after fencing and verified exclusivity;
    /// and compensation (6.7), which is never issued on retry, on a target found by inspection, after a
    /// committed or unresolved marker, or after lock loss.
    /// </summary>
    Task<DmsCommandOutcome> DropInstanceAsync(
        IDmsTargetLockHolder holder,
        DmsTargetDatabaseName target,
        DmsInstanceIdentity instance,
        DmsDropReason reason,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Drops this operation's staging database on any session after re-verifying
    /// <paramref name="stagingInstance"/> under <paramref name="staging"/>; staging names are unique to the
    /// operation, so this is cleanup, allowed after lock loss (6.7).
    /// </summary>
    Task<DmsCommandOutcome> DropStagingAsync(
        DmsDatabaseName staging,
        DmsInstanceIdentity stagingInstance,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Terminates <paramref name="session"/> (matched by id and start time) and proves it gone within
    /// <paramref name="deadline"/> (D-22). An unknown outcome is resolved only after this returns a
    /// proven-gone verdict.
    /// </summary>
    Task<DmsSessionTermination> TerminateSessionAsync(
        DmsServerSessionId session,
        TimeSpan deadline,
        CancellationToken cancellationToken
    );
}
