// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;

namespace EdFi.DataManagementService.Backend.RelationalModel.Build.Steps.ExtractInputs;

/// <summary>
/// Extracts securable element metadata from a resource's ApiSchema.json node.
/// </summary>
internal static class SecurableElementsExtractor
{
    /// <summary>
    /// Extracts <see cref="ResourceSecurableElements"/> from the resource schema JSON node.
    /// Returns <see cref="ResourceSecurableElements.Empty"/> if no securable elements are present
    /// (e.g., for descriptor resources).
    /// </summary>
    public static ResourceSecurableElements ExtractSecurableElements(JsonObject resourceSchema)
    {
        var securableElementsNode = resourceSchema["securableElements"];
        if (securableElementsNode is null)
        {
            return ResourceSecurableElements.Empty;
        }

        var edOrg = ExtractEdOrgElements(securableElementsNode);
        var ns = ExtractStringPaths(securableElementsNode, "Namespace");
        var student = ExtractStringPaths(securableElementsNode, "Student");
        var contact = ExtractStringPaths(securableElementsNode, "Contact");
        var staff = ExtractStringPaths(securableElementsNode, "Staff");

        return new ResourceSecurableElements(edOrg, ns, student, contact, staff);
    }

    private static IReadOnlyList<EdOrgSecurableElement> ExtractEdOrgElements(JsonNode securableElementsNode)
    {
        var edOrgArray = securableElementsNode["EducationOrganization"]?.AsArray();
        if (edOrgArray is null || edOrgArray.Count == 0)
        {
            return [];
        }

        var result = new EdOrgSecurableElement[edOrgArray.Count];
        for (int i = 0; i < edOrgArray.Count; i++)
        {
            var item = edOrgArray[i]!;
            var jsonPath = item["jsonPath"]!.GetValue<string>();
            var metaEdName = item["metaEdName"]!.GetValue<string>();
            result[i] = new EdOrgSecurableElement(jsonPath, metaEdName);
        }

        return result;
    }

    private static IReadOnlyList<string> ExtractStringPaths(JsonNode securableElementsNode, string key)
    {
        var array = securableElementsNode[key]?.AsArray();
        if (array is null || array.Count == 0)
        {
            return [];
        }

        var result = new string[array.Count];
        for (int i = 0; i < array.Count; i++)
        {
            result[i] = array[i]!.GetValue<string>();
        }

        return result;
    }
}
