// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Tests.Integration;

internal static class HttpResponseEtagExtensions
{
    /// <summary>
    /// Reads the raw ETag header. <see cref="System.Net.Http.Headers.HttpResponseHeaders.ETag"/>
    /// requires the strict RFC 7232 quoted form, but DMS emits an opaque
    /// unquoted value (the API metadata fingerprint), so the strongly typed
    /// property returns null. Routing through <c>TryGetValues</c> preserves
    /// whatever DMS actually sent.
    /// </summary>
    public static bool TryReadRawEtag(this HttpResponseMessage response, out string etag)
    {
        if (response.Headers.TryGetValues("ETag", out IEnumerable<string>? values))
        {
            string? first = values.FirstOrDefault();
            if (!string.IsNullOrEmpty(first))
            {
                etag = first;
                return true;
            }
        }
        etag = string.Empty;
        return false;
    }
}
