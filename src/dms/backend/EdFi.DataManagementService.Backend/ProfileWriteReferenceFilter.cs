// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Backend;

/// <summary>
/// Restricts the document/descriptor references that the relational write executor
/// resolves to those still present in the profile-shaped write body (DMS-1229).
/// </summary>
/// <remarks>
/// References and descriptors are extracted from the raw submitted body
/// (<c>DocumentInfo</c>), but a writable profile may hide submitted members that the
/// shaper strips from the write body. Those hidden references/descriptors must be
/// accepted and ignored rather than resolved and written (or rejected as unresolved).
/// Identity references preserved by the shaper remain present in the shaped body and are
/// therefore retained.
/// </remarks>
internal static class ProfileWriteReferenceFilter
{
    public static IReadOnlyList<DocumentReference> RetainPresent(
        IReadOnlyList<DocumentReference> documentReferences,
        JsonNode shapedBody
    ) => [.. documentReferences.Where(reference => PathExists(shapedBody, reference.Path.Value))];

    public static IReadOnlyList<DescriptorReference> RetainPresent(
        IReadOnlyList<DescriptorReference> descriptorReferences,
        JsonNode shapedBody
    ) => [.. descriptorReferences.Where(descriptor => PathExists(shapedBody, descriptor.Path.Value))];

    /// <summary>
    /// Determines whether a concrete JSON path (property and numeric-index segments only,
    /// e.g. <c>$.schoolReference.schoolId</c> or <c>$.gradeLevels[0].gradeLevelDescriptor</c>)
    /// resolves to an existing node in <paramref name="body"/>.
    /// </summary>
    internal static bool PathExists(JsonNode body, string concretePath)
    {
        if (string.IsNullOrEmpty(concretePath) || concretePath[0] != '$')
        {
            return false;
        }

        JsonNode? cursor = body;
        int index = 1;

        while (index < concretePath.Length)
        {
            char current = concretePath[index];

            if (current == '.')
            {
                int start = index + 1;
                int end = start;
                while (end < concretePath.Length && concretePath[end] != '.' && concretePath[end] != '[')
                {
                    end++;
                }

                string memberName = concretePath[start..end];
                if (cursor is not JsonObject obj || !obj.TryGetPropertyValue(memberName, out cursor))
                {
                    return false;
                }

                index = end;
            }
            else if (current == '[')
            {
                int close = concretePath.IndexOf(']', index);
                if (close < 0)
                {
                    return false;
                }

                string indexText = concretePath[(index + 1)..close];
                if (
                    cursor is not JsonArray array
                    || !int.TryParse(indexText, out int arrayIndex)
                    || arrayIndex < 0
                    || arrayIndex >= array.Count
                )
                {
                    return false;
                }

                cursor = array[arrayIndex];
                index = close + 1;
            }
            else
            {
                return false;
            }
        }

        return true;
    }
}
