// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Backend;

namespace EdFi.DataManagementService.Core;

/// <summary>
/// Default no-op implementation of IDatabaseFingerprintReader that always returns null.
/// Used as a DI fallback when no dialect-specific reader has been registered (e.g. MSSQL
/// before its reader is implemented). Backend-specific registrations override this default.
/// </summary>
internal sealed class NullDatabaseFingerprintReader : IDatabaseFingerprintReader
{
    public Task<DatabaseFingerprint?> ReadFingerprintAsync(string connectionString) =>
        Task.FromResult<DatabaseFingerprint?>(null);
}
