// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace EdFi.DataManagementService.Core.Backend;

internal static class DocumentComparer
{
    public static string GenerateContentHash(JsonNode document, bool excludeMetadata = false)
    {
        var parsedBody = document.DeepClone() as JsonObject;
        if (excludeMetadata)
        {
            parsedBody!.Remove("_etag");
            parsedBody!.Remove("_lastModifiedDate");
            parsedBody!.Remove("id");
        }

        var parsedJson = JsonSerializer.Serialize(parsedBody);

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(parsedJson));
        return Convert.ToBase64String(hash);
    }

    public static bool DocumentsAreEquivalent(JsonNode doc1, JsonNode doc2)
    {
        return GenerateContentHash(doc1, excludeMetadata: true)
            == GenerateContentHash(doc2, excludeMetadata: true);
    }
}
