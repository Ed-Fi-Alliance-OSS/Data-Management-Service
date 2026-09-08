// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core.External.Backend;

/// <summary>
/// Reads the fingerprint fields from the dms.EffectiveSchema singleton row.
/// </summary>
public interface IDatabaseFingerprintReader
{
    /// <summary>
    /// Reads the four fingerprint fields from the dms.EffectiveSchema singleton row
    /// for the database at the given connection string.
    /// </summary>
    /// <param name="connectionString">The database connection string to read from.</param>
    /// <returns>The fingerprint if the table and singleton row exist;
    /// <c>null</c> if the table is missing or has no data.</returns>
    /// <exception cref="DatabaseFingerprintValidationException">
    /// Thrown when the dms.EffectiveSchema table exists but contains malformed singleton content.
    /// </exception>
    Task<DatabaseFingerprint?> ReadFingerprintAsync(string connectionString);
}
