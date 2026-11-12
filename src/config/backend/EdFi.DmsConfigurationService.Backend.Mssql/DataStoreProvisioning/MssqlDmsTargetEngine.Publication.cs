// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;

namespace EdFi.DmsConfigurationService.Backend.Mssql.DataStoreProvisioning;

/// <summary>
/// The publication stage of the SQL Server engine. The rename and the compensation drop run
/// on the holder session inside its execution gate, bounded by <c>CommandTimeout</c>; the rename returns the instance
/// with its <c>create_date</c> re-captured, because the rename changes it, so every later guard (the marker
/// session's open, the marker's resolution, the compensation drop) compares the post-rename snapshot. The marker runs
/// on the instance-bound session with its instance predicate. The owned delete is in the deletion partial.
/// </summary>
public sealed partial class MssqlDmsTargetEngine
{
    public async Task<DmsCommandOutcome<DmsInstanceIdentity>> RenameIntoTargetAsync(
        IDmsTargetLockHolder holder,
        DmsDatabaseName staging,
        DmsInstanceIdentity stagingInstance,
        DmsTargetDatabaseName target,
        CancellationToken cancellationToken
    )
    {
        MssqlTargetLockSession session = HolderOf(holder);
        DmsInstanceIdentity.Mssql instance = MssqlInstanceOf(stagingInstance, nameof(stagingInstance));
        return await session.RunAsync(
            token =>
                MssqlPublicationQueries.RenameAsync(
                    session.Connection,
                    staging,
                    instance,
                    target,
                    session.Session,
                    _adminConnection.CommandTimeoutSeconds,
                    token
                ),
            _adminConnection.CommandTimeout,
            cancellationToken
        );
    }

    public async Task<DmsCommandOutcome> AssignMarkerAsync(
        DmsSession session,
        Guid expectedSourceIdentity,
        CancellationToken cancellationToken
    )
    {
        MssqlDmsSession bound = RequireSession(session);
        DmsInstanceIdentity.Mssql instance = MssqlInstanceOf(bound.Instance, nameof(session));
        return await MssqlPublicationQueries.AssignMarkerAsync(
            bound.Connection,
            instance,
            expectedSourceIdentity,
            bound.ServerSession,
            _adminConnection.CommandTimeoutSeconds,
            cancellationToken
        );
    }

    /// <summary>
    /// On the holder session: the compensation drop of the renamed target, guarded by its instance, or the owned
    /// delete's drop, which also requires the target to be fenced.
    /// </summary>
    public async Task<DmsCommandOutcome> DropInstanceAsync(
        IDmsTargetLockHolder holder,
        DmsTargetDatabaseName target,
        DmsInstanceIdentity instance,
        DmsDropReason reason,
        CancellationToken cancellationToken
    )
    {
        MssqlTargetLockSession session = HolderOf(holder);
        DmsInstanceIdentity.Mssql verified = MssqlInstanceOf(instance, nameof(instance));
        if (reason == DmsDropReason.Delete)
        {
            return await DropOwnedTargetAsync(session, target, verified, cancellationToken);
        }

        if (reason != DmsDropReason.Compensation)
        {
            throw new ArgumentOutOfRangeException(nameof(reason));
        }

        return await session.RunAsync(
            token =>
                MssqlPublicationQueries.DropInstanceAsync(
                    session.Connection,
                    target,
                    verified,
                    session.Session,
                    _adminConnection.CommandTimeoutSeconds,
                    token
                ),
            _adminConnection.CommandTimeout,
            cancellationToken
        );
    }

    private static MssqlTargetLockSession HolderOf(IDmsTargetLockHolder holder)
    {
        RequireHolder(holder);
        return (MssqlTargetLockSession)holder;
    }

    /// <summary>A verified SQL Server instance; a reservation or another engine's identity is a caller error.</summary>
    private static DmsInstanceIdentity.Mssql MssqlInstanceOf(
        DmsInstanceIdentity instance,
        string parameterName
    )
    {
        return instance as DmsInstanceIdentity.Mssql
            ?? throw new ArgumentException(
                "The SQL Server engine publishes only verified SQL Server instances.",
                parameterName
            );
    }
}
