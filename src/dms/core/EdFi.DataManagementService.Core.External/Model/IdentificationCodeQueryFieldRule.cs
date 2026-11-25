// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;

namespace EdFi.DataManagementService.Core.External.Model;

/// <summary>
/// Derives the query fields a resource offers over its identification-code collection, the one kind of
/// collection a GET-many filter may reach into.
/// </summary>
/// <remarks>
/// <para>
/// The resource's collection is the first top-level array property, in insert-schema property order,
/// whose items declare an <c>identificationCode</c> property. Every scalar property of those items is a
/// query field, named as the property. This is the ODS/API rule, so the same resources and parameter
/// names qualify. Root-level, nested and extension properties are never considered.
/// </para>
/// <para>
/// A name that already names one of the resource's <c>queryFieldMapping</c> fields, compared
/// case-insensitively as filter names are matched, is not offered: the existing field keeps its meaning.
/// </para>
/// <para>
/// The rule reads the resource schema and never changes it. It expects the resource-schema shape
/// ApiSchema defines, where every schema node is an object; within that shape, a resource without a
/// qualifying collection, and a property that is not a scalar, simply qualify nothing. Only plain ASCII
/// letter-and-digit property names are used, because they are spliced into JSONPath expressions.
/// </para>
/// </remarks>
public static class IdentificationCodeQueryFieldRule
{
    private const string IdentificationCodePropertyName = "identificationCode";

    /// <summary>
    /// The identification-code query fields of one resource, in item property order, or none when the
    /// resource has no qualifying collection.
    /// </summary>
    /// <param name="resourceSchema">
    /// One entry of <c>resourceSchemas</c>, carrying <c>jsonSchemaForInsert</c> and
    /// <c>queryFieldMapping</c>.
    /// </param>
    public static IReadOnlyList<IdentificationCodeQueryField> Derive(JsonNode resourceSchema)
    {
        ArgumentNullException.ThrowIfNull(resourceSchema);

        if (
            resourceSchema["jsonSchemaForInsert"]?["properties"] is not JsonObject rootProperties
            || FindCollection(rootProperties) is not { } collection
        )
        {
            return [];
        }

        (string collectionName, JsonObject itemProperties) = collection;

        HashSet<string> takenNames = new(StringComparer.OrdinalIgnoreCase);

        if (resourceSchema["queryFieldMapping"] is JsonObject queryFieldMapping)
        {
            takenNames.UnionWith(queryFieldMapping.Select(static queryField => queryField.Key));
        }

        string collectionPath = $"$.{collectionName}[*]";
        List<IdentificationCodeQueryField> queryFields = [];

        foreach ((string propertyName, JsonNode? propertySchema) in itemProperties)
        {
            if (
                !IsPlainName(propertyName)
                || QueryTypeOf(propertySchema) is not { } queryType
                || !takenNames.Add(propertyName)
            )
            {
                continue;
            }

            queryFields.Add(
                new IdentificationCodeQueryField(
                    propertyName,
                    new JsonPath(collectionPath),
                    new JsonPath($"{collectionPath}.{propertyName}"),
                    queryType,
                    queryType == "string" ? DeclaredMaxLength(propertySchema) : null
                )
            );
        }

        return queryFields;
    }

    private static (string Name, JsonObject ItemProperties)? FindCollection(JsonObject rootProperties)
    {
        foreach ((string propertyName, JsonNode? propertySchema) in rootProperties)
        {
            if (
                IsPlainName(propertyName)
                && StringValue(propertySchema?["type"]) == "array"
                && propertySchema?["items"]?["properties"] is JsonObject itemProperties
                && itemProperties.ContainsKey(IdentificationCodePropertyName)
            )
            {
                return (propertyName, itemProperties);
            }
        }

        return null;
    }

    /// <summary>
    /// The <c>queryFieldMapping</c> type of a scalar property, mapped as MetaEd maps it, or null for a
    /// property that is not a scalar.
    /// </summary>
    private static string? QueryTypeOf(JsonNode? propertySchema) =>
        StringValue(propertySchema?["type"]) switch
        {
            "string" => StringValue(propertySchema?["format"]) switch
            {
                "date" => "date",
                "date-time" => "date-time",
                "time" => "time",
                _ => "string",
            },
            "integer" or "number" => "number",
            "boolean" => "boolean",
            _ => null,
        };

    private static int? DeclaredMaxLength(JsonNode? propertySchema) =>
        propertySchema?["maxLength"] is JsonValue maxLength && maxLength.TryGetValue(out int value)
            ? value
            : null;

    private static string? StringValue(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue(out string? text) ? text : null;

    private static bool IsPlainName(string name) => name.Length > 0 && name.All(char.IsAsciiLetterOrDigit);
}
