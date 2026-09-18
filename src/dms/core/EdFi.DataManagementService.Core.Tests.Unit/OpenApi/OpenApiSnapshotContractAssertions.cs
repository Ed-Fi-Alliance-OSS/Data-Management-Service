// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;

namespace EdFi.DataManagementService.Core.Tests.Unit.OpenApi;

/// <summary>
/// Inspection primitives for the snapshot OpenAPI contract (DMS-1369), shared by the served-document,
/// profile-filter, and package-payload fixtures so they all judge the same document the same way.
/// </summary>
/// <remarks>
/// <para>
/// Two things are hard to assert by hand and easy to get wrong. The first is self-resolution: every
/// independently served document must define the components it references, because a document cannot
/// resolve <c>#/components/parameters/Use-Snapshot</c> out of a sibling document. The second is
/// operation coverage, where the interesting distinctions - GET-many against GET-by-id, and
/// <c>/deletes</c> and <c>/keyChanges</c> against their base collection - live in the path string
/// rather than in the operation.
/// </para>
/// <para>
/// This deliberately reports rather than throws, so a fixture can name the document in its own failure
/// message and so a caller can assert on what was found rather than only on that something failed.
/// </para>
/// </remarks>
internal static class OpenApiSnapshotContractAssertions
{
    public const string UseSnapshotParameterName = "Use-Snapshot";
    public const string SnapshotNotFoundResponseName = "SnapshotNotFound";
    public const string SnapshotMethodNotAllowedResponseName = "SnapshotMethodNotAllowed";
    public const string ProblemDetailsSchemaName = "ProblemDetails";

    public const string UseSnapshotParameterReference = "#/components/parameters/Use-Snapshot";
    public const string SnapshotNotFoundResponseReference = "#/components/responses/SnapshotNotFound";
    public const string SnapshotMethodNotAllowedResponseReference =
        "#/components/responses/SnapshotMethodNotAllowed";

    public const string ProblemJsonContentType = "application/problem+json";

    public const string NotFoundStatusCode = "404";
    public const string MethodNotAllowedStatusCode = "405";

    /// <summary>
    /// The standalone Change Queries route, matched exactly the way
    /// <c>ProfileOpenApiSpecificationFilter</c> matches it.
    /// </summary>
    public const string AvailableChangeVersionsPath = "/availableChangeVersions";

    private const string LocalReferencePrefix = "#";

    /// <summary>
    /// The operation keys of a path item. Everything else a path item may carry - <c>parameters</c>,
    /// <c>summary</c>, <c>servers</c>, an <c>x-</c> extension - is not an operation.
    /// </summary>
    private static readonly string[] _httpMethodNames =
    [
        "get",
        "put",
        "post",
        "delete",
        "options",
        "head",
        "patch",
        "trace",
    ];

    /// <summary>
    /// What a path is, as far as the snapshot contract is concerned. The distinction is carried by the
    /// path string because the operations themselves are indistinguishable.
    /// </summary>
    public enum OperationPathKind
    {
        /// <summary>A resource or descriptor collection, e.g. <c>/ed-fi/schools</c>.</summary>
        Collection,

        /// <summary>A single item by id, e.g. <c>/ed-fi/schools/{id}</c>.</summary>
        Item,

        /// <summary>A tracked-change tombstone feed, e.g. <c>/ed-fi/schools/deletes</c>.</summary>
        Deletes,

        /// <summary>A tracked-change key-change feed, e.g. <c>/ed-fi/schools/keyChanges</c>.</summary>
        KeyChanges,

        /// <summary>A cursor-paging partition feed, e.g. <c>/ed-fi/schools/partitions</c>.</summary>
        Partitions,

        /// <summary>The standalone Change Queries route.</summary>
        AvailableChangeVersions,
    }

    /// <summary>
    /// A reference that does not resolve inside its own document, with where it was found and why it
    /// failed, so a failure names the operation rather than only the document.
    /// </summary>
    public sealed record UnresolvedReference(string Location, string Reference, string Reason)
    {
        public override string ToString() => $"{Location} -> '{Reference}' ({Reason})";
    }

