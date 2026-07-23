// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.InstanceManagement.Tests.E2E.Management;
using Microsoft.Extensions.Logging;
using Reqnroll;
using Serilog;
using Serilog.Extensions.Logging;

namespace EdFi.InstanceManagement.Tests.E2E.Hooks;

[Binding]
public class InstanceManagementCleanupHooks(InstanceManagementContext context)
{
    private static ILogger<InstanceManagementCleanupHooks>? _logger;

    [BeforeTestRun]
    public static void InitializeLogger()
    {
        if (_logger == null)
        {
            var loggerFactory = new SerilogLoggerFactory(Log.Logger);
            _logger = loggerFactory.CreateLogger<InstanceManagementCleanupHooks>();
        }
    }

    [AfterScenario("@InstanceCleanup", Order = 1000)]
    public async Task CleanupInstanceResources()
    {
        _logger?.LogInformation("Starting instance cleanup");

        try
        {
            // Delete only records the scenario explicitly created, in dependency-safe order
            // (applications, then data stores, then vendors). Every deletion is independently guarded
            // against the immutable suite-owned fixture IDs so a hydrated fixture record is never deleted,
            // including a replacement claim-set application created under a fixture tenant.
            await DeleteScenarioOwnedAsync(
                context.ScenarioOwnedApplications,
                context.FixtureApplicationIds,
                "application",
                (client, id) => client.DeleteApplicationAsync(id)
            );
            await DeleteScenarioOwnedAsync(
                context.ScenarioOwnedDataStores,
                context.FixtureDataStoreIds,
                "data store",
                (client, id) => client.DeleteInstanceAsync(id)
            );
            await DeleteScenarioOwnedAsync(
                context.ScenarioOwnedVendors,
                context.FixtureVendorIds,
                "vendor",
                (client, id) => client.DeleteVendorAsync(id)
            );

            _logger?.LogInformation("Instance cleanup completed");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error during cleanup");
        }
        finally
        {
            // Reset context for next scenario (discards any replacement credentials so the next
            // @InstanceFixture scenario re-hydrates canonical fixture state).
            context.Reset();
        }
    }

    /// <summary>
    /// Selects the scenario-owned records that may be deleted: every record whose id is not an immutable
    /// suite-owned fixture id, newest first. Ownership is never inferred from tenant name, so a scenario-owned
    /// replacement application created under a fixture tenant is selected while the fixture application is not.
    /// </summary>
    internal static IReadOnlyList<OwnedRecord> SelectDeletable(
        IEnumerable<OwnedRecord> ownedRecords,
        IReadOnlySet<int> fixtureIds
    ) => [.. ownedRecords.Where(r => !fixtureIds.Contains(r.Id)).OrderByDescending(r => r.Id)];

    private async Task DeleteScenarioOwnedAsync(
        List<OwnedRecord> ownedRecords,
        IReadOnlySet<int> fixtureIds,
        string recordKind,
        Func<ConfigServiceClient, int, Task> delete
    )
    {
        foreach (var record in SelectDeletable(ownedRecords, fixtureIds))
        {
            var client = ResolveClient(record.Tenant);
            if (client is null)
            {
                _logger?.LogWarning(
                    "No Configuration Service token available; cannot delete scenario-owned {RecordKind} {Id}",
                    recordKind,
                    record.Id
                );
                continue;
            }

            try
            {
                await delete(client, record.Id);
                _logger?.LogInformation("Deleted scenario-owned {RecordKind} {Id}", recordKind, record.Id);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to delete {RecordKind} {Id}", recordKind, record.Id);
            }
        }
    }

    private ConfigServiceClient? ResolveClient(string tenantName)
    {
        if (context.ConfigClientsByTenant.TryGetValue(tenantName, out var existing))
        {
            return existing;
        }

        if (context.ConfigToken is null)
        {
            return null;
        }

        return new ConfigServiceClient(TestConfiguration.ConfigServiceUrl, context.ConfigToken, tenantName);
    }
}
