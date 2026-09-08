// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Backend;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Backend.Mssql;

/// <summary>
/// How the request-path SQL Server seams reach the acquisition boundary. Held in one place so the
/// relational command executor, the document hydrator, and the write-session factory cannot drift into
/// three slightly different ways of turning the resolved data store into an open connection.
/// </summary>
internal static class MssqlSeamConnection
{
    /// <summary>
    /// Opens a leased connection against the target this request selected, with acquisition guarded.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The target is read from request-scoped state rather than rebuilt from the parent, so a request
    /// routed to a snapshot or a read replica reaches that database through every seam. Reading it
    /// throws when no target was selected; there is deliberately no fallback to the parent, because
    /// serving the primary to a request that asked for a derivative is the failure this design exists
    /// to prevent.
    /// </para>
    /// <para>
    /// The boundary spans taking the lease as well as the open, because the connection string is
    /// parsed twice before the open - once realizing the derivative's effective string, once in the
    /// SqlConnection constructor - so a provider-invalid string fails at one of those rather than at
    /// the open. Command execution stays outside it.
    /// </para>
    /// <para>
    /// The write-session factory opens through here too, and is unaffected rather than excluded: a
    /// mutation always selects the primary, and the guard wraps only a snapshot, so session creation
    /// still raises a DbException for the write-failure mapper to route. That contract is pinned by its
    /// own test rather than left to the condition.
    /// </para>
    /// </remarks>
    public static Task<MssqlLeasedConnection> OpenAsync(
        IDataStoreSelection dataStoreSelection,
        IMssqlConnectionAcquisition acquisition,
        ILogger logger,
        CancellationToken cancellationToken
    )
    {
        EffectiveDataStoreTarget target = dataStoreSelection.GetEffectiveTarget();

        return ConnectionAcquisition.GuardAsync(
            () => MssqlLeasedConnection.OpenAsync(acquisition, target, cancellationToken),
            target.Kind,
            MssqlConnectionAcquisitionFailure.IsExpected,
            logger,
            cancellationToken
        );
    }
}
