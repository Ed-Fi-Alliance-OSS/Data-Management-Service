// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.Globalization;
using EdFi.DmsConfigurationService.Backend.Deploy;
using Microsoft.Data.SqlClient;
using Npgsql;

namespace EdFi.DmsConfigurationService.Tests.DmsProjectionE2E.Live;

/// <summary>One education organization of a data store's active snapshot.</summary>
public sealed record SnapshotRow(
    long EducationOrganizationId,
    string NameOfInstitution,
    string? ShortNameOfInstitution,
    string Discriminator,
    long? ParentId
);

/// <summary>A data store's active snapshot: its generation and its rows in identifier order.</summary>
public sealed record ActiveSnapshot(long Generation, IReadOnlyList<SnapshotRow> Rows);

/// <summary>
/// A Configuration Service database of its own, <c>dmscs_refresh_e2e_&lt;run&gt;</c>, on the server of the fixture's
/// route databases, deployed by the production <c>DatabaseDeploy</c> and holding the fixture's tenants and data stores
/// with the identifiers DMS knows them by. A refresh that runs against it reads the live DMS. The database is dropped
/// through <see cref="CleanupRegistry"/>. A server that refuses to create it fails the set-up; nothing is skipped.
/// </summary>
public sealed class ScratchCmsDatabase
{
    private const string SetupFailure = "Live refresh E2E setup failed";
    private const string HostPortName = "INSTANCE_E2E_DATABASE_HOST_PORT";

    private readonly bool _mssql;
    private readonly Dictionary<string, long> _tenantIds = new(StringComparer.Ordinal);

    private ScratchCmsDatabase(bool mssql, string name, string connectionString)
    {
        _mssql = mssql;
        Name = name;
        ConnectionString = connectionString;
    }

    public string Name { get; }

    /// <summary>Secret-bearing: never logged.</summary>
    public string ConnectionString { get; }

    public string Provider => _mssql ? "sqlserver" : "postgresql";

    public long TenantId(string tenant) => _tenantIds[tenant];

    /// <summary>
    /// Creates, deploys and seeds a database with every fixture tenant and route, each data store with its fixture
    /// identifier and its <c>districtId</c> and <c>schoolYear</c> contexts.
    /// </summary>
    public static async Task<ScratchCmsDatabase> CreateAsync(ProjectionE2EFixture fixture)
    {
        bool mssql = fixture.Environment.DatabaseEngine == "mssql";
        string name = $"dmscs_refresh_e2e_{Guid.NewGuid():N}"[..34];
        string server = ServerConnectionString(fixture.Environment.RouteTwoConnectionString, mssql);

        try
        {
            await ExecuteAsync(mssql, server, $"CREATE DATABASE {name};");
        }
        catch (DbException exception)
        {
            throw new InvalidOperationException(
                $"{SetupFailure}: the route database server refused to create {name} ({exception.GetType().Name}). "
                    + "The route credentials need permission to create databases.",
                exception
            );
        }

        fixture.Cleanup.Add(
            $"scratch Configuration Service database {name}",
            () => DropAsync(mssql, server, name)
        );

        ScratchCmsDatabase database = new(mssql, name, WithDatabase(server, name, mssql));
        DatabaseDeployResult deployed = mssql
            ? new Backend.Mssql.Deploy.DatabaseDeploy().DeployDatabase(database.ConnectionString)
            : new Backend.Postgresql.Deploy.DatabaseDeploy().DeployDatabase(database.ConnectionString);
        if (deployed is DatabaseDeployResult.DatabaseDeployFailure failure)
        {
            throw new InvalidOperationException(
                $"{SetupFailure}: deploying {name} failed ({failure.Error.GetType().Name}).",
                failure.Error
            );
        }

        foreach (string tenant in fixture.Environment.Routes.Select(route => route.Tenant).Distinct())
        {
            database._tenantIds[tenant] = await database.InsertTenantAsync(tenant);
        }

        foreach (FixtureRoute route in fixture.Environment.Routes)
        {
            await database.InsertDataStoreAsync(
                route.Tenant,
                route.DataStoreId,
                new Dictionary<string, string>
                {
                    ["districtId"] = route.DistrictId,
                    ["schoolYear"] = route.SchoolYear,
                }
            );
        }

        return database;
    }

