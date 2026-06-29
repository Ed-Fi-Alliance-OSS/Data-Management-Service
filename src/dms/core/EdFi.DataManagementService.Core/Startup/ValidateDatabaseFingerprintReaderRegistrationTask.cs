// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Backend;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Core.Startup;

/// <summary>
/// Validates that relational fingerprint validation has a concrete backend reader.
/// </summary>
internal sealed class ValidateDatabaseFingerprintReaderRegistrationTask(
    IDatabaseFingerprintReader fingerprintReader,
    ILogger<ValidateDatabaseFingerprintReaderRegistrationTask> logger
) : IDmsStartupTask
{
    public int Order => 50;

    public string Name => "Validate Database Fingerprint Reader Registration";

    public Task ExecuteAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (fingerprintReader is MissingDatabaseFingerprintReader or NullDatabaseFingerprintReader)
        {
            throw MissingDatabaseFingerprintReader.CreateConfigurationException();
        }

        logger.LogInformation(
            "Using database fingerprint reader {FingerprintReaderType}",
            fingerprintReader.GetType().FullName
        );

        return Task.CompletedTask;
    }
}
