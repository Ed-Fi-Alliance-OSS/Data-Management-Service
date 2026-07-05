// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;

namespace EdFi.DataManagementService.Core.Utilities;

public static class ResourceEtagFormatter
{
    public static string FormatEtag(JsonNode document)
    {
        var canonicalDocument = BuildCanonicalDocument(document);
        var hash = CanonicalJsonSerializer.ComputeSha256Hash(canonicalDocument);

        return Convert.ToBase64String(hash);
    }

    private static JsonObject BuildCanonicalDocument(JsonNode document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var canonicalDocument = document.DeepClone();

        if (canonicalDocument is not JsonObject documentObject)
        {
            throw new InvalidOperationException("API ETag formatting requires a root JSON object.");
        }

        RemoveServerGeneratedFields(documentObject);

        return documentObject;
    }

    private static void RemoveServerGeneratedFields(JsonNode node)
    {
        switch (node)
        {
            case JsonObject objectNode:
                foreach (var propertyName in ServerGeneratedFieldNames.Names)
                {
                    objectNode.Remove(propertyName);
                }

                foreach (var (_, childNode) in objectNode)
                {
                    if (childNode is not null)
                    {
                        RemoveServerGeneratedFields(childNode);
                    }
                }

                break;

            case JsonArray arrayNode:
                for (var index = 0; index < arrayNode.Count; index++)
                {
                    var childNode = arrayNode[index];

                    if (childNode is not null)
                    {
                        RemoveServerGeneratedFields(childNode);
                    }
                }

                break;
        }
    }
}
