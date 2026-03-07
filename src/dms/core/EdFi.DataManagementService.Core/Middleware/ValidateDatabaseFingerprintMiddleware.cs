// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Pipeline;
using EdFi.DataManagementService.Core.Utilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EdFi.DataManagementService.Core.Middleware;

/// <summary>
/// Validates that the selected database has been provisioned by reading the
/// dms.EffectiveSchema fingerprint. Short-circuits with 503 if unprovisioned.
/// No-op when UseRelationalBackend is false.
/// </summary>
internal class ValidateDatabaseFingerprintMiddleware(
    IOptions<AppSettings> appSettings,
    DatabaseFingerprintProvider fingerprintProvider,
    ILogger<ValidateDatabaseFingerprintMiddleware> logger
) : IPipelineStep
{
    private const string MalformedFingerprintTitle = "Database Provisioning Error";
    private const string MalformedFingerprintDetail =
        "The target database contains malformed dms.EffectiveSchema provisioning metadata. Repair the database by re-running 'ddl provision' against an empty database. If provisioning was partial or the database was modified after provisioning, drop and recreate the database before reprovisioning.";

    public async Task Execute(RequestInfo requestInfo, Func<Task> next)
    {
        if (!appSettings.Value.UseRelationalBackend)
        {
            await next();
            return;
        }

        var dmsInstanceSelection =
            requestInfo.ScopedServiceProvider.GetRequiredService<IDmsInstanceSelection>();
        if (!dmsInstanceSelection.IsSet)
        {
            logger.LogError(
                "DMS instance not set before fingerprint validation - TraceId: {TraceId}",
                requestInfo.FrontendRequest.TraceId.Value
            );
            requestInfo.FrontendResponse = ProblemDetailsResponse.Create(
                503,
                ProblemDetailsResponse.ServiceConfigurationError,
                "Service Configuration Error",
                "Database instance has not been resolved for this request",
                requestInfo.FrontendRequest.TraceId
            );
            return;
        }

        var selectedInstance = dmsInstanceSelection.GetSelectedDmsInstance();

        if (string.IsNullOrEmpty(selectedInstance.ConnectionString))
        {
            logger.LogError(
                "DMS instance {InstanceName} has no connection string configured - TraceId: {TraceId}",
                LoggingSanitizer.SanitizeForLogging(selectedInstance.InstanceName),
                requestInfo.FrontendRequest.TraceId.Value
            );
            requestInfo.FrontendResponse = ProblemDetailsResponse.Create(
                503,
                ProblemDetailsResponse.ServiceConfigurationError,
                "Service Configuration Error",
                "DMS instance has no connection string configured",
                requestInfo.FrontendRequest.TraceId
            );
            return;
        }

        var connectionString = selectedInstance.ConnectionString;
        DatabaseFingerprint? fingerprint;

        try
        {
            fingerprint = await fingerprintProvider.GetFingerprintAsync(connectionString);
        }
        catch (DatabaseFingerprintValidationException ex)
        {
            logger.LogError(
                ex,
                "Malformed dms.EffectiveSchema fingerprint for DMS instance {InstanceId} ({InstanceName}) - TraceId: {TraceId}",
                selectedInstance.Id,
                LoggingSanitizer.SanitizeForLogging(selectedInstance.InstanceName),
                requestInfo.FrontendRequest.TraceId.Value
            );

            requestInfo.FrontendResponse = ProblemDetailsResponse.Create(
                503,
                ProblemDetailsResponse.DatabaseFingerprintValidationError,
                MalformedFingerprintTitle,
                MalformedFingerprintDetail,
                [ex.Message, MalformedFingerprintDetail],
                requestInfo.FrontendRequest.TraceId
            );

            return;
        }

        if (fingerprint == null)
        {
            logger.LogWarning(
                "Database not provisioned (no dms.EffectiveSchema row) for instance {InstanceName} - TraceId: {TraceId}",
                LoggingSanitizer.SanitizeForLogging(selectedInstance.InstanceName),
                requestInfo.FrontendRequest.TraceId.Value
            );
            requestInfo.FrontendResponse = ProblemDetailsResponse.Create(
                503,
                ProblemDetailsResponse.DatabaseNotProvisioned,
                "Database Not Provisioned",
                "The target database has not been provisioned. Run 'ddl provision' to initialize the database schema. If this database was provisioned after DMS first tried to use it, restart DMS to clear the cached provisioning state.",
                requestInfo.FrontendRequest.TraceId
            );
            return;
        }

        requestInfo.DatabaseFingerprint = fingerprint;
        await next();
    }
}
