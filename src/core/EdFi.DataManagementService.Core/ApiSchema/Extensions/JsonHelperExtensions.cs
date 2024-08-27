// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Microsoft.Extensions.Logging;
using System.Text.Json.Nodes;
using Json.Path;
using System.Text.Json;

namespace EdFi.DataManagementService.Core.ApiSchema.Extensions;

internal static class JsonHelperExtensions
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
                    return null;
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
        catch (PathParseException)
        {
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

            return result.Matches.Select(x =>
                x.Value
            );
        }
        catch (PathParseException)
        {
            throw new InvalidOperationException($"Unexpected Json.Path error for '{jsonPathString}'");
        }
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
    /// Helper to go from a scalar JSONPath selection directly to the typed value,
    /// or null if the node does not exist
    /// </summary>
    public static T? SelectNodeFromPathAs<T>(this JsonNode jsonNode, string jsonPathString, ILogger logger)
    {
        JsonNode? selectedNode = SelectNodeFromPath(jsonNode, jsonPathString, logger);

        if (selectedNode == null)
            return default;

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
                if (decimal.TryParse(stringValue, out decimal decimalValue))
                {
                    jsonNode.ReplaceWith(decimalValue);
                }
            }
            else
            {
                if (long.TryParse(stringValue, out long longValue))
                {
                    jsonNode.ReplaceWith(longValue);
                }
            }
        }
    }

    /// <summary>
    /// Helper to extract a list of JsonNodes as the values of all the properties of a JsonNode
    /// </summary>
    /// <param name="jsonNode"></param>
    public static List<JsonNode> SelectNodesFromPropertyValues(this JsonNode jsonNode)
    {
        KeyValuePair<string, JsonNode?>[]? nodeKeys = jsonNode?.AsObject().ToArray();

        if (nodeKeys == null)
        {
            throw new InvalidOperationException("Unexpected null");
        }

        return nodeKeys
            .Where(x => x.Value != null)
            .Select(x => x.Value ?? new JsonObject())
            .ToList();
    }
}
