// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using EdFi.DmsConfigurationService.Backend.Services;
using Microsoft.Data.SqlClient;

namespace EdFi.DmsConfigurationService.Backend.Mssql;

/// <summary>
/// Reads a submitted data store connection string with Microsoft.Data.SqlClient's own parser, so what
/// the API accepts is what the SQL Server provider accepts.
/// </summary>
public class MssqlDataStoreConnectionStringValidator : DataStoreConnectionStringValidator
{
    protected override DbConnectionStringBuilder CreateBuilder(string connectionString) =>
        new SqlConnectionStringBuilder(connectionString);
}
