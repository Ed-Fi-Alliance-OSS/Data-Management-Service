// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Plans;

namespace EdFi.DataManagementService.Backend;

internal static class RelationalApiMetadataFormatter
{
    public static string FormatEtag(JsonNode document) => FormatEtag(document, readPlan: null);

    public static string FormatEtag(JsonNode document, ResourceReadPlan? readPlan)
    {
        ArgumentNullException.ThrowIfNull(document);

        var canonicalDocument = readPlan is null
            ? document.DeepClone()
            : DocumentReconstituter.ReorderToReadPlanOrder(document, readPlan);

        if (canonicalDocument is not JsonObject documentObject)
        {
            throw new InvalidOperationException(
                "Relational API metadata formatting requires a root JSON object."
            );
        }

        documentObject.Remove("_etag");
        documentObject.Remove("_lastModifiedDate");
        documentObject.Remove("id");

        var serializedDocument = JsonSerializer.Serialize(documentObject);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(serializedDocument));

        return Convert.ToBase64String(hash);
    }

    public static string FormatEtag(ExtractedDescriptorBody descriptorBody)
    {
        ArgumentNullException.ThrowIfNull(descriptorBody);

        return FormatEtag(BuildCanonicalDescriptorDocument(descriptorBody));
    }

    public static void RefreshEtag(JsonNode document) => RefreshEtag(document, readPlan: null);

    public static void RefreshEtag(JsonNode document, ResourceReadPlan? readPlan)
    {
        ArgumentNullException.ThrowIfNull(document);

        if (document is not JsonObject documentObject)
        {
            throw new InvalidOperationException(
                "Relational API metadata formatting requires a root JSON object."
            );
        }

        documentObject["_etag"] = FormatEtag(documentObject, readPlan);
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
