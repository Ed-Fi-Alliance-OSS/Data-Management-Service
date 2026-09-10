// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using EdFi.DataManagementService.Core.DocumentCache.Cdc;

namespace EdFi.DataManagementService.Backend.Cdc;

/// <summary>
/// Explicit infrastructure effects; CdcWorkerStartup owns authorization under its controller session.
/// This interface never resumes a connector.
/// Broker startup (including UI) cannot start Connect through Compose dependencies or profiles.
/// </summary>
public interface ICdcWorkerStartupTransport
{
    Task StartBrokerAsync(CdcDeploymentRequest request, CancellationToken cancellationToken);
    Task StartWorkerAsync(CdcDeploymentRequest request, CancellationToken cancellationToken);
}

/// <summary>One fresh offset-store preparation and policy gate precedes each worker launch.</summary>
public sealed class CdcWorkerStartup(CdcKafkaProvisioning kafka, ICdcWorkerStartupTransport infrastructure)
{
    public async Task<CdcTransportResult<CdcTransportAcknowledgement>> StartAsync(
        CdcDeploymentRequest request,
        CancellationToken cancellationToken
    ) => await StartAsync(request, createMissingOffsetStore: true, cancellationToken);

    /// <summary>Retained deployment startup inspects shared offset storage without repairing it.</summary>
    public Task<CdcTransportResult<CdcTransportAcknowledgement>> StartRetainedAsync(
        CdcDeploymentRequest request,
        CancellationToken cancellationToken
    ) => StartAsync(request, createMissingOffsetStore: false, cancellationToken);

    private async Task<CdcTransportResult<CdcTransportAcknowledgement>> StartAsync(
        CdcDeploymentRequest request,
        bool createMissingOffsetStore,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(request.Timing.WaitTimeout);
        try
        {
            await using var session = await kafka.Store.AcquireAsync(
                request.Timing.CallTimeout,
                request.Timing.PollInterval < request.Timing.CallTimeout
                    ? request.Timing.PollInterval
                    : request.Timing.CallTimeout,
                timeout.Token
            );
            if (createMissingOffsetStore)
            {
                await kafka.RequireInitialStartupAsync(request, session, timeout.Token);
            }
            else
            {
                await RequireManagedShutdownAsync(session, request, timeout.Token);
            }
            return await StartInSessionAsync(request, session, createMissingOffsetStore, timeout.Token);
        }
        catch (CdcWorkflowStateException exception)
        {
            return new CdcTransportResult<CdcTransportAcknowledgement>.Unavailable(
                new(
                    CdcDeploymentComponent.WorkflowState,
                    exception.Failure == CdcWorkflowStateFailure.LockTimeout
                        ? CdcDeploymentFailure.Timeout
                        : CdcDeploymentFailure.ValidationFailed
                )
            );
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

    /// <summary>The caller retains the session from its original startup authorization through both effects.</summary>
    internal async Task<CdcTransportResult<CdcTransportAcknowledgement>> StartInSessionAsync(
        CdcDeploymentRequest request,
        LocalCdcWorkflowJournalStore.Session session,
        bool createMissingOffsetStore,
        CancellationToken token
    )
    {
        await infrastructure.StartBrokerAsync(request, token);
        DateTimeOffset started = DateTimeOffset.UtcNow;
        var result = await kafka.OffsetStoreInSessionAsync(request, session, createMissingOffsetStore, token);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (result is not CdcTransportResult<CdcConnectOffsetStorePolicyObservation>.Observed observed)
        {
            return Reject();
        }
        if (!ValidOffsetStore(request, observed.Value, started, now))
        {
            return Reject();
        }
        token.ThrowIfCancellationRequested();
        await infrastructure.StartWorkerAsync(request, token);
        return new CdcTransportResult<CdcTransportAcknowledgement>.Observed(new());
    }

    internal static bool ValidOffsetStore(
        CdcDeploymentRequest request,
        CdcConnectOffsetStorePolicyObservation value,
        DateTimeOffset started,
        DateTimeOffset now
    )
    {
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
            return false;
        }
        return true;
    }

    internal static async Task RequireManagedShutdownAsync(
        LocalCdcWorkflowJournalStore.Session session,
        CdcDeploymentRequest request,
        CancellationToken token
    )
    {
        var journal = await session.ReadAsync(request.TargetIdentity, token);
        var latest = journal.Operations.Last();
        CdcWorkflowJournalValidation.Require(
            latest.Effect == CdcWorkflowEffect.StopConnector
                && latest.Completions is [{ Evidence: CdcWorkflowCompletion.Shutdown }],
            CdcWorkflowStateFailure.Contradictory
        );
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
    private readonly string _brokerSizeOverrideFile;
    private string[] ComposeArguments =>
        string.IsNullOrEmpty(_brokerSizeOverrideFile) || !File.Exists(_brokerSizeOverrideFile)
            ? _arguments
            : [.. _arguments, "-f", _brokerSizeOverrideFile];
    private readonly ICdcWorkerDockerCommand _docker;
    private readonly IReadOnlySet<string> _qualifiedImages;

    public CdcComposeWorkerStartupTransport(
        string composeFile,
        string environmentFile,
        string project,
        string brokerSizeOverrideFile = ""
    )
        : this(composeFile, environmentFile, project, CdcQualifiedWorkerImage.Images, brokerSizeOverrideFile)
    { }

    public CdcComposeWorkerStartupTransport(
        string composeFile,
        string environmentFile,
        string project,
        IReadOnlySet<string> qualifiedImages,
        string brokerSizeOverrideFile = ""
    )
        : this(
            composeFile,
            environmentFile,
            project,
            qualifiedImages,
            new CdcWorkerDockerCommand(),
            brokerSizeOverrideFile
        )
    {
        ArgumentNullException.ThrowIfNull(qualifiedImages);
    }

    internal CdcComposeWorkerStartupTransport(
        string composeFile,
        string environmentFile,
        string project,
        IReadOnlySet<string> qualifiedImages,
        ICdcWorkerDockerCommand docker,
        string brokerSizeOverrideFile = ""
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
        _brokerSizeOverrideFile = string.IsNullOrEmpty(brokerSizeOverrideFile)
            ? ""
            : Path.GetFullPath(brokerSizeOverrideFile);
        _qualifiedImages = qualifiedImages.ToHashSet(StringComparer.Ordinal);
    }

    public async Task StartBrokerAsync(CdcDeploymentRequest request, CancellationToken cancellationToken)
    {
        await ValidateComposeAsync(request, cancellationToken);
        await _docker.RunAsync(
            [.. ComposeArguments, "up", "--detach", "--wait", "--wait-timeout", "180", "kafka"],
            cancellationToken
        );
    }

    public async Task StartWorkerAsync(CdcDeploymentRequest request, CancellationToken cancellationToken)
    {
        await ValidateComposeAsync(request, cancellationToken);
        await _docker.RunAsync(
            [
                .. ComposeArguments,
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
            [.. ComposeArguments, "--profile", "cdc-managed-worker", "config", "--format", "json"],
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
            || worker.GetProperty("environment").GetProperty("BOOTSTRAP_SERVERS").GetString()
                != request.ConnectorPolicy.KafkaBootstrapServers
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
