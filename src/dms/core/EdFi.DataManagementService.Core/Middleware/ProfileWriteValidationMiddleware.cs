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
            // Debug logging to understand why filtering is skipped
            if (requestInfo.ProfileContext == null)
            {
                logger.LogDebug(
                    "ProfileWriteValidationMiddleware: Skipping - ProfileContext is null. TraceId: {TraceId}",
                    requestInfo.FrontendRequest.TraceId.Value
                );
            }
            else if (requestInfo.ProfileContext.ResourceProfile.WriteContentType == null)
            {
                logger.LogDebug(
                    "ProfileWriteValidationMiddleware: Skipping - WriteContentType is null for profile {ProfileName}. TraceId: {TraceId}",
                    SanitizeForLog(requestInfo.ProfileContext.ProfileName),
                    requestInfo.FrontendRequest.TraceId.Value
                );
            }

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
    /// <remarks>
    /// NOTE: This approach requires an additional database read before the update operation,
    /// which is sub-optimal from a performance perspective. A future optimization could move
    /// this logic to the backend where the update statement could explicitly update only the
    /// fields included in the profile, eliminating the need for a separate read operation.
    /// </remarks>
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

        logger.LogDebug(
            "ProfileWriteValidationMiddleware: Fetching existing document for merge. DocumentUuid: {DocumentUuid}, ResourceName: {ResourceName} - {TraceId}",
            requestInfo.PathComponents.DocumentUuid.Value,
            SanitizeForLog(requestInfo.ResourceSchema.ResourceName.Value),
            requestInfo.FrontendRequest.TraceId.Value
        );

        var getResult = await documentStoreRepository.GetDocumentById(
            new GetRequest(
                DocumentUuid: requestInfo.PathComponents.DocumentUuid,
                ResourceName: requestInfo.ResourceSchema.ResourceName,
                ResourceAuthorizationHandler: bypassAuthHandler,
                TraceId: requestInfo.FrontendRequest.TraceId
            )
        );

        if (getResult is GetFailureNotExists)
        {
            // Document doesn't exist - let the normal update handler return 404
            logger.LogDebug(
                "ProfileWriteValidationMiddleware: GetDocumentById returned NotExists. DocumentUuid: {DocumentUuid}, ResourceName: {ResourceName} - {TraceId}",
                requestInfo.PathComponents.DocumentUuid.Value,
                SanitizeForLog(requestInfo.ResourceSchema.ResourceName.Value),
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

        if (success.EdfiDoc is not JsonObject existingDoc)
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
    /// This includes top-level fields, nested objects, and collection items.
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

        // Recursively merge nested objects
        foreach (var kvp in writeContentType.ObjectRulesByName)
        {
            string objectName = kvp.Key;
            ObjectRule objectRule = kvp.Value;

            if (
                filteredBody.TryGetPropertyValue(objectName, out JsonNode? filteredNestedNode)
                && filteredNestedNode is JsonObject filteredNestedObject
                && originalRequest.TryGetPropertyValue(objectName, out JsonNode? originalNestedNode)
                && originalNestedNode is JsonObject originalNestedObject
                && existingDoc.TryGetPropertyValue(objectName, out JsonNode? existingNestedNode)
                && existingNestedNode is JsonObject existingNestedObject
            )
            {
                MergeNestedObjectStrippedFields(
                    filteredNestedObject,
                    originalNestedObject,
                    existingNestedObject,
                    objectRule
                );
            }
        }

        // Recursively merge collections
        foreach (var kvp in writeContentType.CollectionRulesByName)
        {
            string collectionName = kvp.Key;
            CollectionRule collectionRule = kvp.Value;

            if (
                filteredBody.TryGetPropertyValue(collectionName, out JsonNode? filteredCollectionNode)
                && filteredCollectionNode is JsonArray filteredCollection
                && originalRequest.TryGetPropertyValue(collectionName, out JsonNode? originalCollectionNode)
                && originalCollectionNode is JsonArray originalCollection
                && existingDoc.TryGetPropertyValue(collectionName, out JsonNode? existingCollectionNode)
                && existingCollectionNode is JsonArray existingCollection
            )
            {
                MergeCollectionStrippedFields(
                    filteredCollection,
                    originalCollection,
                    existingCollection,
                    collectionRule
                );
            }
        }
    }

    /// <summary>
    /// Recursively merges stripped fields within a nested object.
    /// </summary>
    private static void MergeNestedObjectStrippedFields(
        JsonObject filteredNestedObject,
        JsonObject originalNestedObject,
        JsonObject existingNestedObject,
        ObjectRule objectRule
    )
    {
        // Find properties that were stripped from this nested object
        var strippedPropertyNames = originalNestedObject
            .Select(p => p.Key)
            .Where(propName => !filteredNestedObject.ContainsKey(propName));

        foreach (string propName in strippedPropertyNames)
        {
            if (
                existingNestedObject.TryGetPropertyValue(propName, out JsonNode? existingValue)
                && existingValue != null
            )
            {
                filteredNestedObject[propName] = existingValue.DeepClone();
            }
        }

        // Merge fields from existing doc that weren't in request but are excluded by profile
        var existingOnlyPropertyNames = existingNestedObject
            .Select(p => p.Key)
            .Where(propName => !filteredNestedObject.ContainsKey(propName));

        foreach (string propName in existingOnlyPropertyNames)
        {
            bool wouldBeExcluded = objectRule.MemberSelection switch
            {
                MemberSelection.IncludeOnly => !objectRule.PropertyNameSet.Contains(propName)
                    && !objectRule.CollectionRulesByName.ContainsKey(propName)
                    && !objectRule.NestedObjectRulesByName.ContainsKey(propName),
                MemberSelection.ExcludeOnly => objectRule.PropertyNameSet.Contains(propName),
                _ => false,
            };

            if (
                wouldBeExcluded
                && existingNestedObject.TryGetPropertyValue(propName, out JsonNode? existingValue)
                && existingValue != null
            )
            {
                filteredNestedObject[propName] = existingValue.DeepClone();
            }
        }

        // Recursively handle nested objects within this object
        foreach (var kvp in objectRule.NestedObjectRulesByName)
        {
            string nestedObjectName = kvp.Key;
            ObjectRule nestedObjectRule = kvp.Value;

            if (
                filteredNestedObject.TryGetPropertyValue(nestedObjectName, out JsonNode? filteredInnerNode)
                && filteredInnerNode is JsonObject filteredInnerObject
                && originalNestedObject.TryGetPropertyValue(nestedObjectName, out JsonNode? originalInnerNode)
                && originalInnerNode is JsonObject originalInnerObject
                && existingNestedObject.TryGetPropertyValue(nestedObjectName, out JsonNode? existingInnerNode)
                && existingInnerNode is JsonObject existingInnerObject
            )
            {
                MergeNestedObjectStrippedFields(
                    filteredInnerObject,
                    originalInnerObject,
                    existingInnerObject,
                    nestedObjectRule
                );
            }
        }

        // Recursively handle collections within this object
        foreach (var kvp in objectRule.CollectionRulesByName)
        {
            string collectionName = kvp.Key;
            CollectionRule collectionRule = kvp.Value;

            if (
                filteredNestedObject.TryGetPropertyValue(collectionName, out JsonNode? filteredCollectionNode)
                && filteredCollectionNode is JsonArray filteredCollection
                && originalNestedObject.TryGetPropertyValue(
                    collectionName,
                    out JsonNode? originalCollectionNode
                )
                && originalCollectionNode is JsonArray originalCollection
                && existingNestedObject.TryGetPropertyValue(
                    collectionName,
                    out JsonNode? existingCollectionNode
                )
                && existingCollectionNode is JsonArray existingCollection
            )
            {
                MergeCollectionStrippedFields(
                    filteredCollection,
                    originalCollection,
                    existingCollection,
                    collectionRule
                );
            }
        }
    }

    /// <summary>
    /// Merges stripped fields within collection items and preserves items filtered by ItemFilter.
    /// </summary>
    private static void MergeCollectionStrippedFields(
        JsonArray filteredCollection,
        JsonArray originalCollection,
        JsonArray existingCollection,
        CollectionRule collectionRule
    )
    {
        // First, merge stripped fields within each filtered collection item
        foreach (JsonNode? filteredItemNode in filteredCollection)
        {
            if (filteredItemNode is not JsonObject filteredItem)
            {
                continue;
            }

            // Find the corresponding original item (needed for merging stripped properties)
            JsonObject? originalItem = FindMatchingItemInArray(filteredItem, originalCollection);
            if (originalItem == null)
            {
                continue;
            }

            // Find the corresponding existing item using filteredItem (not originalItem)
            // because originalItem may have excluded properties with different values that
            // would cause a mismatch. filteredItem only has non-excluded properties.
            JsonObject? existingItem = FindMatchingExistingItem(
                filteredItem,
                existingCollection,
                collectionRule
            );

            if (existingItem != null)
            {
                MergeCollectionItemStrippedFields(filteredItem, originalItem, existingItem, collectionRule);
            }
        }

        // Second, add back items that were filtered out by ItemFilter
        // These items should be preserved from the existing document
        if (collectionRule.ItemFilter != null)
        {
            var itemsToAdd = new List<JsonNode>();

            foreach (JsonNode? existingItemNode in existingCollection)
            {
                if (existingItemNode is not JsonObject existingItem)
                {
                    continue;
                }

                // Check if this item was filtered out by ItemFilter
                if (WasFilteredOutByItemFilter(existingItem, collectionRule.ItemFilter))
                {
                    // This item cannot be modified via this profile - preserve it
                    itemsToAdd.Add(existingItem.DeepClone());
                }
            }

            // Add filtered items back to the collection
            foreach (var item in itemsToAdd)
            {
                filteredCollection.Add(item);
            }
        }
    }

    /// <summary>
    /// Merges stripped fields within a single collection item.
    /// </summary>
    private static void MergeCollectionItemStrippedFields(
        JsonObject filteredItem,
        JsonObject originalItem,
        JsonObject existingItem,
        CollectionRule collectionRule
    )
    {
        // Find properties that were stripped from this collection item
        var strippedPropertyNames = originalItem
            .Select(p => p.Key)
            .Where(propName => !filteredItem.ContainsKey(propName));

        foreach (string propName in strippedPropertyNames)
        {
            if (
                existingItem.TryGetPropertyValue(propName, out JsonNode? existingValue)
                && existingValue != null
            )
            {
                filteredItem[propName] = existingValue.DeepClone();
            }
        }

        // Merge fields from existing doc that weren't in request but are excluded by profile
        var existingOnlyPropertyNames = existingItem
            .Select(p => p.Key)
            .Where(propName => !filteredItem.ContainsKey(propName));

        foreach (string propName in existingOnlyPropertyNames)
        {
            bool wouldBeExcluded = collectionRule.MemberSelection switch
            {
                MemberSelection.IncludeOnly => !collectionRule.PropertyNameSet.Contains(propName)
                    && !collectionRule.NestedCollectionRulesByName.ContainsKey(propName)
                    && !collectionRule.NestedObjectRulesByName.ContainsKey(propName),
                MemberSelection.ExcludeOnly => collectionRule.PropertyNameSet.Contains(propName),
                _ => false,
            };

            if (
                wouldBeExcluded
                && existingItem.TryGetPropertyValue(propName, out JsonNode? existingValue)
                && existingValue != null
            )
            {
                filteredItem[propName] = existingValue.DeepClone();
            }
        }

        // Recursively handle nested objects within collection items
        foreach (var kvp in collectionRule.NestedObjectRulesByName)
        {
            string nestedObjectName = kvp.Key;
            ObjectRule nestedObjectRule = kvp.Value;

            if (
                filteredItem.TryGetPropertyValue(nestedObjectName, out JsonNode? filteredNestedNode)
                && filteredNestedNode is JsonObject filteredNestedObject
                && originalItem.TryGetPropertyValue(nestedObjectName, out JsonNode? originalNestedNode)
                && originalNestedNode is JsonObject originalNestedObject
                && existingItem.TryGetPropertyValue(nestedObjectName, out JsonNode? existingNestedNode)
                && existingNestedNode is JsonObject existingNestedObject
            )
            {
                MergeNestedObjectStrippedFields(
                    filteredNestedObject,
                    originalNestedObject,
                    existingNestedObject,
                    nestedObjectRule
                );
            }
        }

        // Recursively handle nested collections within collection items
        foreach (var kvp in collectionRule.NestedCollectionRulesByName)
        {
            string nestedCollectionName = kvp.Key;
            CollectionRule nestedCollectionRule = kvp.Value;

            if (
                filteredItem.TryGetPropertyValue(
                    nestedCollectionName,
                    out JsonNode? filteredNestedCollectionNode
                )
                && filteredNestedCollectionNode is JsonArray filteredNestedCollection
                && originalItem.TryGetPropertyValue(
                    nestedCollectionName,
                    out JsonNode? originalNestedCollectionNode
                )
                && originalNestedCollectionNode is JsonArray originalNestedCollection
                && existingItem.TryGetPropertyValue(
                    nestedCollectionName,
                    out JsonNode? existingNestedCollectionNode
                )
                && existingNestedCollectionNode is JsonArray existingNestedCollection
            )
            {
                MergeCollectionStrippedFields(
                    filteredNestedCollection,
                    originalNestedCollection,
                    existingNestedCollection,
                    nestedCollectionRule
                );
            }
        }
    }

    /// <summary>
    /// Finds a matching item in the existing collection based on the collection rule's ItemFilter
    /// property (if available) or by comparing all properties.
    /// </summary>
    private static JsonObject? FindMatchingExistingItem(
        JsonObject requestItem,
        JsonArray existingCollection,
        CollectionRule collectionRule
    )
    {
        // If there's an ItemFilter, use its property as the identity key
        if (collectionRule.ItemFilter != null)
        {
            string filterPropertyName = collectionRule.ItemFilter.PropertyName;
            if (
                requestItem.TryGetPropertyValue(filterPropertyName, out JsonNode? requestValue)
                && requestValue != null
            )
            {
                string? requestValueStr = requestValue.GetValue<string>();
                foreach (JsonNode? existingItemNode in existingCollection)
                {
                    if (
                        existingItemNode is JsonObject existingItem
                        && existingItem.TryGetPropertyValue(filterPropertyName, out JsonNode? existingValue)
                        && existingValue != null
                        && existingValue.GetValue<string>() == requestValueStr
                    )
                    {
                        return existingItem;
                    }
                }
            }
        }

        // Fallback: Try to find by matching all common properties
        return FindMatchingItemByAllProperties(requestItem, existingCollection);
    }

    /// <summary>
    /// Finds a matching item in an array by comparing common properties.
    /// Used for matching filtered items back to original items.
    /// </summary>
    private static JsonObject? FindMatchingItemInArray(JsonObject item, JsonArray array)
    {
        foreach (JsonNode? arrayItemNode in array)
        {
            if (arrayItemNode is not JsonObject arrayItem)
            {
                continue;
            }

            // Check if all properties in the filtered item match
            bool allMatch = true;
            foreach (var kvp in item)
            {
                if (!arrayItem.TryGetPropertyValue(kvp.Key, out JsonNode? arrayValue))
                {
                    allMatch = false;
                    break;
                }

                if (kvp.Value == null && arrayValue == null)
                {
                    continue;
                }

                if (
                    kvp.Value == null
                    || arrayValue == null
                    || kvp.Value.ToJsonString() != arrayValue.ToJsonString()
                )
                {
                    allMatch = false;
                    break;
                }
            }

            if (allMatch)
            {
                return arrayItem;
            }
        }

        return null;
    }

    /// <summary>
    /// Finds a matching item in the existing collection by comparing all common properties.
    /// </summary>
    private static JsonObject? FindMatchingItemByAllProperties(
        JsonObject requestItem,
        JsonArray existingCollection
    )
    {
        foreach (JsonNode? existingItemNode in existingCollection)
        {
            if (existingItemNode is not JsonObject existingItem)
            {
                continue;
            }

            // Count matching properties (at least one should match for identity)
            int matchingProps = 0;
            int totalProps = 0;
            bool hasMismatch = false;

            foreach (var kvp in requestItem)
            {
                totalProps++;
                if (!existingItem.TryGetPropertyValue(kvp.Key, out JsonNode? existingValue))
                {
                    continue;
                }

                if (kvp.Value == null && existingValue == null)
                {
                    matchingProps++;
                    continue;
                }

                if (
                    kvp.Value != null
                    && existingValue != null
                    && kvp.Value.ToJsonString() == existingValue.ToJsonString()
                )
                {
                    matchingProps++;
                }
                else
                {
                    hasMismatch = true;
                }
            }

            // If all checked properties match and we have at least one match, consider it a match
            if (!hasMismatch && matchingProps > 0)
            {
                return existingItem;
            }
        }

        return null;
    }

    /// <summary>
    /// Checks if an item was filtered out by the CollectionItemFilter.
    /// Returns true if the item does NOT pass the filter (i.e., was excluded).
    /// </summary>
    private static bool WasFilteredOutByItemFilter(JsonObject item, CollectionItemFilter? itemFilter)
    {
        if (itemFilter == null)
        {
            return false;
        }

        if (!item.TryGetPropertyValue(itemFilter.PropertyName, out JsonNode? propertyValue))
        {
            // If property doesn't exist, it doesn't match the filter values
            // In IncludeOnly mode, items without the property are filtered out
            // In ExcludeOnly mode, items without the property are kept
            return itemFilter.FilterMode == FilterMode.IncludeOnly;
        }

        string? valueStr = propertyValue?.GetValue<string>();
        bool matchesFilter = itemFilter.Values.Contains(valueStr ?? string.Empty);

        return itemFilter.FilterMode switch
        {
            // IncludeOnly: item was filtered OUT if it does NOT match the values
            FilterMode.IncludeOnly => !matchesFilter,
            // ExcludeOnly: item was filtered OUT if it DOES match the values
            FilterMode.ExcludeOnly => matchesFilter,
            _ => false,
        };
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
