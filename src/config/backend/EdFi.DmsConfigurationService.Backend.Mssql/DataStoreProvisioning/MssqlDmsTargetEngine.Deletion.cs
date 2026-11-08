// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using Microsoft.Data.SqlClient;

namespace EdFi.DmsConfigurationService.Backend.Mssql.DataStoreProvisioning;

/// <summary>
/// The owned delete of the SQL Server engine (6.6): the fence, the identity read under it, its reversal and the owned
/// drop, each on the holder session inside its execution gate. The fence (<c>SET SINGLE_USER WITH ROLLBACK IMMEDIATE</c>)
/// ends every session on the database and can keep none, so the caller closes and proves gone its own session before
/// it. The holder that set single-user mode keeps a shared database lock, and with it the database's one slot, until it
/// sets multi-user mode again or ends: while the fence stands no other session can open the target, so the holder reads
/// the identity itself through a three-part name, and drops the target from the same session. The exclusivity
/// observation is bounded by <c>SessionTerminationTimeout</c>, the statements by <c>CommandTimeout</c>.
/// </summary>
public sealed partial class MssqlDmsTargetEngine
{
    /// <summary>
    /// Test seam: replaces each exclusivity observation of the fence (index 0 for the first, right after
    /// <c>SET SINGLE_USER</c> returned). While the holder lives no other session can join the fenced database, so a
    /// session still listed can only be simulated.
    /// </summary>
    internal Func<int, MssqlFenceObservation, MssqlFenceObservation>? FenceObservationHook { get; init; }

    /// <summary>
    /// On the holder session: single-user mode on <paramref name="target"/>, guarded by <paramref name="instance"/>,
    /// then proof that no other session uses it (D-22). SQL Server cannot keep a session through the fence, so
    /// <paramref name="keep"/> must be null.
    /// </summary>
    public async Task<DmsCommandOutcome> FenceAsync(
        IDmsTargetLockHolder holder,
        DmsTargetDatabaseName target,
        DmsInstanceIdentity instance,
        DmsServerSessionId? keep,
        CancellationToken cancellationToken
    )
    {
        MssqlTargetLockSession session = HolderOf(holder);
        DmsInstanceIdentity.Mssql verified = MssqlInstanceOf(instance, nameof(instance));
        if (keep is not null)
        {
            throw new ArgumentException(
                "SET SINGLE_USER WITH ROLLBACK IMMEDIATE ends every session; the SQL Server fence keeps none.",
                nameof(keep)
            );
        }

        return await session.RunAsync(
            token =>
                MssqlDeletionQueries.FenceAsync(
                    session.Connection,
                    target,
                    verified,
                    session.Session,
                    _settings.SessionTerminationTimeout,
                    _adminConnection.CommandTimeoutSeconds,
                    _timeProvider,
                    FenceObservationHook,
                    token
                ),
            _adminConnection.CommandTimeout + _settings.SessionTerminationTimeout,
            cancellationToken
        );
    }

    /// <summary>On the holder session: multi-user mode again on <paramref name="target"/>, guarded by <paramref name="instance"/>.</summary>
    public async Task<DmsCommandOutcome> UnfenceAsync(
        IDmsTargetLockHolder holder,
        DmsTargetDatabaseName target,
        DmsInstanceIdentity instance,
        CancellationToken cancellationToken
    )
    {
        MssqlTargetLockSession session = HolderOf(holder);
        DmsInstanceIdentity.Mssql verified = MssqlInstanceOf(instance, nameof(instance));
        return await session.RunAsync(
            token =>
                MssqlDeletionQueries.UnfenceAsync(
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

    /// <summary>On the holder session: the owned drop (6.6 step 5) of our fenced instance.</summary>
    private async Task<DmsCommandOutcome> DropOwnedTargetAsync(
        MssqlTargetLockSession session,
        DmsTargetDatabaseName target,
        DmsInstanceIdentity.Mssql instance,
        CancellationToken cancellationToken
    ) =>
        await session.RunAsync(
            token =>
                MssqlDeletionQueries.DropTargetAsync(
                    session.Connection,
                    target,
                    instance,
                    session.Session,
                    _adminConnection.CommandTimeoutSeconds,
                    token
                ),
            _adminConnection.CommandTimeout,
            cancellationToken
        );

    /// <summary>
    /// On the holder session, under its fence: the identity read through a three-part name, because the holder holds the
    /// single-user slot and no other session can open the target (6.6 SQL Server step 4). A missing table and a refusal
    /// for lack of a privilege are typed reads, as in <see cref="ReadIdentityAsync"/>; anything else propagates.
    /// </summary>
    public async Task<DmsIdentityRead> ReadIdentityOnHolderAsync(
        IDmsTargetLockHolder holder,
        DmsTargetDatabaseName target,
        CancellationToken cancellationToken
    )
    {
        MssqlTargetLockSession session = HolderOf(holder);
        return await session.RunAsync(
            async token =>
            {
                List<string?> values;
                try
                {
                    values = await ReadRowsAsync(
                        session.Connection,
                        MssqlInventoryQueries.SourceIdentityRowsIn(target),
                        reader => reader.IsDBNull(0) ? null : reader.GetString(0),
                        token
                    );
                }
                catch (SqlException exception) when (MssqlErrorClassifier.IsMissingRelation(exception.Number))
                {
                    return new DmsIdentityRead.Invalid(DmsIdentityReadProblem.TableMissing);
                }
                catch (SqlException exception) when (MssqlErrorClassifier.IsAccessRefusal(exception.Number))
                {
                    return new DmsIdentityRead.Invalid(DmsIdentityReadProblem.AccessDenied)
                    {
                        Diagnostic = MssqlErrorClassifier.Diagnostic(exception, ReadIdentityOperation),
                    };
                }

                return values.Count switch
                {
                    0 => new DmsIdentityRead.Invalid(DmsIdentityReadProblem.NoRows),
                    1 => ParseIdentity(values[0]),
                    _ => new DmsIdentityRead.Invalid(DmsIdentityReadProblem.MultipleRows),
                };
            },
            _adminConnection.CommandTimeout,
            cancellationToken
        );
    }
}
