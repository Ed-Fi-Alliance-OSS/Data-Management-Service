// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Linq;
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
                ["Namespace"] = new JsonArray(
                    documentSecurityElements.Namespace.Select(ns => JsonValue.Create(ns)).ToArray()
                ),
                ["EducationOrganization"] = new JsonArray(
                    documentSecurityElements
                        .EducationOrganization.Select(eo => new JsonObject
                        {
                            ["Id"] = JsonValue.Create(eo.Id.Value.ToString()),
                            ["ResourceName"] = JsonValue.Create(eo.ResourceName.Value),
                        })
                        .ToArray()
                ),
                ["Student"] = new JsonArray(
                    documentSecurityElements.Student.Select(usi => JsonValue.Create(usi.Value)).ToArray()
                ),
                ["Contact"] = new JsonArray(
                    documentSecurityElements.Contact.Select(usi => JsonValue.Create(usi.Value)).ToArray()
                ),
            }.ToJsonString()
        );
    }

    /// <summary>
    /// Converts a JsonElement to DocumentSecurityElements
    /// </summary>
    public static DocumentSecurityElements ToDocumentSecurityElements(this JsonElement jsonElement)
    {
        var jsonObject = JsonNode.Parse(jsonElement.GetRawText())!.AsObject();

        var namespaces =
            jsonObject["Namespace"]?.AsArray().Select(ns => ns!.GetValue<string>()).ToArray() ?? [];

        var educationOrganizations =
            jsonObject["EducationOrganization"]
                ?.AsArray()
                .Select(eo => new EducationOrganizationSecurityElement(
                    new ResourceName(eo!["ResourceName"]!.GetValue<string>()),
                    new EducationOrganizationId(long.Parse(eo["Id"]!.GetValue<string>()))
                ))
                .ToArray() ?? [];

        var studentUniqueId =
            jsonObject["Student"]
                ?.AsArray()
                .Select(id => new StudentUniqueId(id!.GetValue<string>()))
                .ToArray() ?? [];

        var contactUniqueId =
            jsonObject["Contact"]
                ?.AsArray()
                .Select(id => new ContactUniqueId(id!.GetValue<string>()))
                .ToArray() ?? [];

        return new DocumentSecurityElements(
            namespaces,
            educationOrganizations,
            studentUniqueId,
            contactUniqueId
        );
    }
}
