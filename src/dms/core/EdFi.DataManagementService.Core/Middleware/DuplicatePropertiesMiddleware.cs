// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text;
using System.Text.Json;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using Microsoft.Extensions.Logging;
using static EdFi.DataManagementService.Core.Response.FailureResponse;

namespace EdFi.DataManagementService.Core.Middleware;

/// <summary>
/// Middleware that detects duplicate property names in JSON request bodies.
/// This uses Utf8JsonReader to scan the raw JSON and detect duplicates with their exact paths,
/// which is necessary because System.Text.Json's JsonNode silently overwrites duplicate properties.
/// </summary>
internal class DuplicatePropertiesMiddleware(ILogger logger) : IPipelineStep
{
    public async Task Execute(RequestInfo requestInfo, Func<Task> next)
    {
        logger.LogDebug(
            "Entering DuplicatePropertiesMiddleware - {TraceId}",
            requestInfo.FrontendRequest.TraceId.Value
        );

        if (!string.IsNullOrEmpty(requestInfo.FrontendRequest.Body))
        {
            string? duplicatePath = FindDuplicatePropertyPath(requestInfo.FrontendRequest.Body);

            if (duplicatePath != null)
            {
                var validationErrors = new Dictionary<string, string[]>
                {
                    { duplicatePath, ["An item with the same key has already been added."] },
                };

                logger.LogDebug(
                    "Duplicate key found at {DuplicatePath} - {TraceId}",
                    duplicatePath,
                    requestInfo.FrontendRequest.TraceId.Value
                );

                requestInfo.FrontendResponse = new FrontendResponse(
                    StatusCode: 400,
                    Body: ForDataValidation(
                        "Data validation failed. See 'validationErrors' for details.",
                        traceId: requestInfo.FrontendRequest.TraceId,
                        validationErrors,
                        []
                    ),
                    Headers: []
                );
                return;
            }
        }

        await next();
    }

    /// <summary>
    /// Scans the raw JSON string using Utf8JsonReader to find duplicate property names.
    /// Returns the JSON path of the first duplicate found, or null if no duplicates exist.
    /// </summary>
    private static string? FindDuplicatePropertyPath(string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        var reader = new Utf8JsonReader(
            bytes,
            new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip }
        );

        // Stack to track the path to current location in the JSON
        var pathStack = new Stack<PathSegment>();
        // Stack to track property names seen at each object nesting level (for duplicate detection)
        var propertyNamesStack = new Stack<HashSet<string>>();

        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.StartObject:
                    propertyNamesStack.Push([]);
                    break;

                case JsonTokenType.EndObject:
                    if (propertyNamesStack.Count > 0)
                    {
                        propertyNamesStack.Pop();
                    }
                    // Pop the property segment that led to this object (if any)
                    if (pathStack.Count > 0 && !pathStack.Peek().IsArray)
                    {
                        pathStack.Pop();
                    }
                    break;

                case JsonTokenType.StartArray:
                    // Push an array segment to track indices
                    pathStack.Push(new PathSegment(IsArray: true, PropertyName: null, ArrayIndex: 0));
                    break;

                case JsonTokenType.EndArray:
                    // Pop the array segment
                    if (pathStack.Count > 0 && pathStack.Peek().IsArray)
                    {
                        pathStack.Pop();
                    }
                    // Pop the property that led to this array (if any)
                    if (pathStack.Count > 0 && !pathStack.Peek().IsArray)
                    {
                        pathStack.Pop();
                    }
                    break;

                case JsonTokenType.PropertyName:
                    string propertyName = reader.GetString()!;

                    // Check for duplicate in current object BEFORE adding to path
                    if (propertyNamesStack.Count > 0)
                    {
                        var currentProperties = propertyNamesStack.Peek();
                        if (!currentProperties.Add(propertyName))
                        {
                            // Duplicate found! Build the path (don't include the duplicate itself in the stack)
                            return BuildJsonPath(pathStack, propertyName);
                        }
                    }

                    // Push this property onto the path stack (will be popped when its value ends)
                    pathStack.Push(
                        new PathSegment(IsArray: false, PropertyName: propertyName, ArrayIndex: 0)
                    );
                    break;

                case JsonTokenType.String:
                case JsonTokenType.Number:
                case JsonTokenType.True:
                case JsonTokenType.False:
                case JsonTokenType.Null:
                    // Primitive value encountered - pop the property that led here
                    if (pathStack.Count > 0 && !pathStack.Peek().IsArray)
                    {
                        pathStack.Pop();
                    }
                    // If we're in an array, increment the index for the next element
                    if (pathStack.Count > 0 && pathStack.Peek().IsArray)
                    {
                        var arraySegment = pathStack.Pop();
                        pathStack.Push(arraySegment with { ArrayIndex = arraySegment.ArrayIndex + 1 });
                    }
                    break;
            }

            // After completing an object or array that was an array element, increment the array index
            if (
                (reader.TokenType == JsonTokenType.EndObject || reader.TokenType == JsonTokenType.EndArray)
                && pathStack.Count > 0
                && pathStack.Peek().IsArray
            )
            {
                var arraySegment = pathStack.Pop();
                pathStack.Push(arraySegment with { ArrayIndex = arraySegment.ArrayIndex + 1 });
            }
        }

        return null;
    }

    /// <summary>
    /// Builds a JSON path string from the path stack and the duplicate property name.
    /// </summary>
    private static string BuildJsonPath(Stack<PathSegment> pathStack, string duplicatePropertyName)
    {
        var segments = pathStack.ToArray();
        Array.Reverse(segments);

        var pathBuilder = new StringBuilder("$");

        foreach (var segment in segments)
        {
            if (segment.IsArray)
            {
                pathBuilder.Append($"[{segment.ArrayIndex}]");
            }
            else if (segment.PropertyName != null)
            {
                pathBuilder.Append($".{segment.PropertyName}");
            }
        }

        pathBuilder.Append($".{duplicatePropertyName}");
        return pathBuilder.ToString();
    }

    /// <summary>
    /// Represents a segment in the JSON path being tracked.
    /// </summary>
    private sealed record PathSegment(bool IsArray, string? PropertyName, int ArrayIndex);
}
