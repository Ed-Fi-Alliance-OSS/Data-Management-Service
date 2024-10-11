// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Tests.E2E.Management;

public class ContainerSetup : ContainerSetupBase
{
    public IContainer? DbContainer;
    public IContainer? DmsApiContainer;
    private readonly ushort httpPort = 8987;

    public override string ApiUrl()
    {
        if (DmsApiContainer is null)
        {
            throw new InvalidOperationException($"{nameof(DmsApiContainer)} has not been initialized.");
        }

        while (DmsApiContainer.State != TestcontainersStates.Running)
        {
            Thread.Sleep(1000);
        }

        return new UriBuilder(
            Uri.UriSchemeHttp,
            DmsApiContainer.Hostname,
            DmsApiContainer!.GetMappedPublicPort(httpPort)
        ).ToString();
    }

    public override async Task ApiLogs(TestLogger logger)
    {
        if (DmsApiContainer is null)
        {
            throw new InvalidOperationException($"{nameof(DmsApiContainer)} has not been initialized.");
        }

        var (stdout, stderr) = await DmsApiContainer.GetLogsAsync();
        logger.log.Information($"{Environment.NewLine}API stdout logs:{Environment.NewLine}{stdout}");

        if (!string.IsNullOrEmpty(stderr))
        {
            logger.log.Error($"{Environment.NewLine}API stderr logs:{Environment.NewLine}{stderr}");
        }
    }

    public override async Task ResetData()
    {
        await ResetDatabase();
    }

    public override async Task StartContainers()
    {
        var network = new NetworkBuilder().Build();

        var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Trace).AddDebug().AddConsole();
        });

        DbContainer = DatabaseContainer(loggerFactory, network);
        DmsApiContainer = ApiContainer("postgresql", loggerFactory, network);

        await network.CreateAsync().ConfigureAwait(false);
        await Task.WhenAll(DbContainer.StartAsync(), DmsApiContainer.StartAsync()).ConfigureAwait(false);
    }
}
