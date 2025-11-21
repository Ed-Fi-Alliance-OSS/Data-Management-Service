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

    [AfterScenario("@kafka", Order = 500)]
    public void CleanupKafkaCollector()
    {
        if (context.KafkaCollector != null)
        {
            _logger?.LogInformation("Cleaning up Kafka message collector");

            // Log final diagnostics before disposal
            _logger?.LogInformation(
                "Final Kafka message count: {MessageCount}",
                context.KafkaCollector.MessageCount
            );
            context.KafkaCollector.LogDiagnostics();

            // Disposal will happen in context.Reset()
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

        var configClient = new ConfigServiceClient(TestConfiguration.ConfigServiceUrl, context.ConfigToken);

        try
        {
            // Delete application
            if (context.ApplicationId.HasValue)
            {
                _logger?.LogInformation("Deleting application {ApplicationId}", context.ApplicationId);
                try
                {
                    await configClient.DeleteApplicationAsync(context.ApplicationId.Value);
                    _logger?.LogInformation("Application deleted successfully");
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(
                        ex,
                        "Failed to delete application {ApplicationId}",
                        context.ApplicationId
                    );
                }
            }

            // Delete instances (this also deletes route contexts)
            // Skip instances 1, 2, 3 as they are pre-existing instances created by setup script
            var instancesToDelete = context.InstanceIds.Where(id => id is not (1 or 2 or 3)).ToList();
            foreach (var instanceId in instancesToDelete)
            {
                _logger?.LogInformation("Deleting instance {InstanceId}", instanceId);
                try
                {
                    await configClient.DeleteInstanceAsync(instanceId);
                    _logger?.LogInformation("Instance {InstanceId} deleted successfully", instanceId);
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Failed to delete instance {InstanceId}", instanceId);
                }
            }

            // Delete vendor
            if (context.VendorId.HasValue)
            {
                _logger?.LogInformation("Deleting vendor {VendorId}", context.VendorId);
                try
                {
                    await configClient.DeleteVendorAsync(context.VendorId.Value);
                    _logger?.LogInformation("Vendor deleted successfully");
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Failed to delete vendor {VendorId}", context.VendorId);
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
