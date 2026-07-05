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
        ValidateRootDocument(document);

        var hash = CanonicalJsonSerializer.ComputeSha256HashExcludingPropertyNames(
            document,
            ServerGeneratedFieldNames.Names
        );

        return Convert.ToBase64String(hash);
    }

    private static void ValidateRootDocument(JsonNode document)
    {
        ArgumentNullException.ThrowIfNull(document);

        if (document is not JsonObject)
        {
            throw new InvalidOperationException("API ETag formatting requires a root JSON object.");
        }
    }
}
