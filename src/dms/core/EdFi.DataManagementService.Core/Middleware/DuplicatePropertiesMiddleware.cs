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

        string? duplicatePath = requestInfo.FrontendRequest.DuplicatePropertyPath;

        if (duplicatePath == null && !string.IsNullOrEmpty(requestInfo.FrontendRequest.Body))
        {
            duplicatePath = FindDuplicatePropertyPath(requestInfo.FrontendRequest.Body);
        }

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

        await next();
    }

    /// <summary>
    /// Scans the raw JSON string using Utf8JsonReader to find duplicate property names.
    /// Returns the JSON path of the first duplicate found, or null if no duplicates exist.
    /// Throws <see cref="InvalidOperationException"/> with the containing object's path when a
    /// property name is malformed.
    /// </summary>
    internal static string? FindDuplicatePropertyPath(string json)
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
                    CompleteValue(pathStack);
                    break;

                case JsonTokenType.StartArray:
                    // Push an array segment to track indices
                    pathStack.Push(new PathSegment(IsArray: true, PropertyName: null, ArrayIndex: 0));
                    break;

                case JsonTokenType.EndArray:
                    // Pop the array segment, then complete the array as a value
                    if (pathStack.Count > 0 && pathStack.Peek().IsArray)
                    {
                        pathStack.Pop();
                    }
                    CompleteValue(pathStack);
                    break;

                case JsonTokenType.PropertyName:
                    string propertyName;
                    try
                    {
                        propertyName = reader.GetString()!;
                    }
                    catch (InvalidOperationException ex)
                    {
                        // A malformed property name, such as an unpaired surrogate escape. Report the
                        // object that holds it so the caller can answer with a path-attributed 400.
                        throw new InvalidOperationException(
                            $"{ex.Message} Path: {BuildContainingPath(pathStack)}",
                            ex
                        );
                    }

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
                    CompleteValue(pathStack);
                    break;
            }
        }

        return null;
    }

    /// <summary>
    /// Records that a value just finished. A value that belongs to a property pops that property's
    /// segment; only a value that is itself an array element advances the array index.
    /// </summary>
    private static void CompleteValue(Stack<PathSegment> pathStack)
    {
        if (pathStack.Count == 0)
        {
            return;
        }

        var segment = pathStack.Pop();
        if (segment.IsArray)
        {
            pathStack.Push(segment with { ArrayIndex = segment.ArrayIndex + 1 });
        }
    }

    /// <summary>
    /// Builds a JSON path string from the path stack and the duplicate property name.
    /// </summary>
    private static string BuildJsonPath(Stack<PathSegment> pathStack, string duplicatePropertyName) =>
        $"{BuildContainingPath(pathStack)}.{duplicatePropertyName}";

    /// <summary>
    /// Builds the JSON path of the current location from the path stack.
    /// </summary>
    private static string BuildContainingPath(Stack<PathSegment> pathStack)
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

        return pathBuilder.ToString();
    }

    /// <summary>
    /// Represents a segment in the JSON path being tracked.
    /// </summary>
    private sealed record PathSegment(bool IsArray, string? PropertyName, int ArrayIndex);
}
