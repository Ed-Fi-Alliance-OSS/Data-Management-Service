// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using EdFi.DataManagementService.Core.Response;
using EdFi.DataManagementService.Core.Startup;
using EdFi.DataManagementService.Core.Utilities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EdFi.DataManagementService.Core.Middleware;

/// <summary>
/// Resolves the compiled mapping set for the current request's database instance
/// and attaches it to RequestInfo. Short-circuits with 503 if the mapping set
/// cannot be resolved. No-op when UseRelationalBackend is false.
/// </summary>
internal class ResolveMappingSetMiddleware(
    IOptions<AppSettings> appSettings,
    IMappingSetProvider mappingSetProvider,
    IEffectiveSchemaSetProvider effectiveSchemaSetProvider,
    IEnumerable<IRuntimeMappingSetCompiler> runtimeCompilers,
    ILogger<ResolveMappingSetMiddleware> logger
) : IPipelineStep
{
    private const string MappingSetUnavailableTitle = "Mapping Set Unavailable";

    // Resolve the configured dialect from the registered compiler(s).
    // In practice there is one compiler per deployment; if multiple are registered,
    // take the first (the backend that was configured).
    // Null when no compiler is registered (UseRelationalBackend is disabled).
    private readonly SqlDialect? _dialect = runtimeCompilers.FirstOrDefault()?.Dialect;

    public async Task Execute(RequestInfo requestInfo, Func<Task> next)
    {
        if (!appSettings.Value.UseRelationalBackend)
        {
            await next();
            return;
        }

        var fingerprint = requestInfo.DatabaseFingerprint;
        if (fingerprint == null)
        {
            // Fingerprint validation already short-circuited or is disabled.
            await next();
            return;
        }

        if (_dialect is null)
        {
            logger.LogError(
                "No runtime mapping set compiler is registered. "
                    + "Ensure a relational backend (PostgreSQL or MSSQL) is configured. "
                    + "TraceId: {TraceId}",
                LoggingSanitizer.SanitizeForLogging(requestInfo.FrontendRequest.TraceId.Value)
            );

            requestInfo.FrontendResponse = new FrontendResponse(
                StatusCode: 503,
                Body: FailureResponse.ForDatabaseFingerprintValidationError(
                    MappingSetUnavailableTitle,
                    "No relational backend compiler is registered. "
                        + "Ensure a relational backend is configured.",
                    ["No relational backend compiler is registered. " + "Check server configuration."],
                    requestInfo.FrontendRequest.TraceId
                ),
                Headers: []
            );
            return;
        }

        var effectiveSchema = effectiveSchemaSetProvider.EffectiveSchemaSet.EffectiveSchema;

        var key = new MappingSetKey(
            EffectiveSchemaHash: fingerprint.EffectiveSchemaHash,
            Dialect: _dialect.Value,
            RelationalMappingVersion: effectiveSchema.RelationalMappingVersion
        );

        try
        {
            requestInfo.MappingSet = await mappingSetProvider.GetOrCreateAsync(key, CancellationToken.None);
            await next();
        }
        catch (MappingSetUnavailableException ex)
        {
            logger.LogError(
                ex,
                "Mapping set unavailable for EffectiveSchemaHash {EffectiveSchemaHash}, "
                    + "Dialect {Dialect}, RelationalMappingVersion {RelationalMappingVersion}. "
                    + "TraceId: {TraceId}",
                LoggingSanitizer.SanitizeForLogging(key.EffectiveSchemaHash),
                key.Dialect,
                LoggingSanitizer.SanitizeForLogging(key.RelationalMappingVersion),
                LoggingSanitizer.SanitizeForLogging(requestInfo.FrontendRequest.TraceId.Value)
            );

            requestInfo.FrontendResponse = new FrontendResponse(
                StatusCode: 503,
                Body: FailureResponse.ForDatabaseFingerprintValidationError(
                    MappingSetUnavailableTitle,
                    "The compiled mapping set for this database is unavailable. "
                        + "Ensure the database is provisioned and mapping packs are available, "
                        + "or that runtime compilation is enabled. Check server logs for details.",
                    [
                        "The compiled mapping set for this database is unavailable. "
                            + "Check server logs for details.",
                    ],
                    requestInfo.FrontendRequest.TraceId
                ),
                Headers: []
            );
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Unexpected error resolving mapping set for EffectiveSchemaHash {EffectiveSchemaHash}. "
                    + "TraceId: {TraceId}",
                LoggingSanitizer.SanitizeForLogging(key.EffectiveSchemaHash),
                LoggingSanitizer.SanitizeForLogging(requestInfo.FrontendRequest.TraceId.Value)
            );

            requestInfo.FrontendResponse = new FrontendResponse(
                StatusCode: 503,
                Body: FailureResponse.ForDatabaseFingerprintValidationError(
                    MappingSetUnavailableTitle,
                    "An unexpected error occurred resolving the mapping set. "
                        + "Check server logs for details.",
                    [
                        "An unexpected error occurred resolving the mapping set. "
                            + "Check server logs for details.",
                    ],
                    requestInfo.FrontendRequest.TraceId
                ),
                Headers: []
            );
        }
    }
}
