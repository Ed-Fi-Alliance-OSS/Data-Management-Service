// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Security.Cryptography;
using System.Text;

namespace EdFi.DataManagementService.Backend.Etag;

/// <summary>
/// Derives the stable <c>profileCode</c> component of a <see cref="VariantKey"/> from the readable
/// profile name. Uses a short lowercase-hex SHA-256 prefix (the ADR's opaque form) because a
/// MappingSet exposes no enumerable profile catalog from which to assign stable ordinals. Returns
/// <see cref="VariantKey.NoProfileCode"/> when no readable profile applies. The prefix is stable
/// across processes and contains only <c>etagc</c>-safe characters.
/// </summary>
public static class ProfileVariantCode
{
    private const int PrefixByteCount = 4; // 8 hex characters

    public static string Of(string? profileName)
    {
        if (profileName is null)
        {
            return VariantKey.NoProfileCode;
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(profileName));
        return Convert.ToHexString(hash, 0, PrefixByteCount).ToLowerInvariant();
    }
}
