// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.Utilities;

namespace EdFi.DataManagementService.Backend;

internal static class RelationalApiMetadataFormatter
{
    private static readonly string[] ServerGeneratedPropertyNames =
    [
        "id",
        "link",
        "_etag",
        "_lastModifiedDate",
    ];

    public static string FormatEtag(JsonNode document)
    {
        var canonicalDocument = BuildCanonicalDocument(document);

        var hash = SHA256.HashData(CanonicalJsonSerializer.SerializeToUtf8Bytes(canonicalDocument));

        return Convert.ToBase64String(hash);
    }

    public static string FormatEtag(ExtractedDescriptorBody descriptorBody)
    {
        ArgumentNullException.ThrowIfNull(descriptorBody);

        return FormatEtag(BuildCanonicalDescriptorDocument(descriptorBody));
    }

    public static void RefreshEtag(JsonNode document)
    {
        ArgumentNullException.ThrowIfNull(document);

        if (document is not JsonObject documentObject)
        {
            throw new InvalidOperationException(
                "Relational API metadata formatting requires a root JSON object."
            );
        }

        documentObject["_etag"] = FormatEtag(documentObject);
    }

    private static JsonObject BuildCanonicalDocument(JsonNode document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var canonicalDocument = document.DeepClone();

        if (canonicalDocument is not JsonObject documentObject)
        {
            throw new InvalidOperationException(
                "Relational API metadata formatting requires a root JSON object."
            );
        }

        RemoveServerGeneratedFields(documentObject);

        return documentObject;
    }

    private static void RemoveServerGeneratedFields(JsonNode node)
    {
        switch (node)
        {
            case JsonObject objectNode:
                foreach (var propertyName in ServerGeneratedPropertyNames)
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

    private static JsonObject BuildCanonicalDescriptorDocument(ExtractedDescriptorBody descriptorBody)
    {
        var document = new JsonObject
        {
            ["namespace"] = descriptorBody.Namespace,
            ["codeValue"] = descriptorBody.CodeValue,
        };

        if (descriptorBody.ShortDescription is not null)
        {
            document["shortDescription"] = descriptorBody.ShortDescription;
        }

        if (descriptorBody.Description is not null)
        {
            document["description"] = descriptorBody.Description;
        }

        if (descriptorBody.EffectiveBeginDate is DateOnly effectiveBeginDate)
        {
            document["effectiveBeginDate"] = effectiveBeginDate.ToString(
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture
            );
        }

        if (descriptorBody.EffectiveEndDate is DateOnly effectiveEndDate)
        {
            document["effectiveEndDate"] = effectiveEndDate.ToString(
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture
            );
        }

        return document;
    }
}
