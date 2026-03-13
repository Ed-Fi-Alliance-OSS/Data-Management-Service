// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Backend;
using Microsoft.Extensions.Options;

namespace EdFi.DataManagementService.Core;

/// <summary>
/// Placeholder resource key row reader used until the host registers a dialect-specific reader.
/// </summary>
internal sealed class MissingResourceKeyRowReader(IOptions<AppSettings> appSettings) : IResourceKeyRowReader
{
    internal const string ConfigurationErrorMessage =
        "UseRelationalBackend is enabled, but no dialect-specific IResourceKeyRowReader is registered. "
        + "Register the PostgreSQL or MSSQL resource key row reader in the host composition root "
        + "(see WebApplicationBuilderExtensions.ConfigureDatastore for an example).";

    public Task<IReadOnlyList<ResourceKeyRow>> ReadResourceKeyRowsAsync(string connectionString)
    {
        if (appSettings.Value.UseRelationalBackend)
        {
            throw CreateConfigurationException();
        }

        return Task.FromResult<IReadOnlyList<ResourceKeyRow>>([]);
    }

    internal static InvalidOperationException CreateConfigurationException() =>
        new(ConfigurationErrorMessage);
}
