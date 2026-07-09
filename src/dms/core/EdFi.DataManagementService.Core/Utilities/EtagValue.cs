// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core.Utilities;

/// <summary>
/// The wire format of an API <c>_etag</c>: an opaque strong entity-tag of the form
/// "{ContentVersion}-{variantKey}". The ContentVersion component is treated as an opaque
/// string and is never parsed or compared numerically (RFC 7232 §2.3, §3.1).
/// </summary>
public static class EtagValue
{
    /// <summary>Composes the opaque etag value (unquoted) from its two parts.</summary>
    public static string Compose(string contentVersion, string variantKey) =>
        $"{contentVersion}-{variantKey}";

    /// <summary>Separator between the ContentVersion and variantKey portions of an etag value.</summary>
    public const char Separator = '-';

    /// <summary>
    /// Splits an opaque etag value "{contentVersion}-{variantKey}" into its two parts. The variantKey
    /// portion is guaranteed not to contain <see cref="Separator"/>, so the last '-' is the unambiguous
    /// boundary. Returns <see langword="false"/> (and empty out-params) when either part is empty or no
    /// separator is present. Callers that also need the header quote/weak-tag handling use
    /// <see cref="TryParseHeaderValue"/> first.
    /// </summary>
    public static bool TryParse(string? etagValue, out string contentVersion, out string variantKey)
    {
        contentVersion = string.Empty;
        variantKey = string.Empty;

        if (string.IsNullOrEmpty(etagValue))
        {
            return false;
        }

        var separator = etagValue.LastIndexOf(Separator);
        if (separator <= 0 || separator == etagValue.Length - 1)
        {
            return false;
        }

        contentVersion = etagValue[..separator];
        variantKey = etagValue[(separator + 1)..];
        return true;
    }

    /// <summary>Quotes an opaque etag value for the ETag / If-Match HTTP header (strong, no W/).</summary>
    public static string ToHeaderValue(string etagValue) => $"\"{etagValue}\"";

    /// <summary>
    /// Extracts the opaque value from a strong entity-tag header. Rejects weak (W/) tags.
    /// Tolerates a bare unquoted value for robustness against non-conforming clients.
    /// </summary>
    public static bool TryParseHeaderValue(string? headerValue, out string value)
    {
        value = string.Empty;

        if (string.IsNullOrEmpty(headerValue))
        {
            return false;
        }

        if (headerValue.StartsWith("W/", StringComparison.Ordinal))
        {
            // Weak validators are not accepted for strong If-Match comparison.
            return false;
        }

        if (headerValue.Length >= 2 && headerValue[0] == '"' && headerValue[^1] == '"')
        {
            value = headerValue[1..^1];
            return true;
        }

        value = headerValue;
        return true;
    }
}
