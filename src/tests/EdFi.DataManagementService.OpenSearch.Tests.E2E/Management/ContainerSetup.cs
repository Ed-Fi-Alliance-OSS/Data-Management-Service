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
        string apiImageName = "local/edfi-data-management-service";
        string dbImageName = "postgres:16.3-alpine3.20";

        var dbUserName = "postgres";
        var dbPassword = "P@ssw0rd";
        var dbContainerName = "dmsdb";

        var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder
                .SetMinimumLevel(LogLevel.Trace)
                .AddDebug()
                .AddConsole();
        });

        DbContainer = new ContainerBuilder()
            .WithImage(dbImageName)
            .WithPortBinding(5404, 5432)
            .WithNetwork(network)
            .WithNetworkAliases(dbContainerName)
            .WithEnvironment("POSTGRES_PASSWORD", dbPassword)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilPortIsAvailable(5432))
            .WithLogger(loggerFactory.CreateLogger("dbContainer"))
            .Build();

        ApiContainer = new ContainerBuilder()
            .WithImage(apiImageName)
            .WithPortBinding(8080)
            .WithEnvironment("NEED_DATABASE_SETUP", "true")
            .WithEnvironment("POSTGRES_ADMIN_USER", dbUserName)
            .WithEnvironment("POSTGRES_ADMIN_PASSWORD", dbPassword)
            .WithEnvironment("POSTGRES_PASSWORD", dbPassword)
            .WithEnvironment("POSTGRES_USER", dbUserName)
            .WithEnvironment("POSTGRES_PORT", "5432")
            .WithEnvironment("POSTGRES_HOST", dbContainerName)
            .WithEnvironment("LOG_LEVEL", "Debug")
            .WithEnvironment("OAUTH_TOKEN_ENDPOINT", "http://localhost/oauth/token")
            .WithEnvironment("BYPASS_STRING_COERCION", "false")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(8080)))
            .WithNetwork(network)
            .WithLogger(loggerFactory.CreateLogger("apiContainer"))
            .Build();

        await network.CreateAsync().ConfigureAwait(false);
        await Task.WhenAll(DbContainer.StartAsync(), ApiContainer.StartAsync()).ConfigureAwait(false);

        return new UriBuilder(
            Uri.UriSchemeHttp,
            ApiContainer.Hostname,
            ApiContainer.GetMappedPublicPort(8080)
        ).ToString();
    }
}
