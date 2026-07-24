// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Backend.RelationalModel.Naming;

/// <summary>
/// Classifies whether a reference or descriptor path carries a semantic role name.
/// </summary>
internal static class ReferenceRoleNameConventions
{
    private const string ReferenceSuffix = "Reference";

    /// <summary>
    /// Returns true when the JSON reference object's leaf name does not match the target resource name.
    /// </summary>
    public static bool IsDocumentReferenceRoleNamed(
        JsonPathExpression referenceObjectPath,
        QualifiedResourceName targetResource
    )
    {
        var leafName = RequireLastPropertyName(referenceObjectPath);
        var referenceName = RelationalNameConventions.ToPascalCase(StripReferenceSuffix(leafName));

        return !string.Equals(referenceName, targetResource.ResourceName, StringComparison.Ordinal);
    }

    /// <summary>
    /// Returns true when the descriptor value path's leaf name does not match the descriptor resource name.
    /// </summary>
    public static bool IsDescriptorRoleNamed(
        JsonPathExpression descriptorValuePath,
        QualifiedResourceName descriptorResource
    )
    {
        var leafName = RequireLastPropertyName(descriptorValuePath);
        var descriptorName = RelationalNameConventions.ToPascalCase(
            descriptorValuePath.Segments[^1] is JsonPathSegment.AnyArrayElement
                ? RelationalNameConventions.SingularizeCollectionSegment(leafName)
                : leafName
        );

        return !string.Equals(descriptorName, descriptorResource.ResourceName, StringComparison.Ordinal);
    }

    private static string StripReferenceSuffix(string leafName) =>
        leafName.EndsWith(ReferenceSuffix, StringComparison.Ordinal)
            ? leafName[..^ReferenceSuffix.Length]
            : leafName;

    private static string RequireLastPropertyName(JsonPathExpression path)
    {
        for (var index = path.Segments.Count - 1; index >= 0; index--)
        {
            if (path.Segments[index] is JsonPathSegment.Property property)
            {
                return property.Name;
            }
        }

        throw new InvalidOperationException(
            $"JSONPath '{path.Canonical}' does not contain a property segment."
        );
    }
}
