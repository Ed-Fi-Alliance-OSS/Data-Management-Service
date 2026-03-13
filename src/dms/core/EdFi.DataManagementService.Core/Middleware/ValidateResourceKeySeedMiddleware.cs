// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using EdFi.DataManagementService.Core.Response;
using EdFi.DataManagementService.Core.Startup;
using EdFi.DataManagementService.Core.Utilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EdFi.DataManagementService.Core.Middleware;

/// <summary>
/// Validates that the database resource key seed matches the expected effective schema.
/// Short-circuits with 503 if the resource key seed is mismatched.
/// No-op when UseRelationalBackend is false.
/// </summary>
internal class ValidateResourceKeySeedMiddleware(
    IOptions<AppSettings> appSettings,
    IResourceKeyValidator resourceKeyValidator,
    ResourceKeyValidationCacheProvider cacheProvider,
    IEffectiveSchemaSetProvider effectiveSchemaSetProvider,
    ILogger<ValidateResourceKeySeedMiddleware> logger
) : IPipelineStep
{
    private const string ResourceKeySeedMismatchTitle = "Resource Key Seed Mismatch";
    private const string ResourceKeySeedMismatchDetail =
        "The database resource key seed does not match the expected schema. "
        + "The database must be reprovisioned with 'ddl provision' against a fresh database "
        + "and DMS restarted to clear the cached validation state.";

    public async Task Execute(RequestInfo requestInfo, Func<Task> next)
    {
        if (!appSettings.Value.UseRelationalBackend)
        {
            await next();
            return;
        }

        // DatabaseFingerprint is set by ValidateDatabaseFingerprintMiddleware (runs before this step).
        // If it's null, fingerprint validation already short-circuited, so this middleware won't run.
        // But guard defensively in case pipeline ordering changes.
        var fingerprint = requestInfo.DatabaseFingerprint;
        if (fingerprint == null)
        {
            await next();
            return;
        }

        var selectedInstance = requestInfo
            .ScopedServiceProvider.GetRequiredService<IDmsInstanceSelection>()
            .GetSelectedDmsInstance();
        // ConnectionString is guaranteed non-null by ValidateDatabaseFingerprintMiddleware,
        // which runs before this step and short-circuits with 503 if the connection string is missing.
        var connectionString = selectedInstance.ConnectionString!;

        var effectiveSchema = effectiveSchemaSetProvider.EffectiveSchemaSet.EffectiveSchema;

        // Convert ResourceKeyEntry list to ResourceKeyRow list for the validator interface
        var expectedResourceKeys = effectiveSchema
            .ResourceKeysInIdOrder.Select(e => new ResourceKeyRow(
                e.ResourceKeyId,
                e.Resource.ProjectName,
                e.Resource.ResourceName,
                e.ResourceVersion
            ))
            .ToList();

        ResourceKeyValidationResult result;

        try
        {
            result = await cacheProvider.GetOrValidateAsync(
                connectionString,
                () =>
                    resourceKeyValidator.ValidateAsync(
                        fingerprint,
                        effectiveSchema.ResourceKeyCount,
                        [.. effectiveSchema.ResourceKeySeedHash],
                        expectedResourceKeys,
                        connectionString
                    )
            );
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Resource key seed validation failed with an unexpected error for instance {InstanceId} ({InstanceName}). TraceId: {TraceId}",
                selectedInstance.Id,
                LoggingSanitizer.SanitizeForLogging(selectedInstance.InstanceName),
                requestInfo.FrontendRequest.TraceId.Value
            );

            requestInfo.FrontendResponse = new FrontendResponse(
                StatusCode: 503,
                Body: FailureResponse.ForDatabaseFingerprintValidationError(
                    ResourceKeySeedMismatchTitle,
                    "Resource key seed validation encountered an unexpected error. Check server logs for details.",
                    ["An unexpected error occurred during resource key seed validation."],
                    requestInfo.FrontendRequest.TraceId
                ),
                Headers: []
            );

            return;
        }

        switch (result)
        {
            case ResourceKeyValidationResult.ValidationSuccess:
                await next();
                return;

            case ResourceKeyValidationResult.ValidationFailure failure:
                logger.LogError(
                    "Resource key seed mismatch for instance {InstanceId} ({InstanceName}). "
                        + "Diff report: {DiffReport}. TraceId: {TraceId}",
                    selectedInstance.Id,
                    LoggingSanitizer.SanitizeForLogging(selectedInstance.InstanceName),
                    LoggingSanitizer.SanitizeForLogging(failure.DiffReport),
                    requestInfo.FrontendRequest.TraceId.Value
                );

                requestInfo.FrontendResponse = new FrontendResponse(
                    StatusCode: 503,
                    Body: FailureResponse.ForDatabaseFingerprintValidationError(
                        ResourceKeySeedMismatchTitle,
                        ResourceKeySeedMismatchDetail,
                        [ResourceKeySeedMismatchDetail],
                        requestInfo.FrontendRequest.TraceId
                    ),
                    Headers: []
                );

                return;
        }
    }
}
