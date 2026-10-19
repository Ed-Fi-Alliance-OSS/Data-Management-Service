// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text;
using System.Text.Json;

namespace EdFi.DataManagementService.Core.Utilities;

/// <summary>
/// Finds the first duplicate property in original JSON tokens, preserving the existing path format.
/// Callers must validate the complete JSON first: scanning returns immediately on a duplicate.
/// </summary>
public static class DuplicatePropertyPathScanner
{
    /// <summary>
    /// Returns true with the first duplicate's path, or false with an empty path.
    /// </summary>
    public static bool TryFindFirstDuplicatePropertyPath(ReadOnlySpan<byte> json, out string path)
    {
        var reader = new Utf8JsonReader(
            json,
            new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip }
        );
        var containers = new Stack<ContainerFrame>();
        var currentPath = new StringBuilder("$");

        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.StartObject:
                case JsonTokenType.StartArray:
                    int parentPathLength = currentPath.Length;
                    if (containers.Count > 0)
                    {
                        ContainerFrame parent = containers.Peek();
                        if (parent.IsArray)
                        {
                            currentPath.Append('[').Append(parent.ArrayIndex).Append(']');
                        }
                        else
                        {
                            currentPath.Append('.').Append(parent.PendingProperty);
                        }
                    }

                    containers.Push(
                        new ContainerFrame(reader.TokenType == JsonTokenType.StartArray, parentPathLength)
                    );
                    break;

                case JsonTokenType.PropertyName:
                    ContainerFrame currentObject = containers.Peek();
                    string propertyName = reader.GetString()!;
                    if (!currentObject.PropertyNames.Add(propertyName))
                    {
                        path = currentPath.Append('.').Append(propertyName).ToString();
                        return true;
                    }

                    currentObject.PendingProperty = propertyName;
                    break;

                case JsonTokenType.EndObject:
                case JsonTokenType.EndArray:
                    currentPath.Length = containers.Pop().ParentPathLength;
                    CompleteValue(containers);
                    break;

                case JsonTokenType.String:
                case JsonTokenType.Number:
                case JsonTokenType.True:
                case JsonTokenType.False:
                case JsonTokenType.Null:
                    CompleteValue(containers);
                    break;
            }
        }

        path = string.Empty;
        return false;
    }

    private static void CompleteValue(Stack<ContainerFrame> containers)
    {
        // Only the actual parent completes a value. A property inside an array element
        // completes its object property, not the owning array element.
        if (containers.Count == 0)
        {
            return;
        }

        ContainerFrame parent = containers.Peek();
        if (parent.IsArray)
        {
            parent.ArrayIndex++;
        }
        else
        {
            parent.PendingProperty = string.Empty;
        }
    }

    private sealed class ContainerFrame(bool isArray, int parentPathLength)
    {
        public bool IsArray { get; } = isArray;
        public int ParentPathLength { get; } = parentPathLength;
        public HashSet<string> PropertyNames { get; } = new(StringComparer.Ordinal);
        public string PendingProperty { get; set; } = string.Empty;
        public int ArrayIndex { get; set; }
    }
}
