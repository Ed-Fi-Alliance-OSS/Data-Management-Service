// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.External.Profile;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using EdFi.DataManagementService.Core.Profile;
using EdFi.DataManagementService.Core.Response;
using EdFi.DataManagementService.Core.Utilities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EdFi.DataManagementService.Core.Middleware;

/// <summary>
/// Middleware that invokes Core's ProfileWritePipeline.Execute() for POST/PUT requests
/// governed by a writable profile, and attaches the resulting BackendProfileWriteContext
/// to the request for downstream backend consumption.
/// </summary>
internal class ProfileWritePipelineMiddleware(
    IOptions<AppSettings> appSettings,
    ILogger<ProfileWritePipelineMiddleware> logger
) : IPipelineStep
{
    public async Task Execute(RequestInfo requestInfo, Func<Task> next)
    {
        // Short-circuit: not relational backend
        if (!appSettings.Value.UseRelationalBackend)
        {
            await next();
            return;
        }

        // Short-circuit: not a write operation
        if (requestInfo.Method is not (RequestMethod.POST or RequestMethod.PUT))
        {
            await next();
            return;
        }

        // Short-circuit: no mapping set resolved
        if (requestInfo.MappingSet is null)
        {
            await next();
            return;
        }

        // Short-circuit: no profile context
        if (requestInfo.ProfileContext is null)
        {
            await next();
            return;
        }

        var profileContext = requestInfo.ProfileContext;
        var writeContentType = profileContext.ResourceProfile.WriteContentType;

        // No writable profile content type — pass through
        if (writeContentType is null)
        {
            await next();
            return;
        }

        var profileName = profileContext.ProfileName;
        var resourceName = profileContext.ResourceProfile.ResourceName;
        var method = requestInfo.Method == RequestMethod.POST ? "POST" : "PUT";
        var operation = requestInfo.Method == RequestMethod.POST ? "upsert" : "update";
        var isCreate = requestInfo.Method == RequestMethod.POST;

        logger.LogDebug(
            "ProfileWritePipelineMiddleware: Executing profile write pipeline for profile {ProfileName}, "
                + "resource {ResourceName}, method {Method}. TraceId: {TraceId}",
            LoggingSanitizer.SanitizeForLogging(profileName),
            LoggingSanitizer.SanitizeForLogging(resourceName),
            method,
            LoggingSanitizer.SanitizeForLogging(requestInfo.FrontendRequest.TraceId.Value)
        );

        // Resolve the write plan from the mapping set
        var qualifiedResourceName = new QualifiedResourceName(
            requestInfo.ResourceInfo.ProjectName.Value,
            requestInfo.ResourceInfo.ResourceName.Value
        );

        ResourceWritePlan writePlan;
        try
        {
            writePlan = requestInfo.MappingSet.GetWritePlanOrThrow(qualifiedResourceName);
        }
        catch (Exception ex) when (ex is NotSupportedException or MissingWritePlanLookupGuardRailException)
        {
            logger.LogWarning(
                ex,
                "ProfileWritePipelineMiddleware: Write plan not available for resource {ResourceName}. "
                    + "Skipping profile pipeline. TraceId: {TraceId}",
                LoggingSanitizer.SanitizeForLogging(resourceName),
                LoggingSanitizer.SanitizeForLogging(requestInfo.FrontendRequest.TraceId.Value)
            );
            await next();
            return;
        }

        // Build scope catalog from the write plan
        var scopeCatalog = CompiledScopeAdapterFactory.BuildFromWritePlan(writePlan);

        // Resolve profile content type for pipeline validation
        ProfileContentType? resolvedContentType =
            profileContext.ContentType == ProfileContentType.Write ? ProfileContentType.Write : null;

        // Empty schema-required members for now (will be populated in future work)
        IReadOnlyDictionary<string, IReadOnlyList<string>> effectiveSchemaRequiredMembersByScope =
            new Dictionary<string, IReadOnlyList<string>>();

        // Execute the profile write pipeline (request-side only, no stored document yet)
        ProfileWritePipelineResult result = ProfileWritePipeline.Execute(
            canonicalizedRequestBody: requestInfo.ParsedBody,
            writeContentType: writeContentType,
            resolvedContentType: resolvedContentType,
            scopeCatalog: scopeCatalog,
            storedDocument: null,
            isCreate: isCreate,
            profileName: profileName,
            resourceName: resourceName,
            method: method,
            operation: operation,
            effectiveSchemaRequiredMembersByScope: effectiveSchemaRequiredMembersByScope
        );

        // Handle failures
        if (result.HasProfile && !result.Failures.IsEmpty)
        {
            logger.LogDebug(
                "ProfileWritePipelineMiddleware: Profile pipeline returned {FailureCount} failures "
                    + "for profile {ProfileName}, resource {ResourceName}. TraceId: {TraceId}",
                result.Failures.Length,
                LoggingSanitizer.SanitizeForLogging(profileName),
                LoggingSanitizer.SanitizeForLogging(resourceName),
                LoggingSanitizer.SanitizeForLogging(requestInfo.FrontendRequest.TraceId.Value)
            );

            int statusCode = result.Failures[0].Category switch
            {
                ProfileFailureCategory.CreatabilityViolation => 403,
                ProfileFailureCategory.CoreBackendContractMismatch => 500,
                ProfileFailureCategory.InvalidProfileDefinition => 500,
                ProfileFailureCategory.BindingAccountingFailure => 500,
                _ => 400, // WritableProfileValidationFailure, InvalidProfileUsage
            };

            requestInfo.FrontendResponse = new FrontendResponse(
                StatusCode: statusCode,
                Body: statusCode >= 500
                    ? FailureResponse.ForSystemError(requestInfo.FrontendRequest.TraceId)
                    : FailureResponse.ForDataPolicyEnforced(profileName, requestInfo.FrontendRequest.TraceId),
                Headers: []
            );
            return;
        }

        // No profile applies from the pipeline's perspective
        if (!result.HasProfile || result.Request is null)
        {
            await next();
            return;
        }

        // Build the backend profile write context with a captured stored-state projection invoker
        var invoker = new CapturedStoredStateProjectionInvoker(
            writeContentType,
            profileName,
            resourceName,
            method,
            effectiveSchemaRequiredMembersByScope
        );

        requestInfo.BackendProfileWriteContext = new BackendProfileWriteContext(
            Request: result.Request,
            ProfileName: profileContext.ProfileName,
            CompiledScopeCatalog: scopeCatalog,
            StoredStateProjectionInvoker: invoker
        );

        await next();
    }

    /// <summary>
    /// Captured invoker that re-executes the profile write pipeline with a stored
    /// document to produce the stored-state projection (ProfileAppliedWriteContext).
    /// </summary>
    private sealed class CapturedStoredStateProjectionInvoker(
        ContentTypeDefinition writeContentType,
        string profileName,
        string resourceName,
        string method,
        IReadOnlyDictionary<string, IReadOnlyList<string>> effectiveSchemaRequiredMembersByScope
    ) : IStoredStateProjectionInvoker
    {
        public ProfileAppliedWriteContext ProjectStoredState(
            JsonNode storedDocument,
            ProfileAppliedWriteRequest request,
            IReadOnlyList<CompiledScopeDescriptor> scopeCatalog
        )
        {
            var operation = method == "POST" ? "upsert" : "update";

            ProfileWritePipelineResult result = ProfileWritePipeline.Execute(
                canonicalizedRequestBody: request.WritableRequestBody,
                writeContentType: writeContentType,
                resolvedContentType: ProfileContentType.Write,
                scopeCatalog: scopeCatalog,
                storedDocument: storedDocument,
                isCreate: false,
                profileName: profileName,
                resourceName: resourceName,
                method: method,
                operation: operation,
                effectiveSchemaRequiredMembersByScope: effectiveSchemaRequiredMembersByScope
            );

            return result.Context
                ?? throw new InvalidOperationException(
                    $"Profile pipeline did not produce a ProfileAppliedWriteContext for stored-state projection. "
                        + $"Profile: {LoggingSanitizer.SanitizeForLogging(profileName)}, Resource: {LoggingSanitizer.SanitizeForLogging(resourceName)}, Method: {method}."
                );
        }
    }
}
