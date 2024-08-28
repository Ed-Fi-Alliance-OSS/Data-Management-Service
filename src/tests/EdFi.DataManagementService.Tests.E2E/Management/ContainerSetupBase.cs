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
    private readonly string apiImageName = "local/edfi-data-management-service";
    private readonly string dbImageName = "postgres:16.3-alpine3.20";

    private readonly string pgAdminUser = "postgres";
    private readonly string pgAdminPassword = "abcdefgh1!";
    private readonly string dbContainerName = "dms-postgresql";
    private readonly string connectionString =
        "host=dms-postgresql;port=5432;username=postgres;password=abcdefgh1!;database=edfi_datamanagementservice;";

    public IContainer ApiContainer(
        string queryHandler,
        ILoggerFactory? loggerFactory,
        INetwork network,
        string? openSearchURl = ""
    ) =>
        new ContainerBuilder()
            .WithImage(apiImageName)
            .WithPortBinding(8080)
            .WithEnvironment("NEED_DATABASE_SETUP", "true")
            .WithEnvironment("DATABASE_CONNECTION_STRING", connectionString)
            .WithEnvironment("POSTGRES_ADMIN_USER", pgAdminUser)
            .WithEnvironment("POSTGRES_ADMIN_PASSWORD", pgAdminPassword)
            .WithEnvironment("POSTGRES_PORT", "5432")
            .WithEnvironment("POSTGRES_HOST", dbContainerName)
            .WithEnvironment("LOG_LEVEL", "Debug")
            .WithEnvironment("OAUTH_TOKEN_ENDPOINT", "http://127.0.0.1:8080/oauth/token")
            .WithEnvironment("BYPASS_STRING_COERCION", "false")
            .WithEnvironment("CORRELATION_ID_HEADER", "correlationid")
            .WithEnvironment("DATABASE_ISOLATION_LEVEL", "RepeatableRead")
            .WithEnvironment("FAILURE_RATIO", "0.1")
            .WithEnvironment("SAMPLING_DURATION_SECONDS", "10")
            .WithEnvironment("MINIMUM_THROUGHPUT", "2")
            .WithEnvironment("BREAK_DURATION_SECONDS", "30")
            .WithEnvironment("QUERY_HANDLER", queryHandler)
            .WithEnvironment("OPENSEARCH_URL", openSearchURl)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(8080)))
            .WithNetwork(network)
            .WithLogger(loggerFactory!.CreateLogger("apiContainer"))
            .Build();

    public IContainer DatabaseContainer(
        ILoggerFactory? loggerFactory,
        INetwork network,
        string? fileMountPath = null
    )
    {
        var containerBuilder = new ContainerBuilder()
            .WithImage(dbImageName)
            .WithHostname(dbContainerName)
            .WithPortBinding(5435, 5432)
            .WithNetwork(network)
            .WithNetworkAliases(dbContainerName)
            .WithEnvironment("POSTGRES_USER", pgAdminUser)
            .WithEnvironment("POSTGRES_PASSWORD", pgAdminPassword)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilPortIsAvailable(5432))
            .WithLogger(loggerFactory!.CreateLogger("dbContainer"));

        if (fileMountPath != null)
        {
            containerBuilder.WithBindMount(fileMountPath, "/docker-entrypoint-initdb.d/debezium_config.sh");
        }

        return containerBuilder.Build();
    }

    public async Task<string> ValidateApiContainer(IContainer apiContainer)
    {
        while (apiContainer!.State != TestcontainersStates.Running)
        {
            await Task.Delay(1000);
        }
        return new UriBuilder(
            Uri.UriSchemeHttp,
            apiContainer?.Hostname,
            apiContainer!.GetMappedPublicPort(8080)
        ).ToString();
    }

    public abstract Task StartContainers();

    public abstract Task ResetData();

    public abstract Task ApiLogs(TestLogger logger);

    public abstract Task<string> ApiUrl();

    public async Task ResetDatabase()
    {
        var hostConnectionString =
            "host=localhost;port=5435;username=postgres;password=abcdefgh1!;database=edfi_datamanagementservice;";
        using var conn = new NpgsqlConnection(hostConnectionString);
        await conn.OpenAsync();

        var deleteRefCmd = new NpgsqlCommand($"DELETE FROM dms.Reference;", conn);
        await deleteRefCmd.ExecuteNonQueryAsync();

        var deleteAliCmd = new NpgsqlCommand($"DELETE FROM dms.Alias;", conn);
        await deleteAliCmd.ExecuteNonQueryAsync();

        var deleteDocCmd = new NpgsqlCommand($"DELETE FROM dms.Document;", conn);
        await deleteDocCmd.ExecuteNonQueryAsync();
    }
}
