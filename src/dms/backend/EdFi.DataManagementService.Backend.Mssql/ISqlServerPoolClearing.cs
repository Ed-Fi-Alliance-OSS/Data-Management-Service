// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Microsoft.Data.SqlClient;

namespace EdFi.DataManagementService.Backend.Mssql;

/// <summary>
/// Clears one exact SqlClient connection pool, named by the provider-effective connection string that
/// identifies it.
///
/// This is an interface so the exact-pool contract is assertable without a server, and so that the
/// backend has no reachable way to clear anything but one exact pool: clearing unrelated pools would
/// disrupt targets that are still configured and in use. Integration fixtures do call
/// <c>SqlConnection.ClearAllPools</c> when tearing a leased database down, which is safe there and is
/// why the guarantee is stated as reachability from the backend rather than as absence everywhere.
/// </summary>
public interface ISqlServerPoolClearing
{
    void ClearPool(string effectiveConnectionString);
}

/// <inheritdoc />
public sealed class SqlClientPoolClearing : ISqlServerPoolClearing
{
    public void ClearPool(string effectiveConnectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(effectiveConnectionString);

        using SqlConnection connection = new(effectiveConnectionString);
        SqlConnection.ClearPool(connection);
    }
}
