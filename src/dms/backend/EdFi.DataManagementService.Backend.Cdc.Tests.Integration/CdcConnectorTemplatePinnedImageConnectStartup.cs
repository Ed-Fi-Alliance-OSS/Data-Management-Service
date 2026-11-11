// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Integration;

internal sealed partial class CdcConnectorTemplatePinnedImageFixture
{
    private sealed record ConnectStartupAttempt(int Attempt, int ExitCode, string Category);

    private async Task StartKafkaConnectWithRetryAsync(
        CancellationToken cancellationToken,
        CdcFixturePorts initialPorts = null!
    )
    {
        const int maximumAttempts = 3;
        List<ConnectStartupAttempt> attempts = [];
        try
        {
            for (int attempt = 1; attempt <= maximumAttempts; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (initialPorts is not null)
                {
                    initialPorts.Release(1);
                    initialPorts.Release(2);
                }
                DockerCommandResult result = _controllerComposeKafka
                    ? await RunControllerComposeAsync(
                        ["up", "--detach", "--wait", "kafka-cdc-worker"],
                        cancellationToken,
                        allowingFailure: true
                    )
                    : await _docker.RunAllowingFailureAsync(
                        BuildKafkaConnectRunArguments(),
                        cancellationToken
                    );
                bool portConflict = IsPortBindFailure(result.StandardError);
                string category = (result.ExitCode, portConflict) switch
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
                if (!portConflict || attempt == maximumAttempts)
                {
                    throw new InvalidOperationException(
                        $"Connect startup failed: attempt={attempt}, exitCode={result.ExitCode}, category={category}."
                    );
                }

                // Docker can leave a created container after a bind failure. Cleanup must finish
                // before retry, even if the caller canceled while Docker was starting it.
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                await _docker.RunAsync(["rm", "-f", "-v", ConnectContainerName], cleanup.Token);
                cancellationToken.ThrowIfCancellationRequested();
                if (initialPorts is not null)
                {
                    initialPorts.Replace(1);
                    initialPorts.Replace(2);
                    _controllerConnectPort = initialPorts.Ports[1];
                    _controllerMetricsPort = initialPorts.Ports[2];
                    if (_controllerComposeKafka)
                    {
                        await PrepareControllerComposeAsync(cancellationToken);
                    }
                }
                else
                {
                    // Recovery of an already launched worker retains the endpoints held by clients.
                    await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
                }
            }
        }
        finally
        {
            try
            {
                string path = Path.Combine(
                    TestContext.CurrentContext.WorkDirectory,
                    "admission-evidence-connect-startup-" + Guid.NewGuid().ToString("N") + ".json"
                );
                await File.WriteAllTextAsync(
                    path,
                    JsonSerializer.Serialize(
                        new
                        {
                            Provider = Provider.ToString(),
                            Stage = "start-kafka-connect",
                            Attempts = attempts,
                        }
                    )
                );
                TestContext.AddTestAttachment(path);
            }
            catch (Exception)
            {
                // Best-effort evidence must not replace the startup or cleanup outcome.
            }
        }
    }
}
