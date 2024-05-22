// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using DotNet.Testcontainers.Builders;

namespace EdFi.DataManagementService.Api.Tests.E2E.Management;

public class ContainerSetup
{
    public async Task<string> SetupDataManagement()
    {
        var network = new NetworkBuilder().Build();

        string apiImageName = "local/edfi-data-management-service";
        string dbImageName = "local/edfi-data-management-postgresql";

        // Image needs to be previously built
        var apiContaner = new ContainerBuilder()
            .WithImage(apiImageName)
            .WithPortBinding(8080)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(8080)))
            .WithNetwork(network)
            .Build();

        var dbContainer = new ContainerBuilder()
        .WithImage(dbImageName)
        .WithPortBinding(5432, 5432)
        .WithNetwork(network)
        .WithNetworkAliases("dmsdb")
        .WithWaitStrategy(Wait.ForUnixContainer().UntilPortIsAvailable(5432))
        .Build();

        await network.CreateAsync().ConfigureAwait(false);
        await Task.WhenAll(apiContaner.StartAsync(), dbContainer.StartAsync()).ConfigureAwait(false);

        return new UriBuilder(
            Uri.UriSchemeHttp,
            apiContaner.Hostname,
            apiContaner.GetMappedPublicPort(8080)
        ).ToString();
    }
}
