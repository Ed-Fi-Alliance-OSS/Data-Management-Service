// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Backend;
using Microsoft.Extensions.Options;

namespace EdFi.DataManagementService.Core;

/// <summary>
/// Placeholder fingerprint reader used until the host registers a dialect-specific reader.
/// </summary>
internal sealed class MissingDatabaseFingerprintReader(IOptions<AppSettings> appSettings)
    : IDatabaseFingerprintReader
{
    internal const string ConfigurationErrorMessage =
        "UseRelationalBackend is enabled, but no dialect-specific IDatabaseFingerprintReader is registered. Register the PostgreSQL or MSSQL fingerprint reader in the host composition.";

    public Task<DatabaseFingerprint?> ReadFingerprintAsync(string connectionString)
    {
        if (appSettings.Value.UseRelationalBackend)
        {
            throw CreateConfigurationException();
        }

        return Task.FromResult<DatabaseFingerprint?>(null);
    }

    internal static InvalidOperationException CreateConfigurationException() =>
        new(ConfigurationErrorMessage);
}
