// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using EdFi.DataManagementService.Core.DocumentCache.Cdc;

namespace EdFi.DataManagementService.Backend.Cdc;

/// <summary>
/// Infrastructure startup only. The owner must perform retained-connector shutdown/provenance
/// checks before restarting an established worker; this interface never resumes a connector.
/// Broker startup (including UI) cannot start Connect through Compose dependencies or profiles.
/// </summary>
public interface ICdcWorkerStartupTransport
{
    Task StartBrokerAsync(CdcDeploymentRequest request, CancellationToken cancellationToken);
    Task StartWorkerAsync(CdcDeploymentRequest request, CancellationToken cancellationToken);
}

/// <summary>One fresh offset-store preparation and policy gate precedes each worker launch.</summary>
public sealed class CdcWorkerStartup(
    ICdcKafkaAdministrationTransport kafka,
    ICdcWorkerStartupTransport infrastructure
)
{
    public async Task<CdcTransportResult<CdcTransportAcknowledgement>> StartAsync(
        CdcDeploymentRequest request,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(request.Timing.WaitTimeout);
        try
        {
            await infrastructure.StartBrokerAsync(request, timeout.Token);
            DateTimeOffset started = DateTimeOffset.UtcNow;
            var result = await kafka.ProvisionOffsetStoreAsync(request, timeout.Token);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            if (result is not CdcTransportResult<CdcConnectOffsetStorePolicyObservation>.Observed observed)
            {
                return Reject();
            }
            var value = observed.Value;
            var validation = CdcConnectOffsetStorePolicyObservationValidator.Validate(
                value,
                new(value.OperationId, request.TargetIdentity, request.Binding.PhysicalSourceFingerprint, now)
            );
            if (
                !validation.Succeeded
                || value.PolicyState != CdcConnectOffsetStorePolicyState.Satisfied
                || value.WorkerKey != request.WorkerPolicy.WorkerKey.Value
                || value.OffsetStorageTopic != request.WorkerPolicy.OffsetStorageTopic.Value
                || value.ObservedAt < started
                || value.ObservedAt > now
                || now - value.ObservedAt > request.Timing.MaximumObservationAge
            )
            {
                return Reject();
            }
            timeout.Token.ThrowIfCancellationRequested();
            await infrastructure.StartWorkerAsync(request, timeout.Token);
            return new CdcTransportResult<CdcTransportAcknowledgement>.Observed(new());
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new CdcTransportResult<CdcTransportAcknowledgement>.Unavailable(
                new(CdcDeploymentComponent.Worker, CdcDeploymentFailure.Timeout)
            );
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new CdcTransportResult<CdcTransportAcknowledgement>.Unavailable(
                CdcDeploymentDiagnostic.FromException(CdcDeploymentComponent.Worker, exception)
            );
        }
    }

    private static CdcTransportResult<CdcTransportAcknowledgement> Reject() =>
        new CdcTransportResult<CdcTransportAcknowledgement>.Unavailable(
            new(CdcDeploymentComponent.Worker, CdcDeploymentFailure.ValidationFailed)
        );
}

/// <summary>Explicit Compose service effects used by CdcWorkerStartup; no profile-wide up or connector REST calls.</summary>
public sealed class CdcComposeWorkerStartupTransport : ICdcWorkerStartupTransport
{
    private readonly string[] _arguments;
    private readonly ICdcWorkerDockerCommand _docker;
    private readonly IReadOnlySet<string> _qualifiedImages;

    public CdcComposeWorkerStartupTransport(string composeFile, string environmentFile, string project)
        : this(composeFile, environmentFile, project, CdcQualifiedWorkerImage.Images) { }

    public CdcComposeWorkerStartupTransport(
        string composeFile,
        string environmentFile,
        string project,
        IReadOnlySet<string> qualifiedImages
    )
        : this(composeFile, environmentFile, project, qualifiedImages, new CdcWorkerDockerCommand()) { }

    internal CdcComposeWorkerStartupTransport(
        string composeFile,
        string environmentFile,
        string project,
        IReadOnlySet<string> qualifiedImages,
        ICdcWorkerDockerCommand docker
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(composeFile);
        ArgumentException.ThrowIfNullOrWhiteSpace(environmentFile);
        ArgumentException.ThrowIfNullOrWhiteSpace(project);
        _arguments =
        [
            "compose",
            "-f",
            Path.GetFullPath(composeFile),
            "--env-file",
            Path.GetFullPath(environmentFile),
            "-p",
            project,
        ];
        _docker = docker;
        _qualifiedImages = qualifiedImages.ToHashSet(StringComparer.Ordinal);
    }

    public async Task StartBrokerAsync(CdcDeploymentRequest request, CancellationToken cancellationToken)
    {
        await ValidateComposeAsync(request, cancellationToken);
        await _docker.RunAsync(
            [.. _arguments, "up", "--detach", "--wait", "--wait-timeout", "180", "kafka"],
            cancellationToken
        );
    }

    public async Task StartWorkerAsync(CdcDeploymentRequest request, CancellationToken cancellationToken)
    {
        await ValidateComposeAsync(request, cancellationToken);
        await _docker.RunAsync(
            [
                .. _arguments,
                "up",
                "--detach",
                "--no-deps",
                "--wait",
                "--wait-timeout",
                "180",
                "kafka-cdc-worker",
            ],
            cancellationToken
        );
    }

    private async Task ValidateComposeAsync(CdcDeploymentRequest request, CancellationToken token)
    {
        string output = await _docker.RunAsync(
            [.. _arguments, "--profile", "cdc-managed-worker", "config", "--format", "json"],
            token
        );
        using var document = JsonDocument.Parse(output);
        JsonElement services = document.RootElement.GetProperty("services");
        JsonElement worker = services.GetProperty("kafka-cdc-worker");
        string image = worker.GetProperty("image").GetString()!;
        if (
            !image.Contains("@sha256:", StringComparison.Ordinal)
            || !_qualifiedImages.Contains(image)
            || !image.EndsWith("@" + request.WorkerPolicy.QualifiedImageDigest, StringComparison.Ordinal)
            || worker.GetProperty("environment").GetProperty("OFFSET_STORAGE_TOPIC").GetString()
                != request.WorkerPolicy.OffsetStorageTopic.Value
            || worker.GetProperty("environment").GetProperty("GROUP_ID").GetString()
                != request.WorkerPolicy.WorkerKey.Value
            || worker
                .GetProperty("environment")
                .GetProperty("CONNECT_CONNECTOR_CLIENT_CONFIG_OVERRIDE_POLICY")
                .GetString() != "All"
            || services.TryGetProperty("kafka-postgresql-source", out _)
            || services.GetProperty("kafka").TryGetProperty("depends_on", out _)
            || worker.TryGetProperty("depends_on", out _)
            || worker.GetProperty("profiles").GetArrayLength() != 1
            || worker.GetProperty("profiles")[0].GetString() != "cdc-managed-worker"
        )
        {
            throw new InvalidDataException("Compose does not select an isolated qualified CDC worker.");
        }
    }
}
