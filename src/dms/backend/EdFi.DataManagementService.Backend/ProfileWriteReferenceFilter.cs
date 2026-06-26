// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Backend;

/// <summary>
/// Restricts the document/descriptor references that the relational write executor
/// resolves to those still present in the profile-shaped write body.
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
    /// resolves to an existing node in <paramref name="body"/>. A present member whose value
    /// is <c>null</c> still counts as existing, matching the writable shaper's
    /// "member was submitted" semantics.
    /// </summary>
    /// <remarks>
    /// Reuses the shared concrete-path parse + navigate helpers rather than re-implementing
    /// path walking, so the property/array-ordinal semantics live in one place. See
    /// <see cref="RelationalJsonPathSupport.ParseConcretePath"/> +
    /// <see cref="RelationalWriteFlattener.TryNavigateConcreteNode"/> and the same pairing in
    /// <c>RelationalWriteProfileMerge.ResolveCollectionItemNode</c>.
    /// </remarks>
    internal static bool PathExists(JsonNode body, string concretePath)
    {
        try
        {
            var parsed = RelationalJsonPathSupport.ParseConcretePath(new JsonPath(concretePath));
            var segments = RelationalJsonPathSupport.GetRestrictedSegments(
                new JsonPathExpression(parsed.WildcardPath, [])
            );

            return RelationalWriteFlattener.TryNavigateConcreteNode(
                body,
                segments,
                parsed.OrdinalPath.AsSpan(),
                out _
            );
        }
        catch (InvalidOperationException)
        {
            // ParseConcretePath / GetRestrictedSegments throw for malformed input; a path
            // that cannot be parsed cannot resolve, so treat it as absent.
            return false;
        }
    }
}
