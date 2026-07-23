// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.InstanceManagement.Tests.E2E.Management;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Reqnroll;
using Serilog;
using Serilog.Extensions.Logging;

namespace EdFi.InstanceManagement.Tests.E2E.Hooks;

[Binding]
public class SetupHooks
{
    private static ILogger<SetupHooks>? _logger;

    private SetupHooks()
    {
        // Private constructor to satisfy SonarAnalyzer
    }

    [BeforeTestRun]
    public static void BeforeTestRun()
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
            .Build();

        Log.Logger = new LoggerConfiguration().ReadFrom.Configuration(configuration).CreateLogger();

        var loggerFactory = new SerilogLoggerFactory(Log.Logger);
        _logger = loggerFactory.CreateLogger<SetupHooks>();

        _logger.LogInformation("Starting Instance Management E2E Tests");
        _logger.LogInformation(
            "Authentication Service: {AuthenticationService}",
            AppSettings.AuthenticationService
        );
    }

    [AfterTestRun]
    public static async Task AfterTestRun()
    {
        // Best-effort cleanup of the suite-owned fixture runs before Serilog is closed so its diagnostics are
        // captured. Cleanup failures are swallowed and can never hide the actual test outcome; the
        // project-scoped `down -v` remains the definitive cleanup.
        await CleanupFixtureAsync();

        _logger?.LogInformation("Instance Management E2E Tests Complete");
        await Log.CloseAndFlushAsync();
    }

    private static async Task CleanupFixtureAsync()
    {
        if (!InstanceFixtureState.IsAvailable)
        {
            // No fixture contract present (e.g. isolated unit-test run): nothing to clean up.
            return;
        }

        InstanceFixtureState fixture;
        try
        {
            fixture = InstanceFixtureState.Current;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Skipping fixture cleanup: fixture state could not be loaded");
            return;
        }

        string configToken;
        try
        {
            configToken = await TokenHelper.GetConfigServiceTokenAsync(
                $"{TestConfiguration.ConfigServiceUrl}/connect/token",
                "DmsConfigurationService",
                "ValidClientSecret1234567890!Abcd"
            );
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(
                ex,
                "Skipping fixture cleanup: could not obtain a Configuration Service token"
            );
            return;
        }

        foreach (var tenant in fixture.Tenants)
        {
            var client = new ConfigServiceClient(
                TestConfiguration.ConfigServiceUrl,
                configToken,
                tenant.Name
            );

            // Dependency-safe order: application, then its data stores, then the vendor. Route-context
            // records have no dedicated delete on ConfigServiceClient; they are removed with their data
            // store or by the project-scoped down -v. Never log keys, secrets, tokens, or connection strings.
            await TryDeleteAsync(
                () => client.DeleteApplicationAsync(tenant.ApplicationId),
                "application",
                tenant.ApplicationId
            );

            var dataStoreIds = fixture
                .RoutesForTenant(tenant.Name)
                .Select(r => r.DataStoreId)
                .OrderByDescending(id => id);
            foreach (var dataStoreId in dataStoreIds)
            {
                await TryDeleteAsync(
                    () => client.DeleteInstanceAsync(dataStoreId),
                    "data store",
                    dataStoreId
                );
            }

            await TryDeleteAsync(() => client.DeleteVendorAsync(tenant.VendorId), "vendor", tenant.VendorId);
        }
    }

    private static async Task TryDeleteAsync(Func<Task> delete, string recordKind, int id)
    {
        try
        {
            await delete();
            _logger?.LogInformation("Deleted fixture {RecordKind} {Id}", recordKind, id);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(
                ex,
                "Best-effort fixture cleanup failed for {RecordKind} {Id}",
                recordKind,
                id
            );
        }
    }
}
