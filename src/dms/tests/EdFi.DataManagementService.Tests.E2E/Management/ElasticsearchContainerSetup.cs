// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Elastic.Clients.Elasticsearch;
using Elastic.Clients.Elasticsearch.IndexManagement;
using ElasticSearchIndices = Elastic.Clients.Elasticsearch.Indices;

namespace EdFi.DataManagementService.Tests.E2E.Management;

internal class ElasticsearchContainerSetup : ContainerSetup
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
        await ResetElasticsearch();
        await ResetDatabase();
    }

    private static async Task ResetElasticsearch()
    {
        ElasticsearchClient elasticsearchClient = new();

        var indices = await elasticsearchClient.Indices.GetAsync(
            new GetIndexRequest(ElasticSearchIndices.All)
        );

        foreach (var index in indices.Indices)
        {
            if (index.Key.ToString().Contains("ed-fi"))
            {
                await elasticsearchClient.Indices.DeleteAsync(index.Key);
            }
        }
    }
}
