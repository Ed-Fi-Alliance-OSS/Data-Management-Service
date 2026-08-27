// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Backend;

namespace EdFi.DataManagementService.Backend.Mssql;

/// <summary>
/// How the request-path SQL Server seams reach the acquisition boundary. Held in one place so the
/// relational command executor, the document hydrator, and the write-session factory cannot drift into
/// three slightly different ways of turning the resolved data store into an open connection.
/// </summary>
internal static class MssqlSeamConnection
{
    /// <summary>
    /// Builds the target for the resolved data store and opens a leased connection against it.
    /// </summary>
    /// <remarks>
    /// The target is constructed here, at the acquisition call boundary, rather than read from
    /// request-scoped state, because nothing yet records a per-request effective target. It is
    /// deliberately explicit: it names the parent's own database and is not a fallback for a target
    /// that failed to resolve.
    /// </remarks>
    public static Task<MssqlLeasedConnection> OpenAsync(
        IDataStoreSelection dataStoreSelection,
        IMssqlConnectionAcquisition acquisition,
        CancellationToken cancellationToken
    )
    {
        DataStore selectedDataStore = dataStoreSelection.GetSelectedDataStore();
        string? connectionString = selectedDataStore.ConnectionString;

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                $"Selected data store '{selectedDataStore.Id}' does not have a valid connection string."
            );
        }

        return MssqlLeasedConnection.OpenAsync(
            acquisition,
            EffectiveDataStoreTarget.Primary(connectionString),
            cancellationToken
        );
    }
}
