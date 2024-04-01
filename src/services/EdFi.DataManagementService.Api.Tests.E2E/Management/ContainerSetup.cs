// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using DotNet.Testcontainers.Builders;
using Microsoft.Extensions.Configuration;

namespace EdFi.DataManagementService.Api.Tests.E2E.Management;

public class ContainerSetup
{
    private readonly TestLogger _logger;

    public ContainerSetup(TestLogger logger)
    {
        _logger = logger;
    }

    public async Task<string> SetupDataManagement(IConfiguration configuration)
    {
        string imageName = "local/edfi-data-management-service";

        // Environment variables
        var issuer = configuration.GetValue<string>("AppSettings:Authentication:Issuer");
        var authority = configuration.GetValue<string>("AppSettings:Authentication:Authority");
        var signingKeyValue = configuration.GetValue<string>("AppSettings:Authentication:SigningKey");

        var environmentList = new Dictionary<string, string>
        {
            { "AppSettings__Authentication__SigningKey", signingKeyValue! },
            { "AppSettings__Authentication__Authority", authority! },
            { "AppSettings__Authentication__Issuer", issuer! }
        };

        // Image needs to be previously built
        var dockerImage = new ContainerBuilder()
            .WithImage(imageName)
            .WithEnvironment(environmentList)
            .WithPortBinding(8080)
            .Build();

        await dockerImage.StartAsync();
        var logs = await dockerImage.GetLogsAsync();
        _logger.log.Information(imageName, logs);

        return new UriBuilder(
            Uri.UriSchemeHttp,
            dockerImage.Hostname,
            dockerImage.GetMappedPublicPort(8080)
        ).ToString();
    }
}
