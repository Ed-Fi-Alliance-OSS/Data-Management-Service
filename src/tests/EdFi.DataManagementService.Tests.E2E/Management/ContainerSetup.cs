// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using DotNet.Testcontainers.Builders;

namespace EdFi.DataManagementService.Tests.E2E.Management;

public class ContainerSetup
{
    public async Task<string> SetupDataManagement()
    {
        var network = new NetworkBuilder().Build();

        // Images need to be previously built
        string apiImageName = "local/edfi-data-management-service";
        string dbImageName = "postgres:16.3-alpine3.20";

        var dbUserName = "postgres";
        var dbPassword = "P@ssw0rd";
        var dbContainerName = "dmsdb";

        var dbContainer = new ContainerBuilder()
            .WithImage(dbImageName)
            .WithPortBinding(5432, 5432)
            .WithNetwork(network)
            .WithNetworkAliases(dbContainerName)
            .WithEnvironment("POSTGRES_PASSWORD", dbPassword)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilPortIsAvailable(5432))
            .Build();

        var apiContainer = new ContainerBuilder()
            .WithImage(apiImageName)
            .WithPortBinding(8080)
            .WithEnvironment("NEED_DATABASE_SETUP", "true")
            .WithEnvironment("POSTGRES_ADMIN_USER", dbUserName)
            .WithEnvironment("POSTGRES_ADMIN_PASSWORD", dbPassword)
            .WithEnvironment("POSTGRES_PASSWORD", dbPassword)
            .WithEnvironment("POSTGRES_USER", dbUserName)
            .WithEnvironment("POSTGRES_PORT", "5432")
            .WithEnvironment("POSTGRES_HOST", dbContainerName)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(8080)))
            .WithNetwork(network)
            .Build();

        await network.CreateAsync().ConfigureAwait(false);
        await Task.WhenAll(dbContainer.StartAsync(), apiContainer.StartAsync()).ConfigureAwait(false);

        return new UriBuilder(
            Uri.UriSchemeHttp,
            apiContainer.Hostname,
            apiContainer.GetMappedPublicPort(8080)
        ).ToString();
    }
}