    /// <summary>Adds a data store with the given identifier and contexts to the tenant.</summary>
    public async Task InsertDataStoreAsync(
        string tenant,
        int dataStoreId,
        IReadOnlyDictionary<string, string> contexts
    )
    {
        string insertDataStore = _mssql
            ? """
                SET IDENTITY_INSERT dmscs.DataStore ON;
                INSERT INTO dmscs.DataStore (Id, TenantId, Name, DataStoreType, Provider)
                VALUES (@Id, @TenantId, @Name, N'District', @Provider);
                SET IDENTITY_INSERT dmscs.DataStore OFF;
                """
            : """
                INSERT INTO dmscs."DataStore" ("Id", "TenantId", "Name", "DataStoreType", "Provider")
                OVERRIDING SYSTEM VALUE
                VALUES (@Id, @TenantId, @Name, 'District', @Provider);
                """;
        await ExecuteAsync(
            _mssql,
            ConnectionString,
            insertDataStore,
            ("@Id", dataStoreId),
            ("@TenantId", TenantId(tenant)),
            ("@Name", $"Live refresh {dataStoreId.ToString(CultureInfo.InvariantCulture)}"),
            ("@Provider", Provider)
        );

        foreach ((string key, string value) in contexts)
        {
            await ExecuteAsync(
                _mssql,
                ConnectionString,
                _mssql
                    ? "INSERT INTO dmscs.DataStoreContext (DataStoreId, ContextKey, ContextValue) VALUES (@Id, @Key, @Value);"
                    : """INSERT INTO dmscs."DataStoreContext" ("DataStoreId", "ContextKey", "ContextValue") VALUES (@Id, @Key, @Value);""",
                ("@Id", dataStoreId),
                ("@Key", key),
                ("@Value", value)
            );
        }
    }

    /// <summary>
    /// Publishes <paramref name="rows"/> as the data store's active snapshot, generation 1, as a completed refresh
    /// would have left it.
    /// </summary>
    public async Task PlantSnapshotAsync(string tenant, int dataStoreId, IReadOnlyList<SnapshotRow> rows)
    {
        await ExecuteAsync(
            _mssql,
            ConnectionString,
            _mssql
                ? """
                INSERT INTO dmscs.EducationOrganizationSnapshotTarget
                    (DataStoreId, TenantId, LastGeneration, ActiveGeneration, RefreshedAt, SourceContractVersion)
                VALUES (@Id, @TenantId, 1, 1, SYSUTCDATETIME(), N'1');
                """
                : """
                INSERT INTO dmscs."EducationOrganizationSnapshotTarget"
                    ("DataStoreId", "TenantId", "LastGeneration", "ActiveGeneration", "RefreshedAt",
                     "SourceContractVersion")
                VALUES (@Id, @TenantId, 1, 1, now() AT TIME ZONE 'UTC', '1');
                """,
            ("@Id", dataStoreId),
            ("@TenantId", TenantId(tenant))
        );

        foreach (SnapshotRow row in rows)
        {
            await ExecuteAsync(
                _mssql,
                ConnectionString,
                _mssql
                    ? """
                    INSERT INTO dmscs.EducationOrganizationSnapshot
                        (DataStoreId, Generation, TenantId, EducationOrganizationId, NameOfInstitution,
                         ShortNameOfInstitution, Discriminator, ParentId, RefreshedAt, SourceContractVersion)
                    VALUES (@Id, 1, @TenantId, @OrganizationId, @Name, @ShortName, @Discriminator, @ParentId,
                        SYSUTCDATETIME(), N'1');
                    """
                    : """
                    INSERT INTO dmscs."EducationOrganizationSnapshot"
                        ("DataStoreId", "Generation", "TenantId", "EducationOrganizationId", "NameOfInstitution",
                         "ShortNameOfInstitution", "Discriminator", "ParentId", "RefreshedAt",
                         "SourceContractVersion")
                    VALUES (@Id, 1, @TenantId, @OrganizationId, @Name, @ShortName, @Discriminator, @ParentId,
                        now() AT TIME ZONE 'UTC', '1');
                    """,
                ("@Id", dataStoreId),
                ("@TenantId", TenantId(tenant)),
                ("@OrganizationId", row.EducationOrganizationId),
                ("@Name", row.NameOfInstitution),
                ("@ShortName", row.ShortNameOfInstitution),
                ("@Discriminator", row.Discriminator),
                ("@ParentId", row.ParentId)
            );
        }
    }

