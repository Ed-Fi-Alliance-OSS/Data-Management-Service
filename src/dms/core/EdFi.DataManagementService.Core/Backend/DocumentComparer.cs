// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Security.Cryptography;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.Utilities;

namespace EdFi.DataManagementService.Core.Backend;

internal static class DocumentComparer
{
    public static string GenerateContentHash(JsonNode document)
    {
        var parsedBody = document.DeepClone() as JsonObject;
        parsedBody!.Remove("_etag");
        parsedBody!.Remove("_lastModifiedDate");
        parsedBody!.Remove("id");

        byte[] hash = SHA256.HashData(CanonicalJsonSerializer.SerializeToUtf8Bytes(parsedBody));
        return Convert.ToBase64String(hash);
    }
}
