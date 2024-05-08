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
        string imageName = "local/edfi-data-management-service";

        // Image needs to be previously built
        var dockerImage = new ContainerBuilder().WithImage(imageName).WithPortBinding(8080).Build();

        await dockerImage.StartAsync();

        return new UriBuilder(
            Uri.UriSchemeHttp,
            dockerImage.Hostname,
            dockerImage.GetMappedPublicPort(8080)
        ).ToString();
    }
}