    /// <summary>The data store's active snapshot, or null when none was ever published.</summary>
    public async Task<ActiveSnapshot?> ActiveSnapshotAsync(int dataStoreId)
    {
        await using DbConnection connection = await OpenAsync(_mssql, ConnectionString);
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = _mssql
            ? """
                SELECT t.ActiveGeneration, s.EducationOrganizationId, s.NameOfInstitution, s.ShortNameOfInstitution,
                    s.Discriminator, s.ParentId
                FROM dmscs.EducationOrganizationSnapshotTarget AS t
                LEFT JOIN dmscs.EducationOrganizationSnapshot AS s
                    ON s.DataStoreId = t.DataStoreId AND s.Generation = t.ActiveGeneration
                WHERE t.DataStoreId = @Id AND t.ActiveGeneration IS NOT NULL
                ORDER BY s.EducationOrganizationId;
                """
            : """
                SELECT t."ActiveGeneration", s."EducationOrganizationId", s."NameOfInstitution",
                    s."ShortNameOfInstitution", s."Discriminator", s."ParentId"
                FROM dmscs."EducationOrganizationSnapshotTarget" AS t
                LEFT JOIN dmscs."EducationOrganizationSnapshot" AS s
                    ON s."DataStoreId" = t."DataStoreId" AND s."Generation" = t."ActiveGeneration"
                WHERE t."DataStoreId" = @Id AND t."ActiveGeneration" IS NOT NULL
                ORDER BY s."EducationOrganizationId";
                """;
        AddParameter(command, "@Id", dataStoreId);
        await using DbDataReader reader = await command.ExecuteReaderAsync();

        long? generation = null;
        List<SnapshotRow> rows = [];
        while (await reader.ReadAsync())
        {
            generation = reader.GetInt64(0);
            if (!await reader.IsDBNullAsync(1))
            {
                rows.Add(
                    new SnapshotRow(
                        reader.GetInt64(1),
                        reader.GetString(2),
                        await reader.IsDBNullAsync(3) ? null : reader.GetString(3),
                        reader.GetString(4),
                        await reader.IsDBNullAsync(5) ? null : reader.GetInt64(5)
                    )
                );
            }
        }

        return generation is { } active ? new ActiveSnapshot(active, rows) : null;
    }

    private async Task<long> InsertTenantAsync(string tenant)
    {
        await using DbConnection connection = await OpenAsync(_mssql, ConnectionString);
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = _mssql
            ? "INSERT INTO dmscs.Tenant (Name) OUTPUT inserted.Id VALUES (@Name);"
            : """INSERT INTO dmscs."Tenant" ("Name") VALUES (@Name) RETURNING "Id";""";
        AddParameter(command, "@Name", tenant);
        return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The route connection string, which names the server as the Docker network reaches it, pointed at the host's
    /// published port of that server and at its system database.
    /// </summary>
    private static string ServerConnectionString(string routeConnectionString, bool mssql)
    {
        string portText = Environment.GetEnvironmentVariable(HostPortName) ?? "";
        if (!int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out int port))
        {
            throw new InvalidOperationException(
                $"{SetupFailure}: required environment variable '{HostPortName}' is not a port. Run this project "
                    + "through './build-dms.ps1 InstanceE2ETest', which exports it."
            );
        }

        return mssql
            ? new SqlConnectionStringBuilder(routeConnectionString)
            {
                DataSource = $"127.0.0.1,{port.ToString(CultureInfo.InvariantCulture)}",
                InitialCatalog = "master",
            }.ConnectionString
            : new NpgsqlConnectionStringBuilder(routeConnectionString)
            {
                Host = "127.0.0.1",
                Port = port,
                Database = "postgres",
            }.ConnectionString;
    }

    private static string WithDatabase(string serverConnectionString, string database, bool mssql) =>
        mssql
            ? new SqlConnectionStringBuilder(serverConnectionString)
            {
                InitialCatalog = database,
            }.ConnectionString
            : new NpgsqlConnectionStringBuilder(serverConnectionString)
            {
                Database = database,
            }.ConnectionString;

    private static async Task DropAsync(bool mssql, string server, string name)
    {
        if (mssql)
        {
            SqlConnection.ClearAllPools();
            await ExecuteAsync(
                mssql,
                server,
                $"IF DB_ID(N'{name}') IS NOT NULL BEGIN ALTER DATABASE {name} SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE {name}; END;"
            );
        }
        else
        {
            NpgsqlConnection.ClearAllPools();
            await ExecuteAsync(mssql, server, $"DROP DATABASE IF EXISTS {name} WITH (FORCE);");
        }
    }

    private static async Task ExecuteAsync(
        bool mssql,
        string connectionString,
        string sql,
        params (string Name, object? Value)[] parameters
    )
    {
        await using DbConnection connection = await OpenAsync(mssql, connectionString);
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = sql;
        foreach ((string name, object? value) in parameters)
        {
            AddParameter(command, name, value);
        }

        await command.ExecuteNonQueryAsync();
    }

    private static async Task<DbConnection> OpenAsync(bool mssql, string connectionString)
    {
        DbConnection connection = mssql
            ? new SqlConnection(connectionString)
            : new NpgsqlConnection(connectionString);
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

    private static void AddParameter(DbCommand command, string name, object? value)
    {
        DbParameter parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }
}
