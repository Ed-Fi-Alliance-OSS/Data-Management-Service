// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using EdFi.DmsConfigurationService.Backend.ManagedDataStores;
using EdFi.DmsConfigurationService.Backend.Postgresql.DataStoreProvisioning;
using Npgsql;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.ManagedDataStores;

public sealed class PostgresqlManagedRuntimeConnectionProvider : IManagedDataStoreRuntimeConnectionProvider
{
    public string Provider => "postgresql";

    public bool TryParse(string snapshot, [NotNullWhen(true)] out ManagedDataStoreEndpoint? endpoint) =>
        PostgresqlManagedEndpoint.TryParse(snapshot, out endpoint);

    public DbConnectionStringBuilder CreateBuilder(string snapshot) =>
        new NpgsqlConnectionStringBuilder(snapshot);
}
