// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.Jobs;
using EdFi.DmsConfigurationService.Backend.Postgresql.Repositories;
using EdFi.DmsConfigurationService.Backend.Repositories;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.Tests.Integration;

public partial class Given_data_store_namespace_reservations
{
    [Test]
    public async Task It_refuses_disabled_management_with_ready_provisioning_without_changing_catalog_or_jobs()
    {
        var (id, _) = await SeedDeleteAcceptance(); // Includes a published store and its trusted artifact pin.
        using var services = AcceptanceServices();
        var enqueuer = services.GetRequiredService<IJobEnqueuer>();
        DataStoreManageRepository disabled = new(
            Options.Create(
                new DatabaseOptions
                {
                    DatabaseConnection = _database.ConnectionString,
                    EncryptionKey = "unused",
                }
            ),
            CreateTenantContext(null),
            CreateReadiness(),
            enqueuer,
            ProtectedDatabaseNames.Create("cms", []),
            Repository(),
            JobClaimPolicy.ExcludeManagedDataStores
        );
        string before = await DeleteAcceptanceSnapshot();
        (await disabled.AcceptCreateAsync(CreateCommand("Suppressed"), default))
            .Should()
            .BeOfType<DataStoreManageCreateResult.ManagementDisabled>();
        (await disabled.AcceptDeleteAsync(id, default))
            .Should()
            .BeOfType<DataStoreManageDeleteResult.ManagementDisabled>();
        (await disabled.AcceptDeleteAsync(int.MaxValue, default))
            .Should()
            .BeOfType<DataStoreManageDeleteResult.ManagementDisabled>();
        (await DeleteAcceptanceSnapshot()).Should().Be(before);

        // Re-enabling can reserve the previously refused name/target and delete the same pinned aggregate.
        var enabled = ManagedRepository(enqueuer);
        (await enabled.AcceptCreateAsync(CreateCommand("Suppressed"), default))
            .Should()
            .BeOfType<DataStoreManageCreateResult.Success>();
        (await enabled.AcceptDeleteAsync(id, default))
            .Should()
            .BeOfType<DataStoreManageDeleteResult.Success>();
    }
}
