// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Utilities;

namespace EdFi.DataManagementService.Core.OpenApi;

/// <summary>
/// Publishes the identification-code query parameters on the collection GET of each resource that
/// offers them. Runs after all core, abstract, and extension fragments are merged and before cursor-paging
/// augmentation, which copies the collection's inline query parameters onto the partitions operation.
/// </summary>
/// <remarks>
/// The parameters come from <see cref="IdentificationCodeQueryFieldRule" />, the same rule that adds the
/// fields to <c>ResourceSchema.QueryFields</c>, so a resource's published names are the names its
/// GET-many and partitions requests accept. Each parameter's type, format and description come from the
/// collection item property and its maxLength is the bound the request pipeline enforces. Domain and
/// profile filtering run afterward and leave inline query parameters as they are.
/// </remarks>
internal static class IdentificationCodeQueryOpenApiAugmenter
{
    private const string PathsPath = "$.paths";
    private const string ParametersPath = "$.components.parameters";
    private const string ParameterComponentRefPrefix = "#/components/parameters/";
    private const string QueryParameterLocation = "query";
    private const string ItemSuffix = "[*]";
    private const string RootPrefix = "$.";

    /// <summary>
    /// Adds the parameters an endpoint-owning resource schema offers to each collection path its fragment
    /// declares. Called only for endpoint-owning resource schemas, so a resource extension, whose
    /// identification codes the rule never considers, contributes nothing.
    /// </summary>
    internal static void CollectParameters(
        JsonNode resourceSchemaNode,
        JsonObject fragmentPaths,
        Dictionary<string, IReadOnlyList<JsonObject>> parametersByCollectionPath
    )
    {
        IReadOnlyList<IdentificationCodeQueryField> queryFields = IdentificationCodeQueryFieldRule.Derive(
            resourceSchemaNode
        );

        if (queryFields.Count == 0)
        {
            return;
        }

        JsonObject[] parameters =
        [
            .. queryFields.Select(queryField => BuildParameter(resourceSchemaNode, queryField)),
        ];

        foreach (
            string pathKey in fragmentPaths
                .Select(fragmentPath => fragmentPath.Key)
                .Where(CursorPagingOpenApiAugmenter.IsCollectionPath)
        )
        {
            parametersByCollectionPath[pathKey] = parameters;
        }
    }

    /// <summary>
    /// Appends the collected parameters to the collection GET of every collected path in the document.
    /// </summary>
    internal static void Augment(
        JsonNode openApiSpecification,
        IReadOnlyDictionary<string, IReadOnlyList<JsonObject>> parametersByCollectionPath
    )
    {
        if (parametersByCollectionPath.Count == 0)
        {
            return;
        }

        if (openApiSpecification["paths"] is not JsonObject paths)
        {
            throw new InvalidOperationException($"Node at path '{PathsPath}' is not a JSON object");
        }

        JsonObject? componentParameters = openApiSpecification["components"]?["parameters"] as JsonObject;

        foreach ((string pathKey, IReadOnlyList<JsonObject> parameters) in parametersByCollectionPath)
        {
            if (paths[pathKey] is null)
            {
                continue;
            }

            JsonArray collectionParameters = RequireCollectionParameters(paths, pathKey);
            HashSet<string> publishedNames = PublishedNames(
                collectionParameters,
                componentParameters,
                pathKey
            );

            foreach (JsonObject parameter in parameters)
            {
                string name = parameter["name"]!.GetValue<string>();

                // The rule never offers a name the resource's queryFieldMapping already has, so a clash here
                // is a parameter the fragment publishes for some other meaning. Publishing both would
                // advertise two contracts for one name, and the request pipeline honors only one.
                if (!publishedNames.Add(name))
                {
                    throw new InvalidOperationException(
                        $"The GET operation at path '{Sanitize(pathKey)}' already declares a "
                            + $"'{Sanitize(name)}' parameter. Identification-code query assembly will not "
                            + "publish a second parameter of the same name."
                    );
                }

                collectionParameters.Add(parameter.DeepClone());
            }
        }
    }