    /// <summary>
    /// One operation, flattened to what the snapshot contract cares about.
    /// </summary>
    /// <param name="ParameterReferences">
    /// The <c>$ref</c> targets of the operation's parameters, including any the path item declares for
    /// every operation under it. Path-level parameters are merged in because OpenAPI allows the
    /// contract to be expressed either way, and a fixture asserting coverage should not fail merely
    /// because upstream hoisted a parameter.
    /// </param>
    public sealed record Operation(
        string PathKey,
        string Method,
        OperationPathKind PathKind,
        IReadOnlyList<string> ParameterReferences,
        IReadOnlyList<string> InlineParameterNames,
        IReadOnlyDictionary<string, string> ResponseReferencesByStatusCode,
        IReadOnlyCollection<string> ResponseStatusCodes
    )
    {
        public bool ReferencesUseSnapshotParameter =>
            ParameterReferences.Contains(UseSnapshotParameterReference, StringComparer.Ordinal);

        public bool ReferencesSnapshotNotFound =>
            ResponseReferencesByStatusCode.TryGetValue(NotFoundStatusCode, out string? reference)
            && string.Equals(reference, SnapshotNotFoundResponseReference, StringComparison.Ordinal);

        public bool ReferencesSnapshotMethodNotAllowed =>
            ResponseReferencesByStatusCode.TryGetValue(MethodNotAllowedStatusCode, out string? reference)
            && string.Equals(reference, SnapshotMethodNotAllowedResponseReference, StringComparison.Ordinal);

        public override string ToString() => $"{Method.ToUpperInvariant()} {PathKey}";
    }

    /// <summary>
    /// Returns a <c>components</c> entry, or null when the section or the entry is absent.
    /// </summary>
    public static JsonNode? Component(JsonNode document, string section, string name) =>
        document["components"]?[section]?[name];

    /// <summary>
    /// Finds every reference in the document that does not resolve inside that same document. An empty
    /// result is what "self-resolving" means.
    /// </summary>
    /// <remarks>
    /// A reference to another file is reported too: a document that reaches outside itself is not
    /// independently servable, whether or not the target exists on disk somewhere.
    /// </remarks>
    public static IReadOnlyList<UnresolvedReference> FindUnresolvedReferences(JsonNode document)
    {
        List<UnresolvedReference> unresolved = [];
        Collect(document, "$");
        return unresolved;

        void Collect(JsonNode? node, string location)
        {
            if (node is JsonObject jsonObject)
            {
                CollectFromObject(jsonObject, location);
            }
            else if (node is JsonArray jsonArray)
            {
                for (int index = 0; index < jsonArray.Count; index++)
                {
                    Collect(jsonArray[index], $"{location}[{index}]");
                }
            }
        }

        void CollectFromObject(JsonObject jsonObject, string location)
        {
            foreach ((string key, JsonNode? value) in jsonObject)
            {
                string childLocation = location + FormatPathSegment(key);

                if (string.Equals(key, "$ref", StringComparison.Ordinal))
                {
                    InspectReference(value, childLocation);
                    continue;
                }

                // An "example" is request or response data rather than schema, so a "$ref"-shaped key
                // inside one is a value and not a reference. "examples" is not skipped: those are
                // Example Objects, which may legitimately reference #/components/examples.
                if (string.Equals(key, "example", StringComparison.Ordinal))
                {
                    continue;
                }

                Collect(value, childLocation);
            }
        }

        void InspectReference(JsonNode? value, string location)
        {
            if (value is not JsonValue referenceValue || !referenceValue.TryGetValue(out string? reference))
            {
                unresolved.Add(
                    new UnresolvedReference(location, value?.ToJsonString() ?? "null", "not a string")
                );
                return;
            }

            if (!reference.StartsWith(LocalReferencePrefix, StringComparison.Ordinal))
            {
                unresolved.Add(
                    new UnresolvedReference(
                        location,
                        reference,
                        "not a local reference, so the document is not independently servable"
                    )
                );
                return;
            }

            if (!TryResolveLocalReference(document, reference, out string? reason))
            {
                unresolved.Add(new UnresolvedReference(location, reference, reason));
            }
        }
    }

