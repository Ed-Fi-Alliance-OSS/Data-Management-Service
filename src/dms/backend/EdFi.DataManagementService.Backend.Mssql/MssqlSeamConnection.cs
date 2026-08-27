// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Configuration;

namespace EdFi.DataManagementService.Backend.Mssql;

/// <summary>
/// How the request-path SQL Server seams reach the acquisition boundary. Held in one place so the
/// relational command executor, the document hydrator, and the write-session factory cannot drift into
/// three slightly different ways of turning the resolved data store into an open connection.
/// </summary>
internal static class MssqlSeamConnection
{
    /// <summary>
    /// Opens a leased connection against the target this request selected.
    /// </summary>
    /// <remarks>
    /// The target is read from request-scoped state rather than rebuilt from the parent, so a request
    /// routed to a snapshot or a read replica reaches that database through every seam. Reading it
    /// throws when no target was selected; there is deliberately no fallback to the parent, because
    /// serving the primary to a request that asked for a derivative is the failure this design exists
    /// to prevent.
    /// </remarks>
    public static Task<MssqlLeasedConnection> OpenAsync(
        IDataStoreSelection dataStoreSelection,
        IMssqlConnectionAcquisition acquisition,
        CancellationToken cancellationToken
    ) =>
        MssqlLeasedConnection.OpenAsync(
            acquisition,
            dataStoreSelection.GetEffectiveTarget(),
            cancellationToken
        );
}
