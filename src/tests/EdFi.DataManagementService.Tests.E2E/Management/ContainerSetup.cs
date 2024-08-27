// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Tests.E2E.Management;

public class ContainerSetup
{
    public static IContainer? DbContainer;
    public static IContainer? ApiContainer;

    public static async Task<string> SetupDataManagement()
    {
        var network = new NetworkBuilder().Build();

        // Images need to be previously built
        string apiImageName = "local/data-management-service";
        string dbImageName = "postgres:16.3-alpine3.20";

        var pgAdminUser = "postgres";
        var pgAdminPassword = "P@ssw0rd";
        var dbContainerName = "dmsdb";
        var connectionString =
            $"host=dmsdb;port=5432;username={pgAdminUser};password={pgAdminPassword};database=edfi_datamanagementservice;";
        var httpPort = 8987;

        var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Trace).AddDebug().AddConsole();
        });

        DbContainer = new ContainerBuilder()
            .WithImage(dbImageName)
            .WithPortBinding(5404, 5432)
            .WithNetwork(network)
            .WithNetworkAliases(dbContainerName)
            .WithEnvironment("POSTGRES_PASSWORD", pgAdminPassword)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilPortIsAvailable(5432))
            .WithLogger(loggerFactory.CreateLogger("dbContainer"))
            .Build();

        ApiContainer = new ContainerBuilder()
            .WithImage(apiImageName)
            .WithPortBinding(httpPort)
            .WithEnvironment("ASPNETCORE_HTTP_PORTS", httpPort.ToString())
            .WithEnvironment("NEED_DATABASE_SETUP", "true")
            .WithEnvironment("DATABASE_CONNECTION_STRING", connectionString)
            .WithEnvironment("DATABASE_CONNECTION_STRING_ADMIN", connectionString)
            .WithEnvironment("LOG_LEVEL", "Debug")
            .WithEnvironment("OAUTH_TOKEN_ENDPOINT", "http://127.0.0.1:8080/oauth/token")
            .WithEnvironment("BYPASS_STRING_COERCION", "false")
            .WithEnvironment("CORRELATION_ID_HEADER", "correlationid")
            .WithEnvironment("DATABASE_ISOLATION_LEVEL", "RepeatableRead")
            .WithEnvironment("ALLOW_IDENTITY_UPDATE_OVERRIDES", "")
            .WithEnvironment("FAILURE_RATIO", "0.1")
            .WithEnvironment("SAMPLING_DURATION_SECONDS", "10")
            .WithEnvironment("MINIMUM_THROUGHPUT", "2")
            .WithEnvironment("BREAK_DURATION_SECONDS", "30")
            .WithEnvironment("DMS_DATASTORE", "postgresql")
            .WithEnvironment("DMS_QUERYHANDLER", "postgresql")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort((ushort)httpPort)))
            .WithNetwork(network)
            .WithLogger(loggerFactory.CreateLogger("apiContainer"))
            .Build();

        await network.CreateAsync().ConfigureAwait(false);
        await Task.WhenAll(DbContainer.StartAsync(), ApiContainer.StartAsync()).ConfigureAwait(false);

        return new UriBuilder(
            Uri.UriSchemeHttp,
            ApiContainer.Hostname,
            ApiContainer.GetMappedPublicPort(httpPort)
        ).ToString();
    }
}
