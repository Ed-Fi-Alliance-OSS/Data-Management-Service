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
/// middleware becomes a no-op for these instances.
///
/// Per-instance validation failures are logged at Critical level but do not abort
/// startup — the failure is cached and the request-time middleware returns 503 for
/// that instance while other instances continue to be served. This preserves the
/// multi-instance-safe failure mode required by the design (see EPIC.md,
/// transactions-and-concurrency.md, 03-config-and-failure-modes.md).
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
        int totalFailed = 0;

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
                    // No connection string means we can't cache anything — the request-time
                    // middleware (ValidateDatabaseFingerprintMiddleware) independently checks
                    // for missing connection strings and returns 503.
                    logger.LogCritical(
                        "Instance {InstanceId} ({InstanceName}) for tenant {Tenant} has no connection string configured. "
                            + "Requests routed to this instance will receive 503. "
                            + "Check the instance configuration in the DMS Configuration Service",
                        instance.Id,
                        LoggingSanitizer.SanitizeForLogging(instance.InstanceName),
                        LoggingSanitizer.SanitizeForLogging(tenant ?? "(default)")
                    );
                    totalFailed++;
                    continue;
                }

                if (await ValidateInstanceAsync(instance, tenant, connectionString, cancellationToken))
                {
                    totalValidated++;
                }
                else
                {
                    totalFailed++;
                }
            }
        }

        if (totalFailed > 0)
        {
            logger.LogWarning(
                "Startup instance validation completed with failures: {ValidatedCount} passed, {FailedCount} failed across {TenantCount} tenants. "
                    + "Failed instances will return 503 at request time",
                totalValidated,
                totalFailed,
                loadedTenantKeys.Count
            );
        }
        else
        {
            logger.LogInformation(
                "Startup instance validation completed: {ValidatedCount} instances validated across {TenantCount} tenants",
                totalValidated,
                loadedTenantKeys.Count
            );
        }
    }

    /// <summary>
    /// Validates a single instance. Returns true on success, false on validation failure.
    /// Validation failures are logged and cached — the request-time middleware will return
    /// 503 for that instance. Only <see cref="OperationCanceledException"/> propagates.
    /// </summary>
    private async Task<bool> ValidateInstanceAsync(
        DmsInstance instance,
        string? tenant,
        string connectionString,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        string sanitizedName = LoggingSanitizer.SanitizeForLogging(instance.InstanceName);
        string sanitizedTenant = LoggingSanitizer.SanitizeForLogging(tenant ?? "(default)");

        try
        {
            // Phase 1: Fingerprint validation (populates DatabaseFingerprintProvider cache)
            var fingerprint = await fingerprintProvider.GetFingerprintAsync(connectionString);

            if (fingerprint == null)
            {
                // Null fingerprint is permanently cached by DatabaseFingerprintProvider;
                // the middleware will return 503 with a "not provisioned" message.
                logger.LogCritical(
                    "Database not provisioned (no dms.EffectiveSchema row) for instance "
                        + "{InstanceId} ({InstanceName}) tenant {Tenant}. "
                        + "Requests routed to this instance will receive 503. "
                        + "Run 'ddl provision' to initialize the database schema",
                    instance.Id,
                    sanitizedName,
                    sanitizedTenant
                );
                return false;
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
                // The fingerprint (with its wrong hash) is already cached by
                // DatabaseFingerprintProvider; the middleware will compare hashes
                // and return 503 with a schema-mismatch message.
                logger.LogCritical(
                    "EffectiveSchemaHash mismatch for instance "
                        + "{InstanceId} ({InstanceName}) tenant {Tenant}: "
                        + "database has {DbHash}, process expects {ExpectedHash}. "
                        + "Requests routed to this instance will receive 503. "
                        + "The database must be reprovisioned with 'ddl provision' against a fresh database",
                    instance.Id,
                    sanitizedName,
                    sanitizedTenant,
                    LoggingSanitizer.SanitizeForLogging(fingerprint.EffectiveSchemaHash),
                    LoggingSanitizer.SanitizeForLogging(effectiveSchema.EffectiveSchemaHash)
                );
                return false;
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
                // The ValidationFailure is permanently cached by ResourceKeyValidationCacheProvider;
                // the middleware will return 503 with a seed-mismatch message.
                logger.LogCritical(
                    "Resource key seed mismatch for instance "
                        + "{InstanceId} ({InstanceName}) tenant {Tenant}. "
                        + "Diff: {DiffReport}. "
                        + "Requests routed to this instance will receive 503. "
                        + "The database must be reprovisioned with 'ddl provision' against a fresh database",
                    instance.Id,
                    sanitizedName,
                    sanitizedTenant,
                    LoggingSanitizer.SanitizeForConsole(failure.DiffReport)
                );
                return false;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (DatabaseFingerprintValidationException ex)
        {
            // DatabaseFingerprintValidationException is permanently cached by
            // DatabaseFingerprintProvider; the middleware will return 503.
            logger.LogCritical(
                ex,
                "Malformed dms.EffectiveSchema fingerprint for instance "
                    + "{InstanceId} ({InstanceName}) tenant {Tenant}. "
                    + "Requests routed to this instance will receive 503. "
                    + "Restart DMS after repairing the database",
                instance.Id,
                sanitizedName,
                sanitizedTenant
            );
            return false;
        }
        catch (Exception ex)
        {
            // Transient exceptions (network, timeout) are evicted from caches so the
            // middleware will retry on first request. Log but don't fail startup.
            logger.LogCritical(
                ex,
                "Startup validation failed for instance "
                    + "{InstanceId} ({InstanceName}) tenant {Tenant}: {ErrorMessage}. "
                    + "Requests routed to this instance will be retried on first access",
                instance.Id,
                sanitizedName,
                sanitizedTenant,
                LoggingSanitizer.SanitizeForLogging(ex.Message)
            );
            return false;
        }

        logger.LogDebug(
            "Startup validation passed for instance {InstanceId} ({InstanceName}) tenant '{Tenant}'",
            instance.Id,
            sanitizedName,
            sanitizedTenant
        );
        return true;
    }
}
