// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Utilities;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.SchemaTools.Provisioning;

/// <summary>
/// Centralizes the preflight schema-hash comparison logic shared by all database provisioners.
/// </summary>
public static class SchemaHashChecker
{
    /// <summary>
    /// Validates that <paramref name="storedHash"/> matches <paramref name="expectedHash"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// When <paramref name="storedHash"/> is <c>null</c>, this method returns successfully.
    /// A null value is interpreted as "no row exists yet" (i.e., the database has not been
    /// provisioned), which is a normal condition on first run.
    /// </para>
    /// <para>
    /// <b>Caller responsibility:</b> This method cannot distinguish between "table does not
    /// exist" and "table exists but the singleton row is missing." Both present as a null
    /// storedHash. Callers must detect the corrupt "table exists but no row" state themselves
    /// before calling this method, typically by checking table existence first and throwing
    /// if the table exists but the query returns null.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// Thrown when <paramref name="storedHash"/> is non-null and differs from
    /// <paramref name="expectedHash"/>.
    /// </exception>
    public static void ValidateOrThrow(string? storedHash, string expectedHash, ILogger logger)
    {
        if (storedHash is null)
        {
            return; // No row yet, proceed with provisioning
        }

        if (!string.Equals(storedHash, expectedHash, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Schema hash mismatch: the database contains schema hash '{LoggingSanitizer.SanitizeForLogging(storedHash)}' "
                    + $"but the current schema produces hash '{LoggingSanitizer.SanitizeForLogging(expectedHash)}'. "
                    + "A different schema version has already been provisioned. "
                    + "To re-provision, drop and recreate the database."
            );
        }

        logger.LogInformation("Preflight schema hash check passed (hash matches existing database)");
    }
}
