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

        if (context.ConfigToken == null)
        {
            _logger?.LogWarning("No config token available, skipping cleanup");
            return;
        }

        try
        {
            // Clean up per-tenant resources
            foreach (var tenantName in context.TenantNames)
            {
                _logger?.LogInformation("Cleaning up resources for tenant {TenantName}", tenantName);

                if (!context.ConfigClientsByTenant.TryGetValue(tenantName, out var tenantClient))
                {
                    tenantClient = new ConfigServiceClient(
                        TestConfiguration.ConfigServiceUrl,
                        context.ConfigToken,
                        tenantName
                    );
                }

                // Delete applications for this tenant
                if (context.ApplicationIdsByTenant.TryGetValue(tenantName, out var appId))
                {
                    _logger?.LogInformation(
                        "Deleting application {ApplicationId} for tenant {TenantName}",
                        appId,
                        tenantName
                    );
                    try
                    {
                        await tenantClient.DeleteApplicationAsync(appId);
                        _logger?.LogInformation("Application deleted successfully");
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogWarning(ex, "Failed to delete application {ApplicationId}", appId);
                    }
                }

                // Delete instances for this tenant
                var instancesForTenant = context
                    .DataStoreIdToTenant.Where(kvp => kvp.Value == tenantName)
                    .Select(kvp => kvp.Key)
                    .OrderByDescending(id => id)
                    .ToList();

                foreach (var dataStoreId in instancesForTenant)
                {
                    _logger?.LogInformation(
                        "Deleting instance {DataStoreId} for tenant {TenantName}",
                        dataStoreId,
                        tenantName
                    );
                    try
                    {
                        await tenantClient.DeleteInstanceAsync(dataStoreId);
                        _logger?.LogInformation("Instance {DataStoreId} deleted successfully", dataStoreId);
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogWarning(ex, "Failed to delete instance {DataStoreId}", dataStoreId);
                    }
                }

                // Delete vendor for this tenant
                if (context.VendorIdsByTenant.TryGetValue(tenantName, out var vendorId))
                {
                    _logger?.LogInformation(
                        "Deleting vendor {VendorId} for tenant {TenantName}",
                        vendorId,
                        tenantName
                    );
                    try
                    {
                        await tenantClient.DeleteVendorAsync(vendorId);
                        _logger?.LogInformation("Vendor deleted successfully");
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogWarning(ex, "Failed to delete vendor {VendorId}", vendorId);
                    }
                }
            }

            // Legacy cleanup for single-tenant scenarios
            if (context.TenantNames.Count == 0 && context.CurrentTenant != null)
            {
                var legacyClient = new ConfigServiceClient(
                    TestConfiguration.ConfigServiceUrl,
                    context.ConfigToken,
                    context.CurrentTenant
                );

                // Delete legacy application
                if (context.ApplicationId.HasValue)
                {
                    _logger?.LogInformation(
                        "Deleting legacy application {ApplicationId}",
                        context.ApplicationId
                    );
                    try
                    {
                        await legacyClient.DeleteApplicationAsync(context.ApplicationId.Value);
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogWarning(
                            ex,
                            "Failed to delete legacy application {ApplicationId}",
                            context.ApplicationId
                        );
                    }
                }

                // Delete legacy instances
                foreach (var dataStoreId in context.DataStoreIds.OrderByDescending(id => id))
                {
                    _logger?.LogInformation("Deleting legacy instance {DataStoreId}", dataStoreId);
                    try
                    {
                        await legacyClient.DeleteInstanceAsync(dataStoreId);
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogWarning(
                            ex,
                            "Failed to delete legacy instance {DataStoreId}",
                            dataStoreId
                        );
                    }
                }

                // Delete legacy vendor
                if (context.VendorId.HasValue)
                {
                    _logger?.LogInformation("Deleting legacy vendor {VendorId}", context.VendorId);
                    try
                    {
                        await legacyClient.DeleteVendorAsync(context.VendorId.Value);
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogWarning(
                            ex,
                            "Failed to delete legacy vendor {VendorId}",
                            context.VendorId
                        );
                    }
                }
            }

            _logger?.LogInformation("Instance cleanup completed");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error during cleanup");
        }
        finally
        {
            // Reset context for next scenario
            context.Reset();
        }
    }
}
