// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EdFi.DataManagementService.Core.Configuration;

namespace EdFi.DataManagementService.Backend;

internal static class DocumentCacheTelemetryLabel
{
    public const string Unknown = "unknown";
    public const string None = "none";

    public static string LowerCamel<TEnum>(TEnum value)
        where TEnum : struct, Enum
    {
        if (!Enum.IsDefined(value))
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, "Unsupported telemetry enum value.");
        }

        return JsonNamingPolicy.CamelCase.ConvertName(value.ToString());
    }
}

internal static class DocumentCacheTelemetryTargetLabel
{
    private const string Prefix = "t1_";
    private const string HashDomain = "document-cache-target-v1";
    private const int LabelHashByteCount = 12;

    public static string FromTargetKey(DocumentCacheTargetKey? targetKey) =>
        targetKey is null
            ? DocumentCacheTelemetryLabel.Unknown
            : FromParts(targetKey.TenantKey, targetKey.DataStoreId);

    public static string FromProjectionTargetKey(DocumentCacheProjectionTargetKey? targetKey) =>
        targetKey is null
            ? DocumentCacheTelemetryLabel.Unknown
            : FromParts(targetKey.TenantKey, targetKey.DataStoreId.Value);

    private static string FromParts(string tenantKey, long dataStoreId)
    {
        if (dataStoreId <= 0)
        {
            return DocumentCacheTelemetryLabel.Unknown;
        }

        string canonicalTenantKey = tenantKey.ToLowerInvariant();
        string material =
            HashDomain
            + '\0'
            + canonicalTenantKey
            + '\0'
            + dataStoreId.ToString(CultureInfo.InvariantCulture);
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(material));
        return Prefix + Convert.ToHexString(hash, 0, LabelHashByteCount).ToLowerInvariant();
    }
}
