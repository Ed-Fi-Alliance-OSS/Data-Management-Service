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

        await network.CreateAsync().ConfigureAwait(false);

        return new UriBuilder(Uri.UriSchemeHttp, "localhost", 8080).ToString();
    }
}
