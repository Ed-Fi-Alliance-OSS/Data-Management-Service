// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Integration;

internal static class CdcFixtureDockerStartup
{
    internal sealed record Attempt(int Number, int ExitCode, string Category);

    internal static async Task RunAsync(
        Func<CancellationToken, Task<DockerCommandResult>> start,
        Func<CancellationToken, Task> remove,
        Action prepareRetry,
        CancellationToken token
    )
    {
        const int maximumAttempts = 3;
        List<Attempt> attempts = [];
        try
        {
            for (int attempt = 1; attempt <= maximumAttempts; attempt++)
            {
                token.ThrowIfCancellationRequested();
                var result = await start(token);
                bool conflict = CdcConnectorTemplatePinnedImageFixture.IsPortBindFailure(
                    result.StandardError
                );
                string category = (result.ExitCode, conflict) switch
                {
                    (0, _) => "Succeeded",
                    (_, true) => "PortBindingConflict",
                    _ => "OtherDockerFailure",
                };
                attempts.Add(new(attempt, result.ExitCode, category));
                if (result.ExitCode == 0)
                {
                    return;
                }
                if (!conflict || attempt == maximumAttempts)
                {
                    throw new InvalidOperationException(
                        $"Fixture startup failed: attempt={attempt}, exitCode={result.ExitCode}, category={category}."
                    );
                }
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                await remove(cleanup.Token);
                token.ThrowIfCancellationRequested();
                prepareRetry();
            }
        }
        finally
        {
            try
            {
                string path = Path.Combine(
                    TestContext.CurrentContext.WorkDirectory,
                    "cdc-controller-startup-" + Guid.NewGuid().ToString("N") + ".json"
                );
                // No operation token: cancellation must not erase failure evidence.
                await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new { Attempts = attempts }));
                TestContext.AddTestAttachment(path);
            }
            catch (Exception)
            {
                // Preserve the original startup/cleanup failure if evidence storage also fails.
            }
        }
    }
}
