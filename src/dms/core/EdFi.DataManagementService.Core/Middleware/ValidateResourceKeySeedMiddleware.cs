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

namespace EdFi.DataManagementService.Core.Middleware;

/// <summary>
/// Validates that the database resource key seed matches the expected effective schema.
/// Short-circuits with 503 if the resource key seed is mismatched.
///
/// Design note: Instances known at startup are validated eagerly by
/// ValidateStartupInstancesTask (Order 310), which pre-populates the cache.
/// This middleware handles dynamically-discovered instances (multi-tenant cache miss)
/// by validating on first request per connection string. See new-startup-flow.md §6.
/// </summary>
internal class ValidateResourceKeySeedMiddleware(
    IResourceKeyValidator resourceKeyValidator,
    ResourceKeyValidationCacheProvider cacheProvider,
    IEffectiveSchemaSetProvider effectiveSchemaSetProvider,
    ILogger<ValidateResourceKeySeedMiddleware> logger
) : IPipelineStep
{
    private const string ResourceKeySeedMismatchTitle = "Resource Key Seed Mismatch";

    /// <summary>
    /// The primary wording, unchanged. A primary mismatch is cached for the life of the process,
    /// so reprovisioning on its own genuinely is not enough.
    /// </summary>
    private const string ResourceKeySeedMismatchDetail =
        "The database resource key seed does not match the expected schema. "
        + "The database must be reprovisioned with 'ddl provision' against a fresh database "
        + "and the Ed-Fi API service restarted to clear the cached validation state.";

    /// <summary>
    /// The same remediation without the restart. This request has already dropped the derivative
    /// verdict, so the next one revalidates; sending the operator after a cached result that no
    /// longer exists would be false guidance.
    /// </summary>
    private const string ResourceKeySeedMismatchDerivativeDetail =
        "The database resource key seed does not match the expected schema. "
        + "The database must be reprovisioned with 'ddl provision' against a fresh database. "
        + "No restart is required: this result was not retained, and the next request will "
        + "revalidate the database.";

    public async Task Execute(RequestInfo requestInfo, Func<Task> next)
    {
        // DatabaseFingerprint is set by ValidateDatabaseFingerprintMiddleware (runs before this step).
        // If it's null, fingerprint validation already short-circuited, so this middleware won't run.
        // But guard defensively in case pipeline ordering changes.
        var fingerprint = requestInfo.DatabaseFingerprint;
        if (fingerprint == null)
        {
            await next();
            return;
        }

        // Validated against the database this request is served from, not the parent, so a request
        // routed to a derivative is checked against that database's own resource keys. The parent is
        // still read, for the identity that names the instance in logs.
        var dataStoreSelection = requestInfo.ScopedServiceProvider.GetRequiredService<IDataStoreSelection>();
        var selectedInstance = dataStoreSelection.GetSelectedDataStore();
        var target = dataStoreSelection.GetEffectiveTarget();

        var effectiveSchema = effectiveSchemaSetProvider.EffectiveSchemaSet.EffectiveSchema;

        // Read synchronously so the token exists before the value is awaited, on the fault path as
        // well as the success path.
        ValidationCacheRead<ResourceKeyValidationResult> read = cacheProvider.Read(
            ValidationCacheKey.For(target),
            () =>
            {
                // The validation task is shared through ResourceKeyValidationCacheProvider.
                // Do not tie first validation for a connection string to one client abort.
                return resourceKeyValidator.ValidateAsync(
                    fingerprint,
                    effectiveSchema.ResourceKeyCount,
                    [.. effectiveSchema.ResourceKeySeedHash],
                    effectiveSchema.ResourceKeysInIdOrder.ToResourceKeyRows(),
                    target
                );
            }
        );

        ResourceKeyValidationResult result;

        try
        {
            result = await read.Value;
        }
        catch (Exception ex)
        {
            // Only the exception's type, never the exception itself and never its message, data, or
            // inner exceptions. This catch can see a selected target fail inside connection
            // acquisition, and a provider exception from parsing or opening a connection string can
            // quote its values back.
            // S6667 asks for the caught exception to be passed to the logger. That is the right
            // default and the wrong thing here, for the reason above: the exception carries the
            // untrusted value. Its type is logged instead, which is the part that helps an operator
            // without carrying anything the provider put in it.
#pragma warning disable S6667
            logger.LogError(
                "Resource key seed validation failed with an unexpected {ExceptionType} for data store {DataStoreId} ({Name}). TraceId: {TraceId}",
                ex.GetType().Name,
                selectedInstance.Id,
                LoggingSanitizer.SanitizeForLogging(selectedInstance.Name),
                LoggingSanitizer.SanitizeForLogging(requestInfo.FrontendRequest.TraceId.Value)
            );
#pragma warning restore S6667

            requestInfo.FrontendResponse = new FrontendResponse(
                StatusCode: 503,
                Body: FailureResponse.ForResourceKeySeedValidationError(
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
                // A mismatch is returned rather than thrown, so the provider never sees it. Dropping
                // the entry here is what lets a derivative reseeded after this request be served
                // without a restart; for a primary the token is a no-op and the verdict stands.
                read.Token.Invalidate();

                // The recovery instruction differs by policy class: the primary verdict just
                // read is retained, the derivative one is not.
                string mismatchDetail =
                    target.Kind == EffectiveTargetKind.Primary
                        ? ResourceKeySeedMismatchDetail
                        : ResourceKeySeedMismatchDerivativeDetail;

                // Use SanitizeForConsole for the diff report to preserve tuple
                // punctuation (parentheses, commas, brackets) needed for readability.
                logger.LogError(
                    "Resource key seed mismatch for data store {DataStoreId} ({Name}). "
                        + "Diff report: {DiffReport}. TraceId: {TraceId}",
                    selectedInstance.Id,
                    LoggingSanitizer.SanitizeForLogging(selectedInstance.Name),
                    LoggingSanitizer.SanitizeForConsole(failure.DiffReport),
                    LoggingSanitizer.SanitizeForLogging(requestInfo.FrontendRequest.TraceId.Value)
                );

                requestInfo.FrontendResponse = new FrontendResponse(
                    StatusCode: 503,
                    Body: FailureResponse.ForResourceKeySeedValidationError(
                        ResourceKeySeedMismatchTitle,
                        mismatchDetail,
                        [mismatchDetail],
                        requestInfo.FrontendRequest.TraceId
                    ),
                    Headers: []
                );

                return;

            default:
                throw new InvalidOperationException(
                    $"Unhandled ResourceKeyValidationResult type: {result.GetType().Name}"
                );
        }
    }
}
