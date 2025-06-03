// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Path;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Core.ApiSchema.Helpers;

internal static class JsonHelpers
{
    /// <summary>
    /// Helper to go from a scalar JSONPath selection directly to the selected JsonNode,
    /// or null if the node does not exist
    /// </summary>
    public static JsonNode? SelectNodeFromPath(this JsonNode jsonNode, string jsonPathString, ILogger logger)
    {
        try
        {
            JsonPath? jsonPath = JsonPath.Parse(jsonPathString);
            if (jsonPath == null)
            {
                logger.LogError("Malformed JSONPath string '{JsonPathString}'", jsonPathString);
                throw new InvalidOperationException($"Malformed JSONPath string '{jsonPathString}'");
            }

            PathResult? result = jsonPath.Evaluate(jsonNode);

            if (result.Matches == null)
            {
                logger.LogError("Malformed JSONPath string '{JsonPathString}'", jsonPathString);
                throw new InvalidOperationException($"Unexpected Json.Path error for '{jsonPathString}'");
            }

            try
            {
                if (result.Matches.Count == 0)
                {
                    return null;
                }
            }
            catch (System.ArgumentException ae)
            {
                throw new InvalidOperationException(
                    $"JSON value to be parsed is problematic, for example might contain duplicate keys.",
                    ae
                );
            }

            if (result.Matches.Count != 1)
            {
                logger.LogError(
                    "JSONPath string '{JsonPathString}' selected multiple values",
                    jsonPathString
                );
                throw new InvalidOperationException(
                    $"JSONPath string '{jsonPathString}' selected multiple values"
                );
            }

            return result.Matches[0].Value;
        }
        catch (PathParseException ex)
        {
            logger.LogError(ex, "PathParseException for JSONPath string '{JsonPathString}'", jsonPathString);
            throw new InvalidOperationException($"Unexpected Json.Path error for '{jsonPathString}'");
        }
    }

    /// <summary>
    /// Helper to go from an array JSONPath selection directly to the selected JsonNodes.
    /// Returns an empty array if none are selected.
    /// </summary>
    public static IEnumerable<JsonNode?> SelectNodesFromArrayPath(
        this JsonNode jsonNode,
        string jsonPathString,
        ILogger logger
    )
    {
        try
        {
            var result = SelectPathResult(jsonNode, jsonPathString, logger);
            return result.Matches.Select(x => x.Value);
        }
        catch (PathParseException)
        {
            throw new InvalidOperationException($"Unexpected Json.Path error for '{jsonPathString}'");
        }
    }

    private static PathResult SelectPathResult(JsonNode jsonNode, string jsonPathString, ILogger logger)
    {
        JsonPath? jsonPath = JsonPath.Parse(jsonPathString);
        if (jsonPath == null)
        {
            logger.LogError("Malformed JSONPath string '{JsonPathString}'", jsonPathString);
            throw new InvalidOperationException($"Malformed JSONPath string '{jsonPathString}'");
        }

        PathResult? result = jsonPath.Evaluate(jsonNode);

        if (result.Matches == null)
        {
            logger.LogError("Malformed JSONPath string '{JsonPathString}'", jsonPathString);
            throw new InvalidOperationException($"Unexpected Json.Path error for '{jsonPathString}'");
        }

        return result;
    }

    /// <summary>
    /// Helper to go from an array JSONPath selection directly to a string array regardless of the JSON type
    /// Returns empty array if the values do not exist.
    /// </summary>
    public static IEnumerable<string> SelectNodesFromArrayPathCoerceToStrings(
        this JsonNode jsonNode,
        string jsonPathString,
        ILogger logger
    )
    {
        IEnumerable<JsonNode?> jsonNodes = SelectNodesFromArrayPath(jsonNode, jsonPathString, logger);
        return jsonNodes.Select(jsonNode =>
        {
            JsonValue result =
                jsonNode?.AsValue() ?? throw new InvalidOperationException("Unexpected JSONPath value error");
            return result.ToString();
        });
    }

