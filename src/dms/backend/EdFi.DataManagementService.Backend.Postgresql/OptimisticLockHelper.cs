// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;

namespace EdFi.DataManagementService.Backend.Postgresql;

internal static class OptimisticLockHelper
{
    private const string IfMatchHeader = "If-Match";
    private const string ETagElement = "_etag";

    public static bool IsDocumentLocked(
        Dictionary<string, string> requestHeaders,
        JsonElement existingDocument
    )
    {
        if (!requestHeaders.TryGetValue(IfMatchHeader, out var ifMatchEtag))
        {
            return false;
        }

        if (!existingDocument.TryGetProperty(ETagElement, out JsonElement existingEtagElement))
        {
            return false;
        }

        return ifMatchEtag != existingEtagElement.GetString();
    }
}
