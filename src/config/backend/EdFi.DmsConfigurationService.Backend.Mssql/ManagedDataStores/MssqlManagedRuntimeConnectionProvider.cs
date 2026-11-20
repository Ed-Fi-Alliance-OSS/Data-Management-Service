// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using EdFi.DmsConfigurationService.Backend.ManagedDataStores;
using EdFi.DmsConfigurationService.Backend.Mssql.DataStoreProvisioning;
using Microsoft.Data.SqlClient;

namespace EdFi.DmsConfigurationService.Backend.Mssql.ManagedDataStores;

public sealed class MssqlManagedRuntimeConnectionProvider : IManagedDataStoreRuntimeConnectionProvider
{
    public string Provider => "mssql";

    public bool TryParse(string snapshot, [NotNullWhen(true)] out ManagedDataStoreEndpoint? endpoint) =>
        MssqlManagedEndpoint.TryParse(snapshot, out endpoint);

    public DbConnectionStringBuilder CreateBuilder(string snapshot) =>
        new SqlConnectionStringBuilder(snapshot);
}