    /// <summary>
    /// Helper to go from an array JSONPath selection directly to a collection of string value and path regardless of the JSON type
    /// Returns empty dictionary if the values do not exist.
    /// </summary>
    public static IEnumerable<JsonPathAndValue> SelectNodesAndLocationFromArrayPathCoerceToStrings(
        this JsonNode jsonNode,
        string jsonPathString,
        ILogger logger
    )
    {
        var nodeValueWithPath = new List<JsonPathAndValue>();
        var result = SelectPathResult(jsonNode, jsonPathString, logger);
        IEnumerable<Node?> jsonNodes = result.Matches.Select(x => x);
        foreach (Node? node in jsonNodes)
        {
            if (node != null && node.Location != null)
            {
                var path = ConvertPath(node.Location.Segments);
                var value =
                    node.Value?.AsValue()
                    ?? throw new InvalidOperationException("Unexpected JSONPath value error");
                nodeValueWithPath.Add(new JsonPathAndValue(path, value.ToString()));
            }
        }

        // Converts $['eduCategories'][0]['eduCategoryDescriptor'] to $.eduCategories[0].eduCategoryDescriptor
        static string ConvertPath(PathSegment[] pathSegments)
        {
            StringBuilder path = new("$");
            foreach (PathSegment pathSegment in pathSegments)
            {
                var name = string.Join(
                    ".",
                    pathSegment.Selectors.Select(x => x != null ? ToJsonPathString(x) : string.Empty)
                );
                path.Append(name);
            }
            var parsedPath = JsonPath.Parse(path.ToString());
            return parsedPath.ToString();

            static string? ToJsonPathString(ISelector input)
            {
                var trimmedValue = $".{input.ToString()?.Trim('\'')}";
                if (int.TryParse(trimmedValue.TrimStart('.'), out var parsedValue))
                {
                    trimmedValue = $"[{parsedValue}]";
                }
                return trimmedValue;
            }
        }

        return nodeValueWithPath;
    }

    /// <summary>
    /// Helper to go from a scalar JSONPath selection directly to the typed value,
    /// or null if the node does not exist
    /// </summary>
    public static T? SelectNodeFromPathAs<T>(this JsonNode jsonNode, string jsonPathString, ILogger logger)
    {
        JsonNode? selectedNode = SelectNodeFromPath(jsonNode, jsonPathString, logger);

        if (selectedNode == null)
        {
            return default;
        }

        JsonValue? resultNode =
            selectedNode.AsValue() ?? throw new InvalidOperationException("Unexpected JSONPath value error");
        return resultNode.GetValue<T>();
    }

    /// <summary>
    /// Helper to go from a scalar JSONPath selection directly to a string value regardless of the JSON type
    /// Throws if the value does not exist
    /// </summary>
    public static string SelectRequiredNodeFromPathCoerceToString(
        this JsonNode jsonNode,
        string jsonPathString,
        ILogger logger
    )
    {
        JsonNode selectedNode =
            SelectNodeFromPath(jsonNode, jsonPathString, logger)
            ?? throw new InvalidOperationException("Unexpected JSONPath value error");

        JsonValue resultNode =
            selectedNode.AsValue() ?? throw new InvalidOperationException("Unexpected JSONPath value error");
        return resultNode.ToString();
    }

    /// <summary>
    /// Helper to go from a scalar JSONPath selection directly to the typed value.
    /// Throws if the value does not exist
    /// </summary>
    public static T SelectRequiredNodeFromPathAs<T>(
        this JsonNode jsonNode,
        string jsonPathString,
        ILogger logger
    )
    {
        T? result =
            SelectNodeFromPathAs<T>(jsonNode, jsonPathString, logger)
            ?? throw new InvalidOperationException($"Node at path '{jsonPathString}' not found");
        return result;
    }

    /// <summary>
    /// Helper to go from a scalar JSONPath selection directly to the selected JsonNode.
    /// Throws if the value does not exist
    /// </summary>
    public static JsonNode SelectRequiredNodeFromPath(
        this JsonNode jsonNode,
        string jsonPathString,
        ILogger logger
    )
    {
        return SelectNodeFromPath(jsonNode, jsonPathString, logger)
            ?? throw new InvalidOperationException($"Node at path '{jsonPathString}' not found");
    }

    /// <summary>
    /// Helper that returns the JsonNode present at the given key (similar to someNode['someKey']).
    /// Throws if the value does not exist or is null.
    /// </summary>
    public static JsonNode GetRequiredNode(this JsonNode jsonNode, string key)
    {
        return (
            jsonNode[key]
            ?? throw new InvalidOperationException($"Node with key '{key}' cannot be null or undefined")
        );
    }

    /// <summary>
    /// Helper to get value from json node. Throws if the node does not exist.
    /// </summary>
    public static T SelectNodeValue<T>(this JsonNode jsonNode, string jsonPathString)
    {
        var resourceName = jsonNode["resourceName"];
        var errorMessage =
            $"Expected {jsonPathString} to be in ResourceSchema for {resourceName}, invalid ApiSchema";

        JsonValue? resultNode =
            jsonNode[jsonPathString]?.AsValue() ?? throw new InvalidOperationException(errorMessage);
        return resultNode.GetValue<T>();
    }

    /// <summary>
    /// Helper to replace a boolean data type that was submitted as a string with its actual
    /// boolean value. Does not handle parsing failures as these will be dealt with in validation.
    /// </summary>
    public static void TryCoerceStringToBoolean(this JsonNode jsonNode)
    {
        var jsonValue = jsonNode.AsValue();
        if (jsonValue.GetValueKind() == JsonValueKind.String)
        {
            // Boolean value was submitted as string, must fix.
            string stringValue = jsonValue.GetValue<string>();
            if (Boolean.TryParse(stringValue, out bool booleanValue))
            {
                jsonNode.ReplaceWith(booleanValue);
            }
        }
    }

