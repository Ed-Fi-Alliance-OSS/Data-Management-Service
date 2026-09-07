// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.DocumentCache.Cdc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;
using PinnedImage = EdFi.DataManagementService.Backend.Cdc.Tests.Integration.CdcConnectorTemplatePinnedImageFixture;

namespace EdFi.DataManagementService.Backend.Cdc.Control.Tests.Integration;

internal sealed partial class CdcControlBrokerFixture
{
    internal IReadOnlyList<string> SharedWorkerTopics =>
        [OffsetStoreTopicName, $"{_resourcePrefix}.connect.configs", $"{_resourcePrefix}.connect.status"];

    internal ServiceProvider CreateRetirementServices(string stateRoot, bool timeout)
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?> { ["AppSettings:Datastore"] = "postgresql" }
            )
            .Build();
        // The timeout changes only the provider deletion boundary. Source validation, Connect,
        // Kafka, and durable state remain the shipped implementations against disposable services.
        CdcControlOptions options = WithControlOptions(options =>
            options.Timeouts.ProviderSetup = TimeSpan.FromSeconds(5)
        );
        ServiceCollection services = new();
        services
            .AddLogging()
            .AddDmsCdcControl(configuration)
            .AddSingleton<IOptions<CdcControlOptions>>(Options.Create(options))
            .Configure<CdcBindingStateStoreOptions>(options => options.RootPath = stateRoot)
            .AddSingleton<IDocumentCacheGuardedNewEmptyActivationCommand, ForbiddenActivation>();
        if (timeout)
        {
            services.AddSingleton<ICdcProviderArtifactTeardown, TimedOutProviderDeletion>();
        }
        return services.BuildServiceProvider();
    }

    internal CdcTargetOperationRequest RetirementRequest(bool connectorAlreadyAbsent) =>
        new(
            "retirement-retry",
            "",
            1,
            ProviderAdminConnectionString,
            new CdcProviderSetupInputs(
                "postgres",
                PinnedImage.ConnectorDatabaseUser,
                PinnedImage.BuildRequiredSourceTableInventory(Ddl.CdcProvider.Postgresql),
                PinnedImage.BuildDmsManagedTableInventory(Ddl.CdcProvider.Postgresql)
            )
        )
        {
            ConnectorAlreadyAbsent = connectorAlreadyAbsent,
        };

    internal async Task<int> ReadRetirementProviderArtifactCountAsync(CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = new(ProviderAdminConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using NpgsqlCommand command = new(
            "SELECT (SELECT count(*) FROM pg_publication WHERE pubname = @publication) + "
                + "(SELECT count(*) FROM pg_replication_slots WHERE slot_name = @slot)",
            connection
        );
        command.Parameters.AddWithValue("publication", Inventory.PostgresqlPublicationName!);
        command.Parameters.AddWithValue("slot", Inventory.PostgresqlLogicalSlotName!);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    private sealed class TimedOutProviderDeletion : ICdcProviderArtifactTeardown
    {
        public CdcProvider Provider => CdcProvider.Postgresql;

        public async Task<IReadOnlyList<CdcGovernedArtifact>> DeleteAsync(
            CdcProviderArtifactTeardownRequest request,
            CancellationToken cancellationToken = default
        )
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException(
                "The provider deletion budget must cancel this test boundary."
            );
        }
    }
}
