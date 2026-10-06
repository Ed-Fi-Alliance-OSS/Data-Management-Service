// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.Tests.Unit.OpenApi;
using Json.Schema;

namespace EdFi.DataManagementService.Tests.Integration.Plugins.Identity;

/// <summary>
/// The identity OpenAPI document as the running host serves it, with the helpers a case needs to
/// hold a real response to what the document promises.
/// </summary>
/// <remarks>
/// Evaluation goes through <see cref="OpenApiSchemaNormalizer"/>, linked from the Core unit tests,
/// so the served document is judged by the same rewrite the Core conformance tests use.
/// </remarks>
internal sealed class IdentityServedOpenApi
{
    public const string DocumentPath = "/metadata/identity/v2/swagger.json";

    /// <summary>The value every normalized <c>correlationId</c> is rewritten to.</summary>
    public const string NormalizedCorrelationId = "normalized-correlation-id";

    private IdentityServedOpenApi(JsonNode document)
    {
        Document = document;
    }

    public JsonNode Document { get; }

    public JsonObject Schemas => Document["components"]!["schemas"]!.AsObject();

    public JsonObject Paths => Document["paths"]!.AsObject();

    public static async Task<IdentityServedOpenApi> FetchAsync(HttpClient client)
    {
        using HttpResponseMessage response = await client.GetAsync(DocumentPath);
        string body = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"GET {DocumentPath} returned {(int)response.StatusCode}: {body}"
            );
        }

        return new IdentityServedOpenApi(
            JsonNode.Parse(body)
                ?? throw new InvalidOperationException($"GET {DocumentPath} returned a null document.")
        );
    }

    /// <summary>
    /// Follows a <c>#/components/...</c> reference to the node it names within the served document,
    /// or returns the node unchanged when it is not a reference.
    /// </summary>
    public JsonNode Resolve(JsonNode node)
    {
        string? refValue = node["$ref"]?.GetValue<string>();

        if (refValue is null)
        {
            return node;
        }

        JsonNode current = Document;

        foreach (string segment in refValue[2..].Split('/'))
        {
            current = current[segment]!;
        }

        return current;
    }

    /// <summary>The response a path operation declares for a status, with a component reference followed.</summary>
    public JsonNode Response(string path, string method, string status) =>
        Resolve(Paths[path]![method]!["responses"]![status]!);

    /// <summary>
    /// The compiled schema an operation declares for one status and media type, with component
    /// references resolved.
    /// </summary>
    public JsonSchema ResponseSchema(
        string path,
        string method,
        string status,
        string mediaType = "application/json"
    ) =>
        OpenApiSchemaNormalizer.BuildJsonSchema(
            Schemas,
            Response(path, method, status)["content"]![mediaType]!["schema"]!
        );

    /// <summary>
    /// Whether <paramref name="body"/> conforms to the schema the operation declares for the status.
    /// </summary>
    public bool Conforms(
        string path,
        string method,
        string status,
        JsonNode body,
        string mediaType = "application/json"
    ) => ResponseSchema(path, method, status, mediaType).Evaluate(body).IsValid;

    /// <summary>The value of a pinned example, which the document declares under its media type.</summary>
    public JsonNode Example(
        string path,
        string method,
        string status,
        string exampleName,
        string mediaType = "application/json"
    ) => Response(path, method, status)["content"]![mediaType]!["examples"]![exampleName]!["value"]!;

    /// <summary>
    /// A copy of <paramref name="node"/> with every <c>correlationId</c> rewritten to
    /// <see cref="NormalizedCorrelationId"/>. The pinned bodies carry a fixed correlation id and a
    /// live response carries its own, so a comparison normalizes both.
    /// </summary>
    public static JsonNode NormalizeCorrelationId(JsonNode node)
    {
        JsonNode copy = node.DeepClone();
        RewriteCorrelationIds(copy);
        return copy;
    }

    /// <summary>
    /// The node serialized with object properties in ordinal order at every depth, so two documents
    /// that differ only in property order are equal as text.
    /// </summary>
    public static string Canonicalize(JsonNode node) => Sort(node).ToJsonString(CanonicalOptions);

    /// <summary>Whether two documents are equal once property order is ignored.</summary>
    public static bool AreCanonicallyEqual(JsonNode left, JsonNode right) =>
        string.Equals(Canonicalize(left), Canonicalize(right), StringComparison.Ordinal);

    private static readonly JsonSerializerOptions CanonicalOptions = new() { WriteIndented = false };

    private static void RewriteCorrelationIds(JsonNode node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (string key in obj.Select(pair => pair.Key).ToList())
                {
                    if (string.Equals(key, "correlationId", StringComparison.Ordinal))
                    {
                        obj[key] = NormalizedCorrelationId;
                    }
                    else if (obj[key] is JsonNode child)
                    {
                        RewriteCorrelationIds(child);
                    }
                }
                break;
            case JsonArray array:
                foreach (JsonNode? element in array)
                {
                    if (element is not null)
                    {
                        RewriteCorrelationIds(element);
                    }
                }
                break;
        }
    }

    private static JsonNode Sort(JsonNode node)
    {
        switch (node)
        {
            case JsonObject obj:
                JsonObject sorted = [];

                foreach (
                    (string key, JsonNode? value) in obj.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                )
                {
                    sorted[key] = value is null ? null : Sort(value.DeepClone());
                }

                return sorted;
            case JsonArray array:
                JsonArray sortedArray = [];

                foreach (JsonNode? element in array)
                {
                    sortedArray.Add(element is null ? null : Sort(element.DeepClone()));
                }

                return sortedArray;
            default:
                return node.DeepClone();
        }
    }
}
