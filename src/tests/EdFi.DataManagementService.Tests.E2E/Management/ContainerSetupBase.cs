// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace EdFi.DataManagementService.Tests.E2E.Management;

public abstract class ContainerSetupBase
{
    private const string ApiImageName = "local/data-management-service";
    private const string DbImageName = "postgres:16.3-alpine3.20";

    private const string PgAdminUser = "postgres";
    private const string PgAdminPassword = "abcdefgh1!";
    private const string DbContainerName = "dms-postgresql";

    private const ushort HttpPort = 8987;
    private const ushort DbPortExternal = 5435;
    private const ushort DbPortInternal = 5432;
    private const string DatabaseName = "edfi_datamanagementservice";

    protected static string InternalConnectionString
    {
        get
        {
            return $"host={DbContainerName};port={DbPortInternal};username={PgAdminUser};password={PgAdminPassword};database={DatabaseName};";
        }
    }

    public static IContainer ApiContainer(
        string queryHandler,
        ILoggerFactory? loggerFactory,
        INetwork network,
        string? openSearchURl = ""
    ) =>
        new ContainerBuilder()
            .WithImage(ApiImageName)
            .WithPortBinding(HttpPort)
            .WithEnvironment("ASPNETCORE_HTTP_PORTS", HttpPort.ToString())
            .WithEnvironment("OAUTH_TOKEN_ENDPOINT", $"http://127.0.0.1:{HttpPort}/oauth/token")
            .WithEnvironment("NEED_DATABASE_SETUP", "true")
            .WithEnvironment("BYPASS_STRING_COERCION", "false")
            .WithEnvironment("LOG_LEVEL", "Debug")
            .WithEnvironment("DMS_DATASTORE", "postgresql")
            .WithEnvironment("DMS_QUERYHANDLER", queryHandler)
            .WithEnvironment("DATABASE_CONNECTION_STRING", InternalConnectionString)
            .WithEnvironment("DATABASE_CONNECTION_STRING_ADMIN", InternalConnectionString)
            .WithEnvironment("DATABASE_ISOLATION_LEVEL", "RepeatableRead")
            .WithEnvironment("CORRELATION_ID_HEADER", "correlationid")
            .WithEnvironment("FAILURE_RATIO", "0.1")
            .WithEnvironment("SAMPLING_DURATION_SECONDS", "10")
            .WithEnvironment("MINIMUM_THROUGHPUT", "2")
            .WithEnvironment("BREAK_DURATION_SECONDS", "30")
            .WithEnvironment("OPENSEARCH_URL", openSearchURl)
            .WithWaitStrategy(
                Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(HttpPort))
            )
            .WithNetwork(network)
            .WithLogger(loggerFactory!.CreateLogger("apiContainer"))
            .Build();

    public static IContainer DatabaseContainer(ILoggerFactory? loggerFactory, INetwork network)
    {
        var containerBuilder = new ContainerBuilder()
            .WithImage(DbImageName)
            .WithHostname(DbContainerName)
            .WithPortBinding(DbPortExternal, DbPortInternal)
            .WithNetwork(network)
            .WithNetworkAliases(DbContainerName)
            .WithEnvironment("POSTGRES_USER", PgAdminUser)
            .WithEnvironment("POSTGRES_PASSWORD", PgAdminPassword)
            .WithEnvironment("POSTGRES_DB_NAME", DatabaseName)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilPortIsAvailable(DbPortInternal))
            .WithLogger(loggerFactory!.CreateLogger("dbContainer"));

        return containerBuilder.Build();
    }

    public abstract Task StartContainers();

    public abstract Task ResetData();

    public abstract Task ApiLogs(TestLogger logger);

    public abstract string ApiUrl();

    public static async Task ResetDatabase()
    {
        var hostConnectionString =
            $"host=localhost;port={DbPortExternal};username={PgAdminUser};password={PgAdminPassword};database={DatabaseName};";
        using var conn = new NpgsqlConnection(hostConnectionString);
        await conn.OpenAsync();

        await DeleteData("dms.Reference");
        await DeleteData("dms.Alias");
        await DeleteData("dms.Document");

        async Task DeleteData(string tableName)
        {
            var deleteRefCmd = new NpgsqlCommand($"DELETE FROM {tableName};", conn);
            await deleteRefCmd.ExecuteNonQueryAsync();
        }
    }
}
