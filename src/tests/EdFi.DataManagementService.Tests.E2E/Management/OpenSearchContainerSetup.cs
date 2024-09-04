// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using OpenSearch.Client;

namespace EdFi.DataManagementService.Tests.E2E.Management;

public class OpenSearchContainerSetup : ContainerSetupBase
{
    public override async Task StartContainers()
    {
        await Task.Delay(10);
    }

    public override async Task ApiLogs(TestLogger logger)
    {
        await Task.Delay(10);
    }

    public override string ApiUrl()
    {
        return "http://localhost:8080/";
    }

    public override async Task ResetData()
    {
        await ResetOpenSearch();
        await ResetDatabase();
    }

    private async Task ResetOpenSearch()
    {
        OpenSearchClient openSearchClient = new();
        var indices = openSearchClient.Cat.Indices();

        foreach (var index in indices.Records.Where(x => x.Index.Contains("ed-fi")))
        {
            await openSearchClient.Indices.DeleteAsync(index.Index);
        }
    }
}
