// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Backend.Postgresql;

internal static class SecurityHelper
{
    /// <summary>
    /// Converts a DocumentSecurityElements to JsonElement form for storage
    /// </summary>
    public static JsonElement ToJsonElement(this DocumentSecurityElements documentSecurityElements)
    {
        return JsonSerializer.Deserialize<JsonElement>(
            new JsonObject
            {
                ["Namespace"] = new JsonArray([.. documentSecurityElements.Namespace]),
                ["EducationOrganization"] = new JsonArray(
                    documentSecurityElements
                        .EducationOrganization.Select(eo => JsonValue.Create(eo))
                        .ToArray<JsonNode?>()
                ),
            }
        );
    }
}
