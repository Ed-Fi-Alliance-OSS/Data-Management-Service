// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.JsonDiffPatch;
using System.Text.Json.JsonDiffPatch.Diffs.Formatters;
using System.Text.Json.Nodes;

namespace EdFi.DataManagementService.Backend.Postgresql;

internal static class JsonPatchUtility
{
    /// <summary>
    /// Computes a JSON Patch (RFC 6902) that transforms existingDocument into newDocument.
    /// Returns null if there are no changes; otherwise returns a JsonNode representing
    /// an array of operations.
    /// </summary>
    public static JsonNode? ComputePatch(JsonElement existingDocument, JsonNode newDocument)
    {
        var existingNode =
            JsonNode.Parse(existingDocument.GetRawText())
            ?? throw new InvalidOperationException("Existing document JSON could not be parsed.");

        var options = new JsonDiffOptions();
        var formatter = new JsonPatchDeltaFormatter();

        JsonNode patch =
            JsonDiffPatcher.Diff(existingNode, newDocument, formatter, options) ?? new JsonArray();

        if (patch is JsonArray arr && arr.Count == 0)
        {
            return null;
        }

        return patch;
    }

    private static readonly HashSet<string> SupportedOps = new(StringComparer.OrdinalIgnoreCase)
    {
        "add",
        "remove",
        "replace",
    };

    /// <summary>
    /// Returns true if all operations in the patch use supported RFC 6902 op codes
    /// ("add", "remove", "replace"). Returns false if the structure is invalid or
    /// if any op is unsupported (e.g., "copy", "move", "test").
    /// </summary>
    public static bool HasOnlySupportedOps(JsonNode patchNode)
    {
        if (patchNode is not JsonArray ops)
        {
            return false;
        }

        foreach (JsonNode? opNode in ops)
        {
            var op = opNode?["op"]?.GetValue<string>();

            if (string.IsNullOrEmpty(op) || !SupportedOps.Contains(op))
            {
                return false;
            }
        }

        return true;
    }
}
