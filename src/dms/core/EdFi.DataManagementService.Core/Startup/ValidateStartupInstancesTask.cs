// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.Utilities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EdFi.DataManagementService.Core.Startup;

/// <summary>
/// Startup task that validates database fingerprints and resource key seeds for all
/// instances known at startup. Pre-populates the singleton caches so the request-time
/// middleware becomes a no-op for these instances. Fails fast on any mismatch.
///
/// Instances discovered dynamically after startup (multi-tenant cache miss) are still
/// validated by the request-time middleware on first access.
/// </summary>
internal sealed class ValidateStartupInstancesTask(
    IOptions<AppSettings> appSettings,
    IDmsInstanceProvider dmsInstanceProvider,
    IConnectionStringProvider connectionStringProvider,
    DatabaseFingerprintProvider fingerprintProvider,
    IResourceKeyValidator resourceKeyValidator,
    ResourceKeyValidationCacheProvider resourceKeyValidationCacheProvider,
    IEffectiveSchemaSetProvider effectiveSchemaSetProvider,
    ILogger<ValidateStartupInstancesTask> logger
) : IDmsStartupTask
{
    public int Order => 310;

    public string Name => "Validate Startup Database Instances";

    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!appSettings.Value.UseRelationalBackend)
        {
            logger.LogDebug("Skipping startup instance validation because UseRelationalBackend is disabled");
            return;
        }

        var loadedTenantKeys = dmsInstanceProvider.GetLoadedTenantKeys();
        if (loadedTenantKeys.Count == 0)
        {
            logger.LogDebug("No loaded tenants found; skipping startup instance validation");
            return;
        }

        int totalValidated = 0;

        foreach (var tenantKey in loadedTenantKeys)
        {
            string? tenant = string.IsNullOrEmpty(tenantKey) ? null : tenantKey;
            var instances = dmsInstanceProvider.GetAll(tenant);

            foreach (var instance in instances)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var connectionString = connectionStringProvider.GetConnectionString(instance.Id, tenant);

                if (string.IsNullOrWhiteSpace(connectionString))
                {
                    throw new InvalidOperationException(
                        $"Instance {instance.Id} ('{LoggingSanitizer.SanitizeForLogging(instance.InstanceName)}') "
                            + $"for tenant '{LoggingSanitizer.SanitizeForLogging(tenant ?? "(default)")}' has no connection string configured. "
                            + "Every loaded DMS instance must have a valid connection string at startup. "
                            + "Check the instance configuration in the DMS Configuration Service."
                    );
                }

                await ValidateInstanceAsync(instance, tenant, connectionString, cancellationToken);
                totalValidated++;
            }
        }

        logger.LogInformation(
            "Startup instance validation completed: {ValidatedCount} instances validated across {TenantCount} tenants",
            totalValidated,
            loadedTenantKeys.Count
        );
    }

    private async Task ValidateInstanceAsync(
        DmsInstance instance,
        string? tenant,
        string connectionString,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            // Phase 1: Fingerprint validation (populates DatabaseFingerprintProvider cache)
            var fingerprint = await fingerprintProvider.GetFingerprintAsync(connectionString);

            if (fingerprint == null)
            {
                throw new InvalidOperationException(
                    $"Database not provisioned (no dms.EffectiveSchema row) for instance "
                        + $"{instance.Id} ('{LoggingSanitizer.SanitizeForLogging(instance.InstanceName)}') "
                        + $"tenant '{LoggingSanitizer.SanitizeForLogging(tenant ?? "(default)")}'. "
                        + "Run 'ddl provision' to initialize the database schema."
                );
            }

            // Phase 1b: EffectiveSchemaHash validation
            var effectiveSchema = effectiveSchemaSetProvider.EffectiveSchemaSet.EffectiveSchema;
            if (
                !string.Equals(
                    fingerprint.EffectiveSchemaHash,
                    effectiveSchema.EffectiveSchemaHash,
                    StringComparison.Ordinal
                )
            )
            {
                throw new InvalidOperationException(
                    $"EffectiveSchemaHash mismatch for instance "
                        + $"{instance.Id} ('{LoggingSanitizer.SanitizeForLogging(instance.InstanceName)}') "
                        + $"tenant '{LoggingSanitizer.SanitizeForLogging(tenant ?? "(default)")}': "
                        + $"database has '{LoggingSanitizer.SanitizeForLogging(fingerprint.EffectiveSchemaHash)}', "
                        + $"process expects '{LoggingSanitizer.SanitizeForLogging(effectiveSchema.EffectiveSchemaHash)}'. "
                        + "The database must be reprovisioned with 'ddl provision' against a fresh database."
                );
            }

            // Phase 2: Resource key seed validation (populates ResourceKeyValidationCacheProvider cache)

            var result = await resourceKeyValidationCacheProvider.GetOrValidateAsync(
                connectionString,
                () =>
                    resourceKeyValidator.ValidateAsync(
                        fingerprint,
                        effectiveSchema.ResourceKeyCount,
                        [.. effectiveSchema.ResourceKeySeedHash],
                        effectiveSchema.ResourceKeysInIdOrder.ToResourceKeyRows(),
                        connectionString,
                        cancellationToken
                    )
            );

            if (result is ResourceKeyValidationResult.ValidationFailure failure)
            {
                throw new InvalidOperationException(
                    $"Resource key seed mismatch for instance "
                        + $"{instance.Id} ('{LoggingSanitizer.SanitizeForLogging(instance.InstanceName)}') "
                        + $"tenant '{LoggingSanitizer.SanitizeForLogging(tenant ?? "(default)")}'. "
                        + $"Diff: {LoggingSanitizer.SanitizeForConsole(failure.DiffReport)}. "
                        + "The database must be reprovisioned with 'ddl provision' against a fresh database."
                );
            }
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Startup validation failed for instance "
                    + $"{instance.Id} ('{LoggingSanitizer.SanitizeForLogging(instance.InstanceName)}') "
                    + $"tenant '{LoggingSanitizer.SanitizeForLogging(tenant ?? "(default)")}': "
                    + ex.Message,
                ex
            );
        }

        logger.LogDebug(
            "Startup validation passed for instance {InstanceId} ({InstanceName}) tenant '{Tenant}'",
            instance.Id,
            LoggingSanitizer.SanitizeForLogging(instance.InstanceName),
            LoggingSanitizer.SanitizeForLogging(tenant ?? "(default)")
        );
    }
}
