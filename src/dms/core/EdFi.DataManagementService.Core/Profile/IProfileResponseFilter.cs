// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Core.Profile;

/// <summary>
/// Filters JSON documents according to profile ReadContentType rules.
/// </summary>
internal interface IProfileResponseFilter
{
    /// <summary>
    /// Filters a JSON document according to profile rules, preserving identity fields.
    /// </summary>
    /// <param name="document">The JSON document to filter</param>
    /// <param name="contentType">The profile content type rules defining what to include/exclude</param>
    /// <param name="identityPropertyNames">Set of top-level property names that must always be preserved</param>
    /// <returns>A new filtered JSON document</returns>
    JsonNode FilterDocument(
        JsonNode document,
        ContentTypeDefinition contentType,
        HashSet<string> identityPropertyNames
    );

    /// <summary>
    /// Extracts top-level property names from JSON paths.
    /// Call this once and reuse the result when filtering multiple documents.
    /// </summary>
    /// <param name="identityJsonPaths">Paths to identity fields</param>
    /// <returns>A set of top-level property names to preserve</returns>
    HashSet<string> ExtractIdentityPropertyNames(IEnumerable<JsonPath> identityJsonPaths);
}