    private static JsonObject BuildParameter(JsonNode resourceSchemaNode, IdentificationCodeQueryField field)
    {
        JsonNode? itemProperty = ItemPropertySchema(resourceSchemaNode, field);
        JsonObject schema = [];

        if (StringValue(itemProperty?["format"]) is { } format)
        {
            schema["format"] = format;
        }

        if (field.MaxLength is int maxLength)
        {
            schema["maxLength"] = maxLength;
        }

        schema["type"] =
            StringValue(itemProperty?["type"])
            ?? throw new InvalidOperationException(
                $"Identification-code query field '{Sanitize(field.QueryFieldName)}' has no item type."
            );

        return new JsonObject
        {
            ["description"] = StringValue(itemProperty?["description"]) ?? string.Empty,
            ["in"] = QueryParameterLocation,
            ["name"] = field.QueryFieldName,
            ["schema"] = schema,
        };
    }

    /// <summary>
    /// The insert-schema item property a field reads. The rule spells the collection path as
    /// <c>$.{collection}[*]</c> over a plain property name, so the collection name is that path's middle.
    /// </summary>
    private static JsonNode? ItemPropertySchema(
        JsonNode resourceSchemaNode,
        IdentificationCodeQueryField field
    )
    {
        string collectionPath = field.CollectionPath.Value;

        if (
            !collectionPath.StartsWith(RootPrefix, StringComparison.Ordinal)
            || !collectionPath.EndsWith(ItemSuffix, StringComparison.Ordinal)
        )
        {
            throw new InvalidOperationException(
                $"Identification-code collection path '{Sanitize(collectionPath)}' is not a top-level "
                    + "collection item path."
            );
        }

        string collectionName = collectionPath[RootPrefix.Length..^ItemSuffix.Length];

        return resourceSchemaNode["jsonSchemaForInsert"]
            ?["properties"]
            ?[collectionName]
            ?["items"]
            ?["properties"]
            ?[field.QueryFieldName];
    }

    private static JsonArray RequireCollectionParameters(JsonObject paths, string pathKey)
    {
        if (paths[pathKey]?["get"] is not JsonObject getOperation)
        {
            throw new InvalidOperationException(
                $"Node at path '{PathDiagnostic(pathKey)}.get' is not a JSON object"
            );
        }

        if (getOperation["parameters"] is null)
        {
            JsonArray created = [];
            getOperation["parameters"] = created;
            return created;
        }

        return getOperation["parameters"] as JsonArray
            ?? throw new InvalidOperationException(
                $"Node at path '{PathDiagnostic(pathKey)}.get.parameters' is not a JSON array"
            );
    }

    /// <summary>
    /// The query names the collection GET already publishes, inline or through a parameter component,
    /// compared case-insensitively as the request pipeline matches filter names.
    /// </summary>
    private static HashSet<string> PublishedNames(
        JsonArray collectionParameters,
        JsonObject? componentParameters,
        string pathKey
    )
    {
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);

        for (int index = 0; index < collectionParameters.Count; index += 1)
        {
            string diagnostic = $"{PathDiagnostic(pathKey)}.get.parameters[{index}]";
            JsonNode? parameter = collectionParameters[index];

            if (StringValue(parameter?["$ref"]) is { } reference)
            {
                if (!reference.StartsWith(ParameterComponentRefPrefix, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Parameter at '{diagnostic}' references '{Sanitize(reference)}', which is not a "
                            + "parameter component reference."
                    );
                }

                string componentName = reference[ParameterComponentRefPrefix.Length..];
                parameter = componentParameters?[componentName];
                diagnostic = $"{ParametersPath}.{Sanitize(componentName)}";
            }

            if (StringValue(parameter?["name"]) is not { Length: > 0 } name)
            {
                throw new InvalidOperationException(
                    $"Parameter at '{diagnostic}' has no name, so its published query name cannot be "
                        + "determined."
                );
            }

            if (
                string.Equals(StringValue(parameter?["in"]), QueryParameterLocation, StringComparison.Ordinal)
            )
            {
                names.Add(name);
            }
        }

        return names;
    }

    private static string? StringValue(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue(out string? text) ? text : null;

    private static string PathDiagnostic(string pathKey) => $"{PathsPath}['{Sanitize(pathKey)}']";

    private static string Sanitize(string value) => LoggingSanitizer.SanitizeInternalValueForLogging(value);
}
