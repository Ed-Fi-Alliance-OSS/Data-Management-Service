// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using EdFi.DataManagementService.Backend.Ddl;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Integration;

internal static class CdcGeneratedSchemaFixture
{
    private const string DatabaseName = "edfi_datastore";

    public static async Task<CdcConnectorTemplateRequest> CreateRequestAsync(
        CdcConnectorTemplatePinnedImageFixture fixture,
        CdcStateProviderOperations provider,
        CancellationToken token
    )
    {
        int port = await ReadMappedProviderPortAsync(fixture, provider, token);
        await ProvisionGeneratedDmsSchemaAsync(port, provider, token);
        return await fixture.CreateRequestAsync(token, generatedSchema: true);
    }

    private static async Task ProvisionGeneratedDmsSchemaAsync(
        int providerPort,
        CdcStateProviderOperations providerOperations,
        CancellationToken cancellationToken
    )
    {
        await providerOperations.ResetDatabaseAsync(providerPort, DatabaseName, cancellationToken);
        await using DbConnection connection = providerOperations.OpenConnection(
            providerOperations.ProviderAdminConnectionString(providerPort, DatabaseName)
        );
        await connection.OpenAsync(cancellationToken);
        await ExecuteSqlScriptAsync(
            connection,
            providerOperations.FullDdl,
            providerOperations,
            cancellationToken
        );
        await ExecuteSqlAsync(connection, providerOperations.SetTrackingLifecycleSql, cancellationToken);
        await ExecuteSqlAsync(connection, providerOperations.UpsertSourceIdentitySql, cancellationToken);
    }

    public static async Task<int> ReadMappedProviderPortAsync(
        CdcConnectorTemplatePinnedImageFixture pinnedFixture,
        CdcStateProviderOperations providerOperations,
        CancellationToken cancellationToken
    )
    {
        string providerContainerName =
            $"{pinnedFixture.KafkaBootstrapServers.Split(':', 2)[0][..^"-broker".Length]}-provider";
        string containerPort =
            providerOperations.Provider == CdcProvider.Postgresql ? "5432/tcp" : "1433/tcp";
        DockerCommandResult result = await new DockerCli().RunAsync(
            ["port", providerContainerName, containerPort],
            cancellationToken
        );
        string endpoint = result.StandardOutput.Trim();
        return int.Parse(
            endpoint[(endpoint.LastIndexOf(':') + 1)..],
            System.Globalization.CultureInfo.InvariantCulture
        );
    }

    private static async Task ExecuteSqlScriptAsync(
        DbConnection connection,
        string sql,
        CdcStateProviderOperations providerOperations,
        CancellationToken cancellationToken
    )
    {
        foreach (string batch in providerOperations.SplitDdlBatches(sql))
        {
            await ExecuteSqlAsync(connection, batch, cancellationToken);
        }
    }

    private static async Task ExecuteSqlAsync(
        DbConnection connection,
        string sql,
        CancellationToken cancellationToken
    )
    {
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
