// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using EdFi.DataManagementService.Tests.E2E.Extensions;
using FluentAssertions;
using static EdFi.DataManagementService.Tests.E2E.Management.JsonComparer;

namespace EdFi.DataManagementService.Tests.E2E.Management;

/// <summary>
/// Utilities for comparing JSON in E2E tests with dynamic value replacement and metadata handling.
/// Enables robust API response testing by replacing placeholders (IDs, URLs) and managing
/// Ed-Fi metadata fields like '_lastModifiedDate' and '_etag'.
/// </summary>
public static partial class JsonTestUtilities
{
    private static readonly Regex _findIds = IdRegex();

    /// <summary>
    /// Compares expected JSON with actual API response, replacing placeholders and handling metadata.
    /// Supports dynamic ID replacement, URL substitution, and optional metadata removal for robust testing.
    /// </summary>
    public static bool CompareJsonWithPlaceholderReplacement(
        string expectedBody,
        JsonNode actualJson,
        string id = "",
        string dependentId = "",
        string etag = "",
        IReadOnlyDictionary<string, string>? variableReplacements = null,
        string baseUrl = "",
        string oauthUrl = "",
        bool removeMetadataFromActual = true,
        bool removeEtagFromActual = true
    )
    {
        // Process the actual JSON
        JsonNode processedActualJson = actualJson.DeepClone()!;

        if (removeMetadataFromActual)
        {
            CheckAndRemoveMetadata(processedActualJson, removeEtagFromActual);
        }

        // Replace placeholders in expected body
        string processedExpectedBody = ReplacePlaceholders(
            expectedBody,
            actualJson,
            id,
            dependentId,
            etag,
            variableReplacements,
            baseUrl,
            oauthUrl
        );

        JsonNode expectedJson = JsonNode.Parse(processedExpectedBody)!;

        // Remove correlationId from both if present (as it's dynamic)
        (processedActualJson as JsonObject)?.Remove("correlationId");
        (expectedJson as JsonObject)?.Remove("correlationId");

        return AreEqual(expectedJson, processedActualJson);
    }

    /// <summary>
    /// Replaces placeholders in JSON content with values from API responses or provided parameters.
    /// Handles {id}, {dependentId}, {etag}, {BASE_URL}, {OAUTH_URL} and custom variable replacements.
    /// </summary>
    public static string ReplacePlaceholders(
        string body,
        JsonNode responseJson,
        string id = "",
        string dependentId = "",
        string etag = "",
        IReadOnlyDictionary<string, string>? variableReplacements = null,
        string baseUrl = "",
        string oauthUrl = ""
    )
    {
        string replacedBody = "";
        if (body.TrimStart().StartsWith('['))
        {
            var responseAsArray = responseJson.AsArray();
            if (responseAsArray == null || responseAsArray.Count == 0)
            {
                return body;
            }

            int index = 0;
            replacedBody = _findIds.Replace(
                body,
                match =>
                {
                    var idValue = responseJson[index]?["id"]?.ToString();
                    index++;
                    return idValue ?? match.ToString();
                }
            );
        }
        else
        {
            replacedBody = _findIds.Replace(
                body,
                match =>
                {
                    var idValue = responseJson["id"]?.ToString();
                    return idValue ?? match.ToString();
                }
            );
        }

        // Replace other placeholders
        replacedBody = replacedBody
            .Replace("{id}", id)
            .Replace("{dependentId}", dependentId)
            .Replace("{etag}", etag);

        if (!string.IsNullOrEmpty(baseUrl))
        {
            replacedBody = replacedBody.Replace("{BASE_URL}/", baseUrl);
        }

        if (!string.IsNullOrEmpty(oauthUrl))
        {
            replacedBody = replacedBody.Replace("{OAUTH_URL}", oauthUrl);
        }

        if (variableReplacements != null)
        {
            replacedBody = replacedBody.ReplacePlaceholdersWithDictionaryValues(variableReplacements);
        }

        return replacedBody;
    }

    /// <summary>
    /// Performs deep JSON comparison with property ordering normalization.
    /// </summary>
    private static bool AreEqual(JsonNode expectedBodyJson, JsonNode responseJson)
    {
        JsonNode orderedResponseJson = OrderJsonProperties(responseJson);
        JsonNode orderedExpectedJson = OrderJsonProperties(expectedBodyJson);

        JsonElement expectedElement = JsonDocument.Parse(orderedExpectedJson.ToJsonString()).RootElement;
        JsonElement responseElement = JsonDocument.Parse(orderedResponseJson.ToJsonString()).RootElement;

        return JsonElementEqualityComparer.Instance.Equals(expectedElement, responseElement);
    }

    /// <summary>
    /// Validates and optionally removes Ed-Fi metadata fields ('_lastModifiedDate', '_etag')
    /// from JSON responses to prevent test failures due to dynamic values.
    /// </summary>
    private static void CheckAndRemoveMetadata(JsonNode responseJson, bool removeEtag)
    {
        if (responseJson is JsonArray jsonArray && jsonArray.Count > 0)
        {
            foreach (JsonObject? item in jsonArray.Cast<JsonObject?>())
            {
                if (item != null)
                {
                    var lastModifiedDate = LastModifiedDate(item);
                    lastModifiedDate.Should().NotBeNull();
                    item.Remove("_lastModifiedDate");

                    var eTag = Etag(item);
                    eTag.Should().NotBeNull();
                    if (removeEtag)
                    {
                        item.Remove("_etag");
                    }
                }
            }
        }
        else if (responseJson is JsonObject jsonObject && jsonObject.Count > 0)
        {
            var lastModifiedDate = LastModifiedDate(responseJson);
            lastModifiedDate.Should().NotBeNull();
            (responseJson as JsonObject)?.Remove("_lastModifiedDate");

            var eTag = Etag(responseJson);
            eTag.Should().NotBeNull();
            if (removeEtag)
            {
                (responseJson as JsonObject)?.Remove("_etag");
            }
        }
    }

    /// <summary>
    /// Extracts the '_lastModifiedDate' metadata field from a JSON response.
    /// </summary>
    private static string? LastModifiedDate(JsonNode response)
    {
        if (
            response is JsonObject jsonObject
            && jsonObject.TryGetPropertyValue("_lastModifiedDate", out JsonNode? lastModifiedDate)
            && lastModifiedDate != null
        )
        {
            return lastModifiedDate.GetValue<string?>();
        }
        return null;
    }

    /// <summary>
    /// Extracts the '_etag' metadata field from a JSON response.
    /// </summary>
    private static string? Etag(JsonNode response)
    {
        if (
            response is JsonObject jsonObject
            && jsonObject.TryGetPropertyValue("_etag", out JsonNode? etag)
            && etag != null
        )
        {
            return etag.GetValue<string?>();
        }
        return null;
    }

    /// <summary>
    /// Compiled regex pattern for finding and replacing {id} placeholders in JSON content.
    /// </summary>
    [GeneratedRegex(@"\{id\}", RegexOptions.Compiled)]
    private static partial Regex IdRegex();
}