    /// <summary>
    /// Resolves a local reference against the document root, per RFC 6901 JSON Pointer.
    /// </summary>
    private static bool TryResolveLocalReference(JsonNode document, string reference, out string reason)
    {
        reason = string.Empty;

        // "#" alone is the whole document, which always resolves.
        if (reference.Length == 1)
        {
            return true;
        }

        if (!reference.StartsWith("#/", StringComparison.Ordinal))
        {
            reason = "not a JSON Pointer fragment";
            return false;
        }

        JsonNode? current = document;

        foreach (string rawToken in reference[2..].Split('/'))
        {
            string token = UnescapePointerToken(rawToken);

            if (current is JsonObject currentObject)
            {
                if (!currentObject.TryGetPropertyValue(token, out current))
                {
                    reason = $"no '{token}' entry";
                    return false;
                }

                continue;
            }

            if (
                current is JsonArray currentArray
                && int.TryParse(token, out int index)
                && index >= 0
                && index < currentArray.Count
            )
            {
                current = currentArray[index];
                continue;
            }

            reason = $"cannot traverse '{token}'";
            return false;
        }

        if (current is null)
        {
            reason = "resolves to null";
            return false;
        }

        return true;
    }

    private static string UnescapePointerToken(string token) =>
        token.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);

    /// <summary>
    /// Flattens every operation in the document's <c>paths</c>.
    /// </summary>
    public static IReadOnlyList<Operation> EnumerateOperations(JsonNode document)
    {
        List<Operation> operations = [];

        if (document["paths"] is not JsonObject paths)
        {
            return operations;
        }

        foreach ((string pathKey, JsonNode? pathValue) in paths)
        {
            if (pathValue is not JsonObject pathItem)
            {
                continue;
            }

            OperationPathKind pathKind = ClassifyPath(pathKey);
            IReadOnlyList<string> pathLevelParameterReferences = ParameterReferencesOf(pathItem);
            IReadOnlyList<string> pathLevelParameterNames = InlineParameterNamesOf(pathItem);

            foreach (string method in _httpMethodNames)
            {
                if (pathItem[method] is not JsonObject operation)
                {
                    continue;
                }

                operations.Add(
                    new Operation(
                        pathKey,
                        method,
                        pathKind,
                        [.. pathLevelParameterReferences, .. ParameterReferencesOf(operation)],
                        [.. pathLevelParameterNames, .. InlineParameterNamesOf(operation)],
                        ResponseReferencesOf(operation),
                        ResponseStatusCodesOf(operation)
                    )
                );
            }
        }

        return operations;
    }

    /// <summary>
    /// Classifies a path by its suffix, the same way profile filtering associates a derived path with
    /// its base collection.
    /// </summary>
    public static OperationPathKind ClassifyPath(string pathKey)
    {
        if (string.Equals(pathKey, AvailableChangeVersionsPath, StringComparison.OrdinalIgnoreCase))
        {
            return OperationPathKind.AvailableChangeVersions;
        }

        if (pathKey.EndsWith("/deletes", StringComparison.OrdinalIgnoreCase))
        {
            return OperationPathKind.Deletes;
        }

        if (pathKey.EndsWith("/keyChanges", StringComparison.OrdinalIgnoreCase))
        {
            return OperationPathKind.KeyChanges;
        }

        if (pathKey.EndsWith("/partitions", StringComparison.OrdinalIgnoreCase))
        {
            return OperationPathKind.Partitions;
        }

        if (pathKey.EndsWith('}'))
        {
            return OperationPathKind.Item;
        }

        return OperationPathKind.Collection;
    }

    private static List<string> ParameterReferencesOf(JsonObject operationOrPathItem) =>
        operationOrPathItem["parameters"] is not JsonArray parameters
            ? []
            :
            [
                .. parameters
                    .OfType<JsonObject>()
                    .Select(parameter => StringValueOf(parameter, "$ref"))
                    .Where(reference => reference is not null)
                    .Select(reference => reference!),
            ];

    private static List<string> InlineParameterNamesOf(JsonObject operationOrPathItem) =>
        operationOrPathItem["parameters"] is not JsonArray parameters
            ? []
            :
            [
                .. parameters
                    .OfType<JsonObject>()
                    .Select(parameter => StringValueOf(parameter, "name"))
                    .Where(name => name is not null)
                    .Select(name => name!),
            ];

    private static Dictionary<string, string> ResponseReferencesOf(JsonObject operation)
    {
        Dictionary<string, string> references = new(StringComparer.Ordinal);

        if (operation["responses"] is not JsonObject responses)
        {
            return references;
        }

        foreach ((string statusCode, JsonNode? response) in responses)
        {
            if (
                response is JsonObject responseObject
                && StringValueOf(responseObject, "$ref") is string reference
            )
            {
                references[statusCode] = reference;
            }
        }

        return references;
    }

    private static List<string> ResponseStatusCodesOf(JsonObject operation) =>
        operation["responses"] is not JsonObject responses ? [] : [.. responses.Select(pair => pair.Key)];

    private static string? StringValueOf(JsonObject jsonObject, string propertyName) =>
        jsonObject[propertyName] is JsonValue value && value.TryGetValue(out string? text) ? text : null;

    private static bool? BooleanValueOf(JsonObject jsonObject, string propertyName) =>
        jsonObject[propertyName] is JsonValue value && value.TryGetValue(out bool flag) ? flag : null;

    /// <summary>
    /// Renders one step of a location path: dotted when the key reads as an identifier, bracketed and
    /// quoted otherwise, so a path key containing slashes stays unambiguous.
    /// </summary>
    private static string FormatPathSegment(string key)
    {
        bool isSimple =
            key.Length > 0
            && key.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-');

        return isSimple ? $".{key}" : $"['{key}']";
    }

    /// <summary>
    /// Asserts the document defines every component it references.
    /// </summary>
    public static void AssertSelfResolves(JsonNode document, string documentName)
    {
        IReadOnlyList<UnresolvedReference> unresolved = FindUnresolvedReferences(document);

        unresolved
            .Should()
            .BeEmpty(
                "the independently served {0} document must define every component it references; "
                    + "unresolved: {1}",
                documentName,
                Describe(unresolved)
            );
    }

    /// <summary>
    /// Asserts the reusable parameter is declared with the published type and default.
    /// </summary>
    public static void AssertUseSnapshotParameterShape(JsonNode document, string documentName)
    {
        JsonNode? parameter = Component(document, "parameters", UseSnapshotParameterName);

        parameter
            .Should()
            .NotBeNull(
                "the {0} document must declare the reusable {1} parameter",
                documentName,
                UseSnapshotParameterName
            );

        JsonObject parameterObject = parameter!.AsObject();

        StringValueOf(parameterObject, "name")
            .Should()
            .Be(
                UseSnapshotParameterName,
                "the {0} document's snapshot parameter is a named header",
                documentName
            );
        StringValueOf(parameterObject, "in")
            .Should()
            .Be(
                "header",
                "the {0} document must advertise {1} as a header",
                documentName,
                UseSnapshotParameterName
            );

        // Read out before asserting rather than reached through ?., which skips the assertion
        // altogether on a parameter declaring no schema, no type, or no default - the shapes this is
        // here to reject.
        JsonObject? schema = parameterObject["schema"] as JsonObject;

        schema
            .Should()
            .NotBeNull(
                "the {0} document must declare a schema for {1}",
                documentName,
                UseSnapshotParameterName
            );

        StringValueOf(schema!, "type")
            .Should()
            .Be(
                "boolean",
                "the {0} document must advertise {1} as a boolean",
                documentName,
                UseSnapshotParameterName
            );
        BooleanValueOf(schema!, "default")
            .Should()
            .BeFalse(
                "the {0} document must default {1} to false so an unaware client is unaffected",
                documentName,
                UseSnapshotParameterName
            );
    }

    private static string Describe(IReadOnlyList<UnresolvedReference> unresolved)
    {
        if (unresolved.Count == 0)
        {
            return "none";
        }

        // Capped: a document that lost a shared component produces one failure per referencing
        // operation, and a few hundred identical lines would bury the one fact that matters.
        const int MaxReported = 10;

        StringBuilder description = new();
        description.AppendJoin("; ", unresolved.Take(MaxReported).Select(reference => reference.ToString()));

        if (unresolved.Count > MaxReported)
        {
            description.Append($"; and {unresolved.Count - MaxReported} more");
        }

        return description.ToString();
    }

    /// <summary>
    /// Parses an OpenAPI document from JSON text. Fixtures use it so a malformed fixture fails as a
    /// parse error naming itself rather than as a null-reference somewhere later.
    /// </summary>
    public static JsonNode Parse(string json, string documentName) =>
        JsonNode.Parse(json) ?? throw new JsonException($"The {documentName} document parsed to null.");
}
