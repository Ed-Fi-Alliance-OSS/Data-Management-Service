// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Concurrent;
using EdFi.DataManagementService.Backend.External;

namespace EdFi.DataManagementService.Core;

/// <summary>
/// Provides cached access to database fingerprints, keyed by connection string.
/// Thread-safe and process-lifetime scoped (singleton).
/// </summary>
internal sealed class DatabaseFingerprintProvider(IDatabaseFingerprintReader fingerprintReader)
{
    private readonly ConcurrentDictionary<string, Lazy<Task<DatabaseFingerprint?>>> _cache = new();

    /// <summary>
    /// Returns the cached fingerprint for the given connection string, or reads it
    /// from the database on first access. Concurrent first calls for the same
    /// connection string result in exactly one database read.
    /// </summary>
    public Task<DatabaseFingerprint?> GetFingerprintAsync(string connectionString)
    {
        var lazy = _cache.GetOrAdd(
            connectionString,
            static (key, reader) =>
                new Lazy<Task<DatabaseFingerprint?>>(() => reader.ReadFingerprintAsync(key)),
            fingerprintReader
        );
        return lazy.Value;
    }
}
