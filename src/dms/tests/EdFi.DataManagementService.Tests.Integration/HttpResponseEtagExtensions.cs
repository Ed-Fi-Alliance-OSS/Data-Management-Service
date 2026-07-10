// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Tests.Integration;

internal static class HttpResponseEtagExtensions
{
    /// <summary>
    /// Reads the ETag header and normalizes it to an unquoted opaque value.
    /// DMS emits ETag headers as RFC 9110 §8.8.3 quoted strong validators (e.g., "1-3581150b.j._.n"),
    /// which are parsed back to their unquoted form for test assertions.
    /// </summary>
    public static bool TryReadRawEtag(this HttpResponseMessage response, out string etag)
    {
        if (response.Headers.TryGetValues("ETag", out IEnumerable<string>? values))
        {
            string? first = values.FirstOrDefault();
            if (
                !string.IsNullOrEmpty(first)
                && EdFi.DataManagementService.Core.Utilities.EtagValue.TryParseHeaderValue(
                    first,
                    out var parsed
                )
            )
            {
                etag = parsed;
                return true;
            }
        }
        etag = string.Empty;
        return false;
    }
}
