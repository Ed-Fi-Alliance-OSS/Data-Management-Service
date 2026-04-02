// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Core.Profile;

/// <summary>
/// Projects a fully reconstituted JSON document through a readable profile definition,
/// removing hidden members, collections, and <c>_ext</c> data while preserving identity
/// properties. Returns a new filtered document without altering the input.
/// </summary>
public interface IReadableProfileProjector
{
    /// <summary>
    /// Applies readable profile member filtering to a reconstituted JSON document.
    /// </summary>
    /// <param name="reconstitutedDocument">
    /// The full reconstituted JSON document including references, descriptors,
    /// collections, nested collections, and <c>_ext</c> data.
    /// </param>
    /// <param name="readContentType">
    /// The readable profile's content-type definition controlling member visibility.
    /// </param>
    /// <param name="identityPropertyNames">
    /// Top-level property names that must always be preserved regardless of profile rules
    /// (e.g., identity fields and reference objects containing identity fields).
    /// </param>
    /// <returns>A new profile-filtered JSON document suitable for GET/query responses.</returns>
    JsonNode Project(
        JsonNode reconstitutedDocument,
        ContentTypeDefinition readContentType,
        IReadOnlySet<string> identityPropertyNames
    );

    /// <summary>
    /// Extracts top-level property names from identity JSON paths. For nested paths like
    /// <c>$.courseOfferingReference.localCourseCode</c>, extracts the first segment
    /// (<c>courseOfferingReference</c>) so the entire reference object is preserved.
    /// </summary>
    static IReadOnlySet<string> ExtractIdentityPropertyNames(IEnumerable<JsonPath> identityJsonPaths)
    {
        return identityJsonPaths
            .Select(path => path.Value)
            .Select(pathValue =>
            {
                if (pathValue.StartsWith("$."))
                {
                    pathValue = pathValue[2..];
                }
                else if (pathValue.StartsWith('$'))
                {
                    pathValue = pathValue[1..];
                }

                int dotIndex = pathValue.IndexOf('.');
                return dotIndex > 0 ? pathValue[..dotIndex] : pathValue;
            })
            .ToHashSet();
    }
}
