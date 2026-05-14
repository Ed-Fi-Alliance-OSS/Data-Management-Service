// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;

namespace EdFi.DataManagementService.Backend.Plans;

/// <summary>
/// Structural, plan-aware strip pass that removes the <c>link</c> property from every
/// document-reference object reachable from a <see cref="CompiledReconstitutionPlan"/>'s
/// <see cref="TableReconstitutionPlan.ReferenceBindingsInOrder"/>. Invoked as the final
/// response-shaping pass in the repository wrapper — after the materializer injects API
/// metadata (<c>id</c>/<c>_etag</c>/<c>_lastModifiedDate</c>) and after readable-profile
/// projection (when applicable) runs — when <see cref="ResourceLinksOptions.Enabled"/> is
/// <see langword="false"/>. The materializer's reconstituted intermediate is always
/// link-bearing; this pass governs only the served body.
/// </summary>
/// <remarks>
/// Walks each compiled reference path segment-by-segment from the document root, branching
/// at <see cref="JsonPathSegment.AnyArrayElement"/>. Identity fields, <c>_etag</c>, and
/// <c>_lastModifiedDate</c> are never touched — only the <c>link</c> property is removed
/// from reference-object containers.
/// </remarks>
internal static class LinkSubtreeStripper
{
    private const string LinkPropertyName = "link";

    public static void Strip(JsonNode? document, CompiledReconstitutionPlan compiledPlan)
    {
        ArgumentNullException.ThrowIfNull(compiledPlan);

        if (document is not JsonObject root)
        {
            return;
        }

        foreach (var tablePlan in compiledPlan.TablePlansInDependencyOrder)
        {
            foreach (var binding in tablePlan.ReferenceBindingsInOrder)
            {
                StripAtReferencePath(root, binding.ReferenceObjectPath.Segments);
            }
        }
    }

    private static void StripAtReferencePath(JsonObject root, IReadOnlyList<JsonPathSegment> segments)
    {
        // Reference object paths always end on a Property segment naming the reference object.
        // The reference container is at segments[..^1]; we remove "link" from that container.
        if (segments.Count == 0 || segments[^1] is not JsonPathSegment.Property finalProperty)
        {
            return;
        }

        VisitContainers(
            root,
            segments,
            startIndex: 0,
            container => RemoveLinkFromReferenceObject(container, finalProperty.Name)
        );
    }

    /// <summary>
    /// Walks <paramref name="segments"/> from <paramref name="startIndex"/> up to (but not
    /// including) the final segment, visiting every concrete container reachable through
    /// property hops and array branches. Invokes <paramref name="onContainer"/> with each
    /// container whose direct child property is the reference-object name.
    /// </summary>
    private static void VisitContainers(
        JsonNode current,
        IReadOnlyList<JsonPathSegment> segments,
        int startIndex,
        Action<JsonObject> onContainer
    )
    {
        // segments[^1] is the reference-object property; visit containers up to segments[^2].
        for (var i = startIndex; i < segments.Count - 1; i++)
        {
            switch (segments[i])
            {
                case JsonPathSegment.Property property:
                    if (current is not JsonObject obj || obj[property.Name] is not JsonNode child)
                    {
                        return;
                    }
                    current = child;
                    break;

                case JsonPathSegment.AnyArrayElement:
                    if (current is not JsonArray array)
                    {
                        return;
                    }
                    foreach (var item in array.Where(static item => item is not null))
                    {
                        VisitContainers(item!, segments, i + 1, onContainer);
                    }
                    return;

                default:
                    return;
            }
        }

        if (current is JsonObject container)
        {
            onContainer(container);
        }
    }

    private static void RemoveLinkFromReferenceObject(JsonObject container, string referenceObjectName)
    {
        if (container[referenceObjectName] is not JsonObject referenceObject)
        {
            return;
        }

        referenceObject.Remove(LinkPropertyName);
    }
}
