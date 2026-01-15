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
    /// <param name="identityJsonPaths">Paths to identity fields that must always be preserved</param>
    /// <returns>A new filtered JSON document</returns>
    JsonNode FilterDocument(
        JsonNode document,
        ContentTypeDefinition contentType,
        IEnumerable<JsonPath> identityJsonPaths
    );
}
