// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.External.Profile;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Model;
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
    IEffectiveSchemaRequiredMembersProvider effectiveSchemaRequiredMembersProvider,
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
        var resolvedContentType = profileContext.ContentType;

        var profileName = profileContext.ProfileName;
        var resourceName = profileContext.ResourceProfile.ResourceName;
        var method = requestInfo.Method == RequestMethod.POST ? "POST" : "PUT";
        var operation = requestInfo.Method == RequestMethod.POST ? "upsert" : "update";

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

        // Build scope catalog from the write plan, augmented with any inlined scopes
        // discovered from the content type tree that have no backing table
        var tableScopeSet = new HashSet<string>(
            writePlan.TablePlansInDependencyOrder.Select(tp => tp.TableModel.JsonScope.Canonical)
        );
        var inlinedScopes = writeContentType is null
            ? []
            : ContentTypeScopeDiscovery.DiscoverInlinedScopes(writeContentType, tableScopeSet);
        var scopeCatalog = CompiledScopeAdapterFactory.BuildFromWritePlan(writePlan, inlinedScopes);
        var effectiveSchemaRequiredMembersByScope = effectiveSchemaRequiredMembersProvider.Resolve(
            writePlan,
            scopeCatalog
        );

        var preResolutionResult = ProfileWritePipeline.ExecutePreResolution(
            canonicalizedRequestBody: requestInfo.ParsedBody,
            writeContentType: writeContentType,
            resolvedContentType: resolvedContentType,
            scopeCatalog: scopeCatalog,
            profileName: profileName,
            resourceName: resourceName,
            method: method,
            operation: operation
        );

        if (preResolutionResult.HasProfile && !preResolutionResult.Failures.IsEmpty)
        {
            logger.LogDebug(
                "ProfileWritePipelineMiddleware: Profile pre-resolution returned {FailureCount} failures "
                    + "for profile {ProfileName}, resource {ResourceName}. TraceId: {TraceId}",
                preResolutionResult.Failures.Length,
                LoggingSanitizer.SanitizeForLogging(profileName),
                LoggingSanitizer.SanitizeForLogging(resourceName),
                LoggingSanitizer.SanitizeForLogging(requestInfo.FrontendRequest.TraceId.Value)
            );

            requestInfo.FrontendResponse = BuildFailureResponse(
                preResolutionResult.Failures,
                profileName,
                requestInfo.FrontendRequest.TraceId
            );
            return;
        }

        if (!preResolutionResult.HasProfile || preResolutionResult.Request is null)
        {
            await next();
            return;
        }

        var preResolvedRequest = preResolutionResult.Request;
        var resolvedWriteContentType =
            writeContentType
            ?? throw new InvalidOperationException(
                "Profile pre-resolution produced a request without a writable content type."
            );
        var resolvedTargetInvoker = new CapturedResolvedProfileWriteInvoker(
            preResolvedRequest,
            resolvedWriteContentType,
            profileName,
            resourceName,
            method,
            operation,
            effectiveSchemaRequiredMembersByScope
        );

        // PUT creatability for descendant scopes and collection items depends on the
        // stored-document existence lookup the backend performs after target resolution.
        // Keep the resolved-target phase deferred until that state is available.
        requestInfo.BackendProfileWriteContext = new BackendProfileWriteContext(
            PreResolvedRequest: preResolvedRequest,
            ProfileName: profileContext.ProfileName,
            CompiledScopeCatalog: scopeCatalog,
            ResolvedProfileWriteInvoker: resolvedTargetInvoker
        );

        await next();
    }

    /// <summary>
    /// Captured invoker that re-executes the resolved-target phase after the backend
    /// knows whether the write targets a new or existing document.
    /// </summary>
    private sealed class CapturedResolvedProfileWriteInvoker(
        ProfilePreResolvedWriteRequest preResolvedRequest,
        ContentTypeDefinition writeContentType,
        string profileName,
        string resourceName,
        string method,
        string operation,
        IReadOnlyDictionary<string, IReadOnlyList<string>> effectiveSchemaRequiredMembersByScope
    ) : IResolvedProfileWriteInvoker
    {
        public ResolvedProfileWriteResult Execute(
            JsonNode? storedDocument,
            bool isCreate,
            IReadOnlyList<CompiledScopeDescriptor> scopeCatalog
        )
        {
            var result = ProfileWritePipeline.ExecuteResolvedTarget(
                preResolvedRequest: preResolvedRequest,
                writeContentType: writeContentType,
                scopeCatalog: scopeCatalog,
                storedDocument: storedDocument,
                isCreate: isCreate,
                profileName: profileName,
                resourceName: resourceName,
                method: method,
                operation: operation,
                effectiveSchemaRequiredMembersByScope: effectiveSchemaRequiredMembersByScope
            );

            return result.Failures.IsEmpty
                ? ResolvedProfileWriteResult.Success(
                    result.Request
                        ?? throw new InvalidOperationException(
                            "Resolved profile write execution completed without producing a request contract."
                        ),
                    result.Context
                )
                : ResolvedProfileWriteResult.Failure(result.Failures);
        }
    }

    private static FrontendResponse BuildFailureResponse(
        ImmutableArray<ProfileFailure> failures,
        string profileName,
        TraceId traceId
    )
    {
        var statusCode = failures[0].Category switch
        {
            ProfileFailureCategory.CreatabilityViolation => 403,
            ProfileFailureCategory.CoreBackendContractMismatch => 500,
            ProfileFailureCategory.InvalidProfileDefinition => 500,
            ProfileFailureCategory.BindingAccountingFailure => 500,
            _ => 400,
        };

        return new FrontendResponse(
            StatusCode: statusCode,
            Body: statusCode >= 500
                ? FailureResponse.ForSystemError(traceId)
                : FailureResponse.ForDataPolicyEnforced(profileName, traceId),
            Headers: []
        );
    }
}
