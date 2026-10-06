// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Integration;

internal sealed class ConnectReadinessTimeoutException(
    int probes,
    int lastStatusCode,
    string lastFailure,
    bool injected = false
) : TimeoutException("Kafka Connect REST API did not become ready within 90 seconds.")
{
    public bool Injected { get; } = injected;
    public int Probes { get; } = probes;
    public int LastStatusCode { get; } = lastStatusCode;
    public string LastFailure { get; } = lastFailure;
}

internal sealed partial class CdcConnectorTemplatePinnedImageFixture
{
    private async Task WaitForKafkaConnectWithRecoveryAsync(
        CancellationToken cancellationToken,
        Func<CdcConnectorTemplatePinnedImageFixture, CancellationToken, Task> waitForConnect
    )
    {
        bool injectedTimeout = false;
        for (int attempt = 1; attempt <= 2; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _startupStage = "read-connect-port";
            Uri endpoint = await ReadMappedConnectBaseUriAsync(cancellationToken);
            // Docker may allocate a new port. HttpClient.BaseAddress cannot change after a request.
            _httpClient.Dispose();
            _httpClient = new HttpClient { BaseAddress = endpoint };
            _startupStage = "wait-for-connect";
            try
            {
                await (
                    waitForConnect is null
                        ? WaitForKafkaConnectAsync(cancellationToken)
                        : waitForConnect(this, cancellationToken)
                );
            }
            catch (ConnectReadinessTimeoutException exception)
            {
                cancellationToken.ThrowIfCancellationRequested();
                injectedTimeout = exception.Injected;
                bool recreate = attempt == 1 && !_settings.KeepContainers;
                // Retain both timeouts before removing anything. Failure to retain evidence stops recovery.
                await WriteConnectReadinessEvidenceAsync(
                    attempt,
                    exception,
                    recreate,
                    injectedTimeout,
                    cancellationToken
                );
                cancellationToken.ThrowIfCancellationRequested();
                if (!recreate)
                {
                    throw;
                }
                _startupStage = "recreate-connect";
                if (_controllerComposeKafka)
                {
                    await RunControllerComposeAsync(
                        ["rm", "--stop", "--force", "kafka-cdc-worker"],
                        cancellationToken
                    );
                }
                else
                {
                    await _docker.RunAsync(["rm", "-f", "-v", ConnectContainerName], cancellationToken);
                }
                cancellationToken.ThrowIfCancellationRequested();
                await StartKafkaConnectAsync(cancellationToken);
                continue;
            }
            if (attempt == 2)
            {
                await WriteConnectEvidenceAsync(
                    new
                    {
                        Provider = Provider.ToString(),
                        Stage = "connect-readiness",
                        Attempt = attempt,
                        Outcome = "Recovered",
                        Injected = injectedTimeout,
                    },
                    cancellationToken
                );
            }
            return;
        }
    }

    private async Task WriteConnectReadinessEvidenceAsync(
        int attempt,
        ConnectReadinessTimeoutException exception,
        bool recreate,
        bool injected,
        CancellationToken cancellationToken
    )
    {
        string status = "Unavailable";
        int exitCode = -1;
        bool oomKilled = false;
        string logs = "";
        bool logsAvailable = false;
        using var inspection = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        inspection.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            var result = await _docker.RunAllowingFailureAsync(
                ["inspect", "--format", "{{json .State}}", ConnectContainerName],
                inspection.Token
            );
            if (result.ExitCode == 0)
            {
                using var document = JsonDocument.Parse(result.StandardOutput);
                var state = document.RootElement;
                string observed = state.GetProperty("Status").GetString() ?? "";
                if (
                    observed
                    is "created"
                        or "running"
                        or "paused"
                        or "restarting"
                        or "removing"
                        or "exited"
                        or "dead"
                )
                {
                    status = observed;
                    exitCode = state.GetProperty("ExitCode").GetInt32();
                    oomKilled = state.GetProperty("OOMKilled").GetBoolean();
                }
            }
        }
        catch (Exception)
        {
            // Best-effort inspection; never substitute arbitrary Docker output for structured evidence.
        }
        try
        {
            var result = await _docker.RunAllowingFailureAsync(
                ["logs", "--tail", "200", ConnectContainerName],
                inspection.Token
            );
            if (result.ExitCode == 0)
            {
                logs = result.StandardOutput + "\n" + result.StandardError;
                logsAvailable = true;
            }
        }
        catch (Exception)
        {
            // Missing logs remain explicit. Caller cancellation is checked before retaining/restarting.
        }
        cancellationToken.ThrowIfCancellationRequested();
        // Only fixed log signals and a digest cross the artifact boundary. No raw prose, URLs,
        // worker configuration, exception messages, or credentials are published.
        string[] markers =
        [
            "OutOfMemoryError",
            "TimeoutException",
            "ConnectException",
            "UnknownHostException",
            "SaslAuthenticationException",
            "SSLHandshakeException",
            "ERROR",
            "WARN",
        ];
        await WriteConnectEvidenceAsync(
            new
            {
                Provider = Provider.ToString(),
                Stage = "connect-readiness",
                Attempt = attempt,
                Outcome = "TimedOut",
                Injected = injected,
                RecreationPermitted = recreate,
                exception.Probes,
                exception.LastStatusCode,
                exception.LastFailure,
                Container = new
                {
                    Status = status,
                    ExitCode = exitCode,
                    OomKilled = oomKilled,
                },
                Logs = new
                {
                    Available = logsAvailable,
                    TailLines = 200,
                    Sha256 = logsAvailable
                        ? Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(logs)))
                        : "",
                    Markers = markers
                        .Where(marker => logs.Contains(marker, StringComparison.Ordinal))
                        .ToArray(),
                },
            },
            cancellationToken
        );
    }

    private static async Task WriteConnectEvidenceAsync<T>(T evidence, CancellationToken cancellationToken)
    {
        string path = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "admission-evidence-connect-readiness-" + Guid.NewGuid().ToString("N") + ".json"
        );
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(evidence), cancellationToken);
        TestContext.AddTestAttachment(path, "Sanitized Connect readiness recovery evidence");
    }
}
