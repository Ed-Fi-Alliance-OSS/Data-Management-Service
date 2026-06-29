// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Backend;

namespace EdFi.DataManagementService.Core;

/// <summary>
/// Placeholder fingerprint reader used until the host registers a dialect-specific reader.
/// </summary>
internal sealed class MissingDatabaseFingerprintReader : IDatabaseFingerprintReader
{
    internal const string ConfigurationErrorMessage =
        "No dialect-specific IDatabaseFingerprintReader is registered. Register the PostgreSQL or MSSQL fingerprint reader in the host composition root (see WebApplicationBuilderExtensions.ConfigureDatastore for an example).";

    public Task<DatabaseFingerprint?> ReadFingerprintAsync(string connectionString)
    {
        throw CreateConfigurationException();
    }

    internal static InvalidOperationException CreateConfigurationException() =>
        new(ConfigurationErrorMessage);
}
