// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;
using EdFi.DataManagementService.Core.Utilities;

namespace EdFi.DataManagementService.DocumentCacheAdmin;

internal static class DocumentCacheAdminOutput
{
    private const int MaximumDiagnosticLength = 512;
    private const int MaximumLabelLength = 128;

    private static readonly string[] SensitiveDiagnosticMarkers =
    [
        "connectionstring",
        "connection string",
        "documentuuid",
        "studentuniqueid",
        "clientsecret",
        "client secret",
        "secret",
        "password",
        "pwd",
        "apikey",
        "api key",
        "raw target json",
    ];

    public static string TargetSurrogate(DocumentCacheTargetKey targetKey)
    {
        ArgumentNullException.ThrowIfNull(targetKey);
        return DocumentCacheTelemetryTargetLabel.FromTargetKey(targetKey);
    }

    public static string TargetSurrogate(DocumentCacheStatusTargetKey targetKey)
    {
        ArgumentNullException.ThrowIfNull(targetKey);
        return TargetSurrogate(DocumentCacheTargetKey.Create(targetKey.TenantKey, targetKey.DataStoreId));
    }

    public static string TargetSurrogate(DocumentCacheAdministrativeTargetKey targetKey)
    {
        ArgumentNullException.ThrowIfNull(targetKey);
        return TargetSurrogate(targetKey.TargetKey);
    }

    public static string BoundedLabel(string value)
    {
        string sanitized = LoggingSanitizer.SanitizeForLogging(value);
        if (string.IsNullOrWhiteSpace(sanitized))
        {
            return DocumentCacheProjectionTelemetryLabel.Unknown;
        }

        return sanitized.Length <= MaximumLabelLength ? sanitized : sanitized[..MaximumLabelLength];
    }

    public static string SanitizeDiagnostic(string? value)
    {
        string sanitized = LoggingSanitizer.SanitizeForLogging(value);
        if (string.IsNullOrWhiteSpace(sanitized))
        {
            return string.Empty;
        }

        if (ContainsSensitiveMarker(sanitized))
        {
            return "diagnostic redacted";
        }

        return sanitized.Length <= MaximumDiagnosticLength ? sanitized : sanitized[..MaximumDiagnosticLength];
    }

    public static string FingerprintPresence(DocumentCachePhysicalSourceFingerprint? fingerprint) =>
        fingerprint is null ? "null" : "present";

    private static bool ContainsSensitiveMarker(string sanitized) =>
        Array.Exists(
            SensitiveDiagnosticMarkers,
            marker => sanitized.Contains(marker, StringComparison.OrdinalIgnoreCase)
        );
}
