// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Integration;

// Temporary diagnostic branch only. The wrapper creates a private directory with umask 077,
// encrypts these files after qualification, and uploads only ciphertext and fixed metadata.
internal static class CdcSqlFailureCapture
{
    private const int MaximumStreamCharacters = 65536;
    private static int _captureCount;

    internal static async Task CaptureAsync(DockerCommandResult result, CancellationToken cancellationToken)
    {
        string directory =
            Environment.GetEnvironmentVariable("CDC_SQL_EXIT_CAPTURE_DIRECTORY") ?? string.Empty;
        if (result.ExitCode != 0 || directory.Length == 0 || Interlocked.Increment(ref _captureCount) > 128)
        {
            return;
        }

        try
        {
            string path = Path.Combine(directory, $"sql-exit-{Guid.NewGuid():N}.json");
            await using var stream = new FileStream(
                path,
                new FileStreamOptions
                {
                    Mode = FileMode.CreateNew,
                    Access = FileAccess.Write,
                    Share = FileShare.None,
                    Options = FileOptions.Asynchronous,
                }
            );
            string test = TestContext.CurrentContext.Test.FullName;
            await JsonSerializer.SerializeAsync(
                stream,
                new
                {
                    ObservedAt = DateTimeOffset.UtcNow,
                    Test = test[..Math.Min(test.Length, 512)],
                    StandardOutput = result.StandardOutput[
                        ..Math.Min(result.StandardOutput.Length, MaximumStreamCharacters)
                    ],
                    StandardError = result.StandardError[
                        ..Math.Min(result.StandardError.Length, MaximumStreamCharacters)
                    ],
                    Truncated = result.StandardOutput.Length > MaximumStreamCharacters
                        || result.StandardError.Length > MaximumStreamCharacters,
                },
                cancellationToken: cancellationToken
            );
        }
        catch (Exception)
        {
            // Share the existing failure-inspection deadline; never alter the failure or cleanup.
        }
    }
}