    /// <summary>
    /// Helper to replace a numeric data type that was submitted as a string with its actual
    /// numeric value. Does not handle parsing failures as these will be dealt with in validation.
    /// </summary>
    public static void TryCoerceStringToNumber(this JsonNode jsonNode)
    {
        var jsonValue = jsonNode.AsValue();
        if (jsonValue.GetValueKind() == JsonValueKind.String)
        {
            // Numeric value was passed in as string, must fix.
            string stringValue = jsonValue.GetValue<string>();
            if (stringValue.Contains('.'))
            {
                if (double.TryParse(stringValue, out double doubleValue))
                {
                    jsonNode.ReplaceWith(doubleValue);
                }
            }
            else
            {
                if (long.TryParse(stringValue, out long longValue))
                {
                    jsonNode.ReplaceWith(JsonValue.Create(longValue));
                }
            }
        }
    }

    /// <summary>
    /// Helper to replace a decimal data type that was submitted as a string with its actual
    /// numeric value. Does not handle parsing failures as these will be dealt with in validation.
    /// </summary>
    public static void TryCoerceStringToDecimal(this JsonNode jsonNode)
    {
        var jsonValue = jsonNode.AsValue();
        if (jsonValue.GetValueKind() == JsonValueKind.String)
        {
            string stringValue = jsonValue.GetValue<string>();
            if (decimal.TryParse(stringValue, out decimal decimalValue))
            {
                jsonNode.ReplaceWith(JsonValue.Create(decimalValue));
            }
        }
    }

    public static void TryCoerceDateToDateTime(this JsonNode jsonNode)
    {
        var jsonValue = jsonNode.AsValue();
        if (jsonValue.GetValueKind() == JsonValueKind.String)
        {
            string stringValue = jsonValue.GetValue<string>();
            if (
                DateOnly.TryParse(
                    stringValue,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out DateOnly dateValue
                )
            )
            {
                jsonNode.ReplaceWith(
                    dateValue
                        .ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)
                        .ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)
                );
            }
        }
    }

    /// <summary>
    /// Helper to extract a list of JsonNodes as the values of all the properties of a JsonNode
    /// </summary>
    public static List<JsonNode> SelectNodesFromPropertyValues(this JsonNode jsonNode)
    {
        KeyValuePair<string, JsonNode?>[]? nodeKeys =
            (jsonNode?.AsObject().ToArray())
            ?? throw new InvalidOperationException("Unexpected null in SelectNodesFromPropertyValues");

        return nodeKeys.Where(x => x.Value != null).Select(x => x.Value ?? new JsonObject()).ToList();
    }

    /// <summary>
    /// Finds duplicates in an array by comparing values at specified JsonPaths.
    /// Returns the index and the array path of the first duplicate found, or null and -1 if no duplicates exist.
    /// </summary>
    public static (string? arrayPath, int dupeIndex) FindDuplicatesWithArrayPath(
        this JsonNode jsonNode,
        IList<string> jsonPaths,
        ILogger logger
    )
    {
        if (jsonPaths.Count == 0)
        {
            return (null, -1);
        }

        // Evaluate all paths and group by number of values
        var pathResults = jsonPaths
            .Select(path => new
            {
                Path = path,
                Values = jsonNode.SelectNodesFromArrayPathCoerceToStrings(path, logger).ToList(),
            })
            .ToList();

        // Group to discard all arrays that have no values in the document or that only have one value
        var groups = pathResults.GroupBy(x => x.Values.Count).Where(g => g.Key > 1);

        foreach (var group in groups)
        {
            var groupPaths = group.ToList();
            int itemCount = group.Key;

            // Defensive validation: all paths in the group must have the same number of values
            bool allSameCount = groupPaths.TrueForAll(gp => gp.Values.Count == itemCount);
            if (!allSameCount)
            {
                continue;
            }

            // Using HashSet to detect duplicates efficiently
            var seen = new Dictionary<string, int>();
            for (int i = 0; i < itemCount; i++)
            {
                // Build the uniqueness key for the current item
                var sb = new StringBuilder();
                for (int p = 0; p < groupPaths.Count; p++)
                {
                    if (p > 0)
                    {
                        sb.Append('|');
                    }
                    sb.Append(groupPaths[p].Values[i] ?? "null");
                }
                string key = sb.ToString();

                if (seen.TryGetValue(key, out _))
                {
                    string arrayPath = groupPaths[0].Path;
                    return (arrayPath, i);
                }
                else
                {
                    seen[key] = i;
                }
            }
        }

        return (null, -1);
    }
}

/// <summary>
/// Contains Json path and Json node value
/// </summary>
public record JsonPathAndValue(string jsonPath, string value);
