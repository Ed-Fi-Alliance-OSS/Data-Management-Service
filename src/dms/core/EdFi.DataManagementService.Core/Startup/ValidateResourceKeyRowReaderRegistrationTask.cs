// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Backend;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EdFi.DataManagementService.Core.Startup;

/// <summary>
/// Validates that relational resource key validation has a concrete backend reader.
/// </summary>
internal sealed class ValidateResourceKeyRowReaderRegistrationTask(
    IOptions<AppSettings> appSettings,
    IResourceKeyRowReader resourceKeyRowReader,
    ILogger<ValidateResourceKeyRowReaderRegistrationTask> logger
) : IDmsStartupTask
{
    public int Order => 55;

    public string Name => "Validate Resource Key Row Reader Registration";

    public Task ExecuteAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!appSettings.Value.UseRelationalBackend)
        {
            logger.LogDebug(
                "Skipping resource key row reader registration validation because UseRelationalBackend is disabled"
            );

            return Task.CompletedTask;
        }

        if (resourceKeyRowReader is MissingResourceKeyRowReader or NullResourceKeyRowReader)
        {
            throw MissingResourceKeyRowReader.CreateConfigurationException();
        }

        logger.LogInformation(
            "Using resource key row reader {ReaderType}",
            resourceKeyRowReader.GetType().FullName
        );

        return Task.CompletedTask;
    }
}
