// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Reflection;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace EdFi.DataManagementService.Tests.E2E.Management;

public abstract class ContainerSetupBase
{
    private readonly string apiImageName = "local/data-management-service";
    private readonly string dbImageName = "postgres:16.3-alpine3.20";

    private readonly string pgAdminUser = "postgres";
    private readonly string pgAdminPassword = "abcdefgh1!";
    private readonly string dbContainerName = "dms-postgresql";
    private readonly string connectionString =
        "host=dms-postgresql;port=5432;username=postgres;password=abcdefgh1!;database=edfi_datamanagementservice;";
    private readonly ushort httpPort = 8987;
    private readonly string databaseName = "edfi_datamanagementservice";

    public IContainer ApiContainer(
        string queryHandler,
        ILoggerFactory? loggerFactory,
        INetwork network,
        string? openSearchURl = ""
    ) =>
        new ContainerBuilder()
            .WithImage(apiImageName)
            .WithPortBinding(httpPort)
            .WithEnvironment("ASPNETCORE_HTTP_PORTS", httpPort.ToString())
            .WithEnvironment("OAUTH_TOKEN_ENDPOINT", $"http://127.0.0.1:{httpPort}/oauth/token")
            .WithEnvironment("NEED_DATABASE_SETUP", "true")
            .WithEnvironment("BYPASS_STRING_COERCION", "false")
            .WithEnvironment("LOG_LEVEL", "Debug")
            .WithEnvironment("DMS_DATASTORE", "postgresql")
            .WithEnvironment("DMS_QUERYHANDLER", queryHandler)
            .WithEnvironment("DATABASE_CONNECTION_STRING", connectionString)
            .WithEnvironment("DATABASE_CONNECTION_STRING_ADMIN", connectionString)
            .WithEnvironment("DATABASE_ISOLATION_LEVEL", "RepeatableRead")
            .WithEnvironment("CORRELATION_ID_HEADER", "correlationid")
            .WithEnvironment("FAILURE_RATIO", "0.1")
            .WithEnvironment("SAMPLING_DURATION_SECONDS", "10")
            .WithEnvironment("MINIMUM_THROUGHPUT", "2")
            .WithEnvironment("BREAK_DURATION_SECONDS", "30")
            .WithEnvironment("OPENSEARCH_URL", openSearchURl)
            .WithWaitStrategy(
                Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort((ushort)httpPort))
            )
            .WithNetwork(network)
            .WithLogger(loggerFactory!.CreateLogger("apiContainer"))
            .Build();

    public IContainer DatabaseContainer(ILoggerFactory? loggerFactory, INetwork network)
    {
        var containerBuilder = new ContainerBuilder()
            .WithImage(dbImageName)
            .WithHostname(dbContainerName)
            .WithPortBinding(5435, 5432)
            .WithNetwork(network)
            .WithNetworkAliases(dbContainerName)
            .WithEnvironment("POSTGRES_USER", pgAdminUser)
            .WithEnvironment("POSTGRES_PASSWORD", pgAdminPassword)
            .WithEnvironment("POSTGRES_DB_NAME", databaseName)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilPortIsAvailable(5432))
            .WithLogger(loggerFactory!.CreateLogger("dbContainer"));

        return containerBuilder.Build();
    }

    public abstract Task StartContainers();

    public abstract Task ResetData();

    public abstract Task ApiLogs(TestLogger logger);

    public abstract string ApiUrl();

    public async Task ResetDatabase()
    {
        var hostConnectionString =
            "host=localhost;port=5435;username=postgres;password=abcdefgh1!;database=edfi_datamanagementservice;";
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
