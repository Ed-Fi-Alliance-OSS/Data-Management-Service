// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using Json.Path;

namespace EdFi.DataManagementService.Api.ApiSchema.Extensions;

public static class JsonHelperExtensions
{
    /// <summary>
    /// Helper to go from a scalar JSONPath selection directly to the selected JsonNode,
    /// or null if the node does not exist
    /// </summary>
    public static JsonNode? SelectNodeFromPath(this JsonNode jsonNode, string jsonPathString)
    {
        try
        {
            JsonPath jsonPath = JsonPath.Parse(jsonPathString) ??
            throw new InvalidOperationException($"Malformed JSONPath string '{jsonPathString}'");

            PathResult? result = jsonPath.Evaluate(jsonNode);

            if (result.Matches == null) throw new InvalidOperationException($"Unexpected Json.Path error for '{jsonPathString}'");

            if (result.Matches.Count == 0) return null;

            if (result.Matches.Count != 1)
            {
                throw new InvalidOperationException($"JSONPath string '{jsonPathString}' selected multiple values");
            }

            return result.Matches[0].Value;
        }
        catch (PathParseException)
        {
            throw new InvalidOperationException($"Unexpected Json.Path error for '{jsonPathString}'");

        }
    }

    /// <summary>
    /// Helper to go from a scalar JSONPath selection directly to the selected JsonNode.
    /// Throws if the value does not exist
    /// </summary>
    public static JsonNode SelectRequiredNodeFromPath(this JsonNode jsonNode, string jsonPathString)
    {
        JsonNode result =
            SelectNodeFromPath(jsonNode, jsonPathString) ??
            throw new InvalidOperationException($"Node at path '{jsonPathString}' not found");
        return result;
    }

    /// <summary>
    /// Helper to go from a scalar JSONPath selection directly to the typed value,
    /// or null if the node does not exist
    /// </summary>
    public static T? SelectNodeFromPathAs<T>(this JsonNode jsonNode, string jsonPathString)
    {
        JsonNode? selectedNode = SelectNodeFromPath(jsonNode, jsonPathString);

        if (selectedNode == null) return default;

        JsonValue resultNode =
            selectedNode!.AsValue() ??
            throw new InvalidOperationException("Unexpected JSONPath value error");
        return resultNode.GetValue<T>();
    }

    /// <summary>
    /// Helper to go from a scalar JSONPath selection directly to the typed value.
    /// Throws if the value does not exist
    /// </summary>
    public static T SelectRequiredNodeFromPathAs<T>(this JsonNode jsonNode, string jsonPathString)
    {
        T result =
            SelectNodeFromPathAs<T>(jsonNode, jsonPathString) ??
            throw new InvalidOperationException($"Node at path '{jsonPathString}' not found");
        return result;
    }
}
