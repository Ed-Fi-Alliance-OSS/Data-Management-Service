// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Integration;

internal sealed partial class CdcConnectorTemplatePinnedImageFixture
{
    public async Task<MessageContractInterruptionEvidence> InterruptDeliveryAndRestartWorkerAsync(
        Func<CancellationToken, Task> observePendingDelivery,
        CancellationToken token
    )
    {
        MessageContractInterruptionEvidence evidence = await MessageContractDeliveryInterruption.ExecuteAsync(
            _docker,
            BrokerContainerName,
            ConnectContainerName,
            observePendingDelivery,
            token
        );
        _httpClient.Dispose();
        _httpClient = new HttpClient { BaseAddress = await ReadMappedConnectBaseUriAsync(token) };
        await WaitForKafkaConnectAsync(token);
        return evidence;
    }
}

// Whole-broker interruption is delivery/replay evidence only; it is not acknowledgement-gating evidence.
internal static class MessageContractDeliveryInterruption
{
    public static async Task<MessageContractInterruptionEvidence> ExecuteAsync(
        IDockerCli docker,
        string broker,
        string worker,
        Func<CancellationToken, Task> observePendingDelivery,
        CancellationToken token
    )
    {
        List<string> phases = ["observe-worker"];
        string identity = await InspectAsync(worker, "{{.Id}}", token);
        string started = await InspectAsync(worker, "{{.State.StartedAt}}", token);
        try
        {
            phases.Add("pause-broker");
            await docker.RunAsync(["pause", broker], token);
            if (await InspectAsync(broker, "{{.State.Paused}}", token) != "true")
            {
                throw new InvalidOperationException();
            }
            phases.Add("observe-pending-delivery");
            await observePendingDelivery(token);
            // The source observer has seen the update and canonical delete while the broker is paused.
            // Terminate before restoring acknowledgements, forcing restart from retained committed offsets.
            phases.Add("kill-worker");
            await docker.RunAsync(["kill", "--signal", "KILL", worker], token);
            if (await InspectAsync(worker, "{{.State.Running}}", token) != "false")
            {
                throw new InvalidOperationException();
            }
        }
        catch (Exception)
        {
            throw new AssertionException(
                $"Message-contract delivery interruption failed in {phases[^1]}; details redacted."
            );
        }
        finally
        {
            // Independent cleanup budgets, including when pause/kill completed after caller cancellation.
            // Always attempt worker restoration even if broker restoration fails.
            bool restored = true;
            try
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                if (await InspectAsync(broker, "{{.State.Paused}}", cleanup.Token) == "true")
                {
                    await docker.RunAsync(["unpause", broker], cleanup.Token);
                }
            }
            catch (Exception)
            {
                restored = false;
            }
            try
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await docker.RunAsync(["start", worker], cleanup.Token);
            }
            catch (Exception)
            {
                restored = false;
            }
            if (!restored)
            {
                throw new AssertionException(
                    "Message-contract delivery restoration failed; broker and worker restoration both attempted; details redacted."
                );
            }
        }
        bool sameWorker = identity == await InspectAsync(worker, "{{.Id}}", token);
        bool restarted = started != await InspectAsync(worker, "{{.State.StartedAt}}", token);
        bool running = await InspectAsync(worker, "{{.State.Running}}", token) == "true";
        bool brokerRestored = await InspectAsync(broker, "{{.State.Paused}}", token) == "false";
        if (!sameWorker || !restarted || !running || !brokerRestored)
        {
            throw new AssertionException(
                "Message-contract delivery recovery state mismatch; details redacted."
            );
        }
        return new(true, true, sameWorker, restarted, running, brokerRestored);

        async Task<string> InspectAsync(string container, string format, CancellationToken cancellation) =>
            (
                await docker.RunAsync(["inspect", "--format", format, container], cancellation)
            ).StandardOutput.Trim();
    }
}

internal sealed record MessageContractInterruptionEvidence(
    bool BrokerPauseObserved,
    bool WorkerStoppedBeforeBrokerRecovery,
    bool SameWorkerContainer,
    bool WorkerStartChanged,
    bool WorkerRunning,
    bool BrokerRestored
);
