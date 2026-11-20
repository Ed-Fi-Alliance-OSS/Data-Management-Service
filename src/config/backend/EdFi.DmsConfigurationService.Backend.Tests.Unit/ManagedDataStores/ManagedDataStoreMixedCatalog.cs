// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.Globalization;
using Dapper;
using EdFi.DmsConfigurationService.Backend.Deploy;
using EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning.Packaging;
using Microsoft.Data.SqlClient;
using Npgsql;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.ManagedDataStores;

/// <summary>Owns only the fresh alternate-provider CMS database used by the mixed live cases.</summary>
public sealed class ManagedDataStoreMixedCatalog : IAsyncDisposable
{
    private readonly bool _mssql;
    private readonly string _name = "dms1439_mixed_" + Guid.NewGuid().ToString("N");
    private readonly string _maintenance;
    private bool _created;
    public string Provider => _mssql ? "mssql" : "postgresql";
    public string ConnectionString { get; }

    private ManagedDataStoreMixedCatalog(bool mssql, string admin)
    {
        _mssql = mssql;
        if (mssql)
        {
            var builder = new SqlConnectionStringBuilder(admin)
            {
                InitialCatalog = "master",
                Pooling = false,
            };
            _maintenance = builder.ConnectionString;
            builder.InitialCatalog = _name;
            ConnectionString = builder.ConnectionString;
        }
        else
        {
            var builder = new NpgsqlConnectionStringBuilder(admin) { Database = "postgres", Pooling = false };
            _maintenance = builder.ConnectionString;
            builder.Database = _name;
            ConnectionString = builder.ConnectionString;
        }
    }

    public static async Task<ManagedDataStoreMixedCatalog> CreateAsync(bool mssql)
    {
        string setting = mssql ? "ConnectionStrings__MssqlDmsAdmin" : "ConnectionStrings__PostgresqlDmsAdmin";
        string? admin = Environment.GetEnvironmentVariable(setting);
        if (string.IsNullOrWhiteSpace(admin))
        {
            DmsProvisioningLaneRequirement.SkipLocallyOrFailInCi(
                $"{setting} is required for mixed CMS qualification."
            );
        }
        var catalog = new ManagedDataStoreMixedCatalog(mssql, admin);
        try
        {
            await using var connection = catalog.Connection(catalog._maintenance);
            await connection.OpenAsync();
            // A collision refuses creation; it never adopts or later drops an existing database.
            await connection.ExecuteAsync($"CREATE DATABASE \"{catalog._name}\";");
            catalog._created = true;
            static bool Through41(string script)
            {
                const string segment = ".Deploy.Scripts.";
                int start = script.IndexOf(segment, StringComparison.Ordinal) + segment.Length;
                return int.Parse(script.AsSpan(start, 4), CultureInfo.InvariantCulture) <= 41;
            }
            IDatabaseDeploy deploy = mssql
                ? new Mssql.Deploy.DatabaseDeploy { ScriptFilter = Through41 }
                : new Postgresql.Deploy.DatabaseDeploy { ScriptFilter = Through41 };
            if (
                deploy.DeployDatabase(catalog.ConnectionString)
                is DatabaseDeployResult.DatabaseDeployFailure failure
            )
            {
                // Raw provider exceptions can include credentials/client-derived values.
                throw new InvalidOperationException(
                    "Mixed CMS deployment failed: " + failure.Error.GetType().Name
                );
            }
            return catalog;
        }
        catch
        {
            await catalog.DisposeAsync();
            throw;
        }
    }

    private DbConnection Connection(string connectionString) =>
        _mssql ? new SqlConnection(connectionString) : new NpgsqlConnection(connectionString);

    public async Task<DbConnection> OpenAsync()
    {
        var connection = Connection(ConnectionString);
        try
        {
            await connection.OpenAsync();
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!_created)
        {
            return;
        }
        await using var connection = Connection(_maintenance);
        await connection.OpenAsync();
        await connection.ExecuteAsync(
            _mssql
                ? $"ALTER DATABASE [{_name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{_name}];"
                : $"DROP DATABASE \"{_name}\" WITH (FORCE);"
        );
        _created = false;
    }
}
