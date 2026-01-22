// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.Backend;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Frontend;
using EdFi.DataManagementService.Core.External.Interface;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using EdFi.DataManagementService.Core.Profile;
using EdFi.DataManagementService.Core.Response;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using static EdFi.DataManagementService.Core.External.Backend.GetResult;

namespace EdFi.DataManagementService.Core.Middleware;

/// <summary>
/// Middleware that filters POST/PUT request bodies according to profile WriteContentType rules.
/// Fields excluded by the profile are silently stripped from the request before processing.
/// For POST requests, also validates that required fields are not excluded by the profile.
/// For PUT requests, stripped fields are merged back from the existing document to preserve values.
/// This middleware runs after ValidateDocumentMiddleware to filter validated request bodies.
/// </summary>
internal class ProfileWriteValidationMiddleware(
    IProfileResponseFilter profileFilter,
    IProfileCreatabilityValidator creatabilityValidator,
    IServiceProvider serviceProvider,
    ILogger<ProfileWriteValidationMiddleware> logger
) : IPipelineStep
{
    public async Task Execute(RequestInfo requestInfo, Func<Task> next)
    {
        // Only process if profile context with WriteContentType exists
        ContentTypeDefinition? writeContentType = requestInfo
            .ProfileContext
            ?.ResourceProfile
            .WriteContentType;

        if (writeContentType == null)
        {
            await next();
            return;
        }

        // Get the request body - only process JSON objects
        if (requestInfo.ParsedBody is not JsonObject requestBody)
        {
            await next();
            return;
        }

        logger.LogDebug(
            "Applying profile write filter. Profile: {ProfileName} - {TraceId}",
            SanitizeForLog(requestInfo.ProfileContext!.ProfileName),
            requestInfo.FrontendRequest.TraceId.Value
        );

        // Pre-compute identity property names for reuse
        HashSet<string> identityPropertyNames = profileFilter.ExtractIdentityPropertyNames(
            requestInfo.ResourceSchema.IdentityJsonPaths
        );

        // For POST requests only, validate that the profile doesn't exclude required fields
        // PUT requests skip this check because existing resources already have the required values
        if (requestInfo.Method == RequestMethod.POST)
        {
            IReadOnlyList<string> excludedRequiredFields = creatabilityValidator.GetExcludedRequiredFields(
                requestInfo.ResourceSchema.RequiredFieldsForInsert,
                writeContentType,
                identityPropertyNames
            );

            if (excludedRequiredFields.Count > 0)
            {
                logger.LogDebug(
                    "Profile {ProfileName} excludes required fields: {ExcludedFields} - {TraceId}",
                    SanitizeForLog(requestInfo.ProfileContext.ProfileName),
                    SanitizeForLog(string.Join(", ", excludedRequiredFields)),
                    requestInfo.FrontendRequest.TraceId.Value
                );

                requestInfo.FrontendResponse = new FrontendResponse(
                    StatusCode: 400,
                    Body: FailureResponse.ForDataPolicyEnforced(
                        requestInfo.ProfileContext.ProfileName,
                        requestInfo.FrontendRequest.TraceId
                    ),
                    Headers: []
                );
                return;
            }
        }

        // Filter the request body according to profile rules
        JsonNode filteredBody = profileFilter.FilterDocument(
            requestBody,
            writeContentType,
            identityPropertyNames
        );

        // For PUT requests, merge stripped fields from the existing document
        // This preserves values for fields the client cannot modify through this profile
        if (requestInfo.Method == RequestMethod.PUT)
        {
            var mergedBody = await MergeWithExistingDocument(
                requestInfo,
                filteredBody as JsonObject ?? new JsonObject(),
                requestBody,
                writeContentType,
                identityPropertyNames
            );

            if (mergedBody == null)
            {
                // If merge failed (e.g., document not found), the response was already set
                return;
            }

            filteredBody = mergedBody;
        }

        // Replace request body with filtered version
        requestInfo.ParsedBody = filteredBody;

        logger.LogDebug(
            "Profile write filter applied successfully - {TraceId}",
            requestInfo.FrontendRequest.TraceId.Value
        );

        await next();
    }

    /// <summary>
    /// For PUT requests, fetches the existing document and merges back fields that were
    /// stripped by the profile filter. This ensures that excluded fields retain their
    /// existing values rather than being deleted.
    /// </summary>
    private async Task<JsonObject?> MergeWithExistingDocument(
        RequestInfo requestInfo,
        JsonObject filteredBody,
        JsonObject originalRequest,
        ContentTypeDefinition writeContentType,
        HashSet<string> identityPropertyNames
    )
    {
        // Get repository from service provider
        var documentStoreRepository = serviceProvider.GetRequiredService<IDocumentStoreRepository>();

        // Create a bypass authorization handler for internal document fetch
        // The actual authorization will be performed by the UpdateByIdHandler later
        var bypassAuthHandler = new BypassResourceAuthorizationHandler();

        var getResult = await documentStoreRepository.GetDocumentById(
            new GetRequest(
                DocumentUuid: requestInfo.PathComponents.DocumentUuid,
                ResourceInfo: requestInfo.ResourceInfo,
                ResourceAuthorizationHandler: bypassAuthHandler,
                TraceId: requestInfo.FrontendRequest.TraceId
            )
        );

        if (getResult is GetFailureNotExists)
        {
            // Document doesn't exist - let the normal update handler return 404
            logger.LogDebug(
                "GetDocumentById returned NotExists for document {DocumentUuid} - {TraceId}",
                requestInfo.PathComponents.DocumentUuid.Value,
                requestInfo.FrontendRequest.TraceId.Value
            );
            return filteredBody;
        }

        if (getResult is not GetSuccess success)
        {
            // Unexpected failure - let it pass through, will be handled later
            logger.LogWarning(
                "Failed to fetch existing document for profile merge: {ResultType} - {TraceId}",
                getResult.GetType().Name,
                requestInfo.FrontendRequest.TraceId.Value
            );
            return filteredBody;
        }

        var existingDoc = success.EdfiDoc as JsonObject;
        if (existingDoc == null)
        {
            logger.LogDebug(
                "Existing document is null or not a JsonObject - {TraceId}",
                requestInfo.FrontendRequest.TraceId.Value
            );
            return filteredBody;
        }

        // Determine which fields were stripped and merge them from existing document
        MergeStrippedFields(
            filteredBody,
            originalRequest,
            existingDoc,
            writeContentType,
            identityPropertyNames
        );

        return filteredBody;
    }

    /// <summary>
    /// Merges fields from the existing document that were stripped by the profile filter.
    /// </summary>
    private static void MergeStrippedFields(
        JsonObject filteredBody,
        JsonObject originalRequest,
        JsonObject existingDoc,
        ContentTypeDefinition writeContentType,
        HashSet<string> identityPropertyNames
    )
    {
        // Find properties that were in the original request but not in the filtered body
        // These are properties that were stripped by the profile filter
        var strippedPropertyNames = originalRequest
            .Select(p => p.Key)
            .Where(propName =>
                !filteredBody.ContainsKey(propName)
                && !identityPropertyNames.Contains(propName)
                && propName is not "id" and not "_etag" and not "_lastModifiedDate"
            );

        foreach (string propName in strippedPropertyNames)
        {
            // This property was stripped - check if it exists in the existing document
            if (
                existingDoc.TryGetPropertyValue(propName, out JsonNode? existingValue)
                && existingValue != null
            )
            {
                // Merge the existing value back
                filteredBody[propName] = existingValue.DeepClone();
            }
        }

        // Also merge any fields that exist in the existing doc but weren't in the request at all
        // This handles the case where the client didn't include a field they can't modify
        var existingPropertyNames = existingDoc
            .Select(p => p.Key)
            .Where(propName =>
                !filteredBody.ContainsKey(propName)
                && !identityPropertyNames.Contains(propName)
                && propName is not "id" and not "_etag" and not "_lastModifiedDate"
            );

        foreach (string propName in existingPropertyNames)
        {
            // Check if this field would be excluded by the profile
            bool wouldBeExcluded = writeContentType.MemberSelection switch
            {
                MemberSelection.IncludeOnly => !writeContentType.PropertyNameSet.Contains(propName)
                    && !writeContentType.CollectionRulesByName.ContainsKey(propName)
                    && !writeContentType.ObjectRulesByName.ContainsKey(propName),
                MemberSelection.ExcludeOnly => writeContentType.PropertyNameSet.Contains(propName),
                _ => false,
            };

            if (
                wouldBeExcluded
                && existingDoc.TryGetPropertyValue(propName, out JsonNode? existingValue)
                && existingValue != null
            )
            {
                // Merge the existing value
                filteredBody[propName] = existingValue.DeepClone();
            }
        }
    }

    /// <summary>
    /// Authorization handler that always authorizes. Used for internal document fetch
    /// when merging stripped fields. The actual authorization is performed later
    /// by the UpdateByIdHandler.
    /// </summary>
    private sealed class BypassResourceAuthorizationHandler : IResourceAuthorizationHandler
    {
        public Task<ResourceAuthorizationResult> Authorize(
            DocumentSecurityElements documentSecurityElements,
            OperationType operationType,
            TraceId traceId
        )
        {
            return Task.FromResult<ResourceAuthorizationResult>(new ResourceAuthorizationResult.Authorized());
        }
    }

    /// <summary>
    /// Sanitizes a string for safe logging by allowing only safe characters.
    /// Uses a whitelist approach to prevent log injection and log forging attacks.
    /// </summary>
    private static string SanitizeForLog(string? input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return string.Empty;
        }

        return new string(
            input
                .Where(c =>
                    char.IsLetterOrDigit(c)
                    || c == ' '
                    || c == '_'
                    || c == '-'
                    || c == '.'
                    || c == ':'
                    || c == '/'
                )
                .ToArray()
        );
    }
}
