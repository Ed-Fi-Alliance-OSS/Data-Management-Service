// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text;
using CoreCdc = EdFi.DataManagementService.Core.DocumentCache.Cdc;

namespace EdFi.DataManagementService.Backend.Cdc;

/// <summary>
/// Direct, uncached collection from the explicitly configured single worker. Host owns the client
/// and credentials; redirects must remain disabled. Qualified task metric replacement plus status
/// brackets establishes current attribution, not a fence against unobserved native recovery.
/// </summary>
public sealed class CdcConnectorTelemetryAdapter(
    HttpClient client,
    ICdcConnectTransport connect,
    ICdcWorkerInspectionTransport worker,
    TimeProvider clock
) : ICdcWorkerMetricsTransport
{
    internal const int MaximumResponseBytes = 4 * 1024 * 1024;

    public CdcConnectorTelemetryAdapter(
        HttpClient client,
        ICdcConnectTransport connect,
        ICdcWorkerInspectionTransport worker
    )
        : this(client, connect, worker, TimeProvider.System) { }

    public static HttpClient CreateHttpClient() =>
        new(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Sonar",
        "S1854",
        Justification = "The catch paths use the current component when scraping or parsing throws."
    )]
    public async Task<CdcTransportResult<CdcConnectorTelemetryObservation>> CollectAsync(
        CdcDeploymentRequest request,
        CdcTelemetryObservationPass pass,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(pass);
        cancellationToken.ThrowIfCancellationRequested();
        if (!pass.Claim(request))
        {
            return Failure(CdcDeploymentComponent.Metrics, CdcDeploymentFailure.InvalidInput);
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(request.Timing.WaitTimeout);
        var component = CdcDeploymentComponent.Worker;
        try
        {
            var before = await CallAsync(
                request,
                token => worker.InspectAsync(request, token),
                timeout.Token
            );
            var first = Evidence(before, component);
            Require(Matches(first, request));
            component = CdcDeploymentComponent.Connect;
            var beforeStatus = Evidence(
                await CallAsync(request, token => connect.ReadStatusAsync(request, token), timeout.Token),
                component
            );
            Require(Matches(beforeStatus, first, request));

            component = CdcDeploymentComponent.Metrics;
            // Include HTTP headers/body and all later identity checks in the observation's age.
            long started = clock.GetTimestamp();
            var startedAt = clock.GetUtcNow();
            string text = await CallAsync(request, token => ScrapeAsync(request, token), timeout.Token);
            var completedAt = clock.GetUtcNow();
            var (observation, statistics) = CdcConnectorTelemetryMetrics.Parse(text, pass, startedAt);

            component = CdcDeploymentComponent.Connect;
            var afterStatus = Evidence(
                await CallAsync(request, token => connect.ReadStatusAsync(request, token), timeout.Token),
                component
            );
            Require(Matches(afterStatus, first, request));
            component = CdcDeploymentComponent.Worker;
            var last = Evidence(
                await CallAsync(request, token => worker.InspectAsync(request, token), timeout.Token),
                component
            );
            Require(
                Matches(last, request)
                    && first.ProcessIdentity == last.ProcessIdentity
                    && first.ConnectWorkerId == last.ConnectWorkerId
                    && first.ImageDigest == last.ImageDigest
                    && first.HeapBytes == last.HeapBytes
            );
            timeout.Token.ThrowIfCancellationRequested();
            var receipt = new CdcConnectorTelemetryObservation(
                pass,
                clock,
                started,
                startedAt,
                completedAt,
                observation,
                statistics
            );
            if (receipt.ReadForEvaluation(pass).LagState == CoreCdc.CdcConnectorLagState.Unknown)
            {
                pass.Invalidate();
                return Failure(CdcDeploymentComponent.Metrics, CdcDeploymentFailure.Unavailable);
            }
            return new CdcTransportResult<CdcConnectorTelemetryObservation>.Observed(receipt);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            pass.Invalidate();
            return Failure(component, CdcDeploymentFailure.Timeout);
        }
        catch (TelemetryEvidenceException exception)
        {
            pass.Invalidate();
            return Failure(exception.Component, exception.FailureKind);
        }
        catch (InvalidDataException)
        {
            pass.Invalidate();
            return Failure(component, CdcDeploymentFailure.ValidationFailed);
        }
        catch (Exception exception)
        {
            pass.Invalidate();
            return new CdcTransportResult<CdcConnectorTelemetryObservation>.Unavailable(
                CdcDeploymentDiagnostic.FromException(component, exception)
            );
        }
    }

    private static async Task<T> CallAsync<T>(
        CdcDeploymentRequest request,
        Func<CancellationToken, Task<T>> call,
        CancellationToken token
    )
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(request.Timing.CallTimeout);
        return await call(timeout.Token).WaitAsync(timeout.Token);
    }

    private async Task<string> ScrapeAsync(CdcDeploymentRequest request, CancellationToken token)
    {
        using var message = new HttpRequestMessage(HttpMethod.Get, request.WorkerMetricsEndpoint);
        message.Headers.CacheControl = new() { NoCache = true, NoStore = true };
        message.Headers.Accept.ParseAdd("text/plain; version=0.0.4");
        using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, token);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new TelemetryEvidenceException(
                CdcDeploymentComponent.Metrics,
                CdcDeploymentFailure.AuthenticationFailed
            );
        }
        Require(
            response.StatusCode == HttpStatusCode.OK
                && response.Content.Headers.ContentType?.MediaType == "text/plain"
                && response.Content.Headers.ContentLength is not > MaximumResponseBytes
                && response.Headers.Age is not { Ticks: > 0 }
        );
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        using MemoryStream buffer = new();
        byte[] chunk = new byte[8192];
        while (true)
        {
            int count = await stream.ReadAsync(chunk, token);
            if (count == 0)
            {
                break;
            }
            Require(buffer.Length + count <= MaximumResponseBytes);
            await buffer.WriteAsync(chunk.AsMemory(0, count), token);
        }
        return new UTF8Encoding(false, true).GetString(buffer.GetBuffer(), 0, checked((int)buffer.Length));
    }

    private static T Evidence<T>(CdcTransportResult<T> result, CdcDeploymentComponent component)
        where T : notnull =>
        result switch
        {
            CdcTransportResult<T>.Observed observed => observed.Value,
            CdcTransportResult<T>.Unavailable unavailable => throw new TelemetryEvidenceException(
                component,
                unavailable.Diagnostic.Failure
            ),
            _ => throw new TelemetryEvidenceException(component, CdcDeploymentFailure.Unavailable),
        };

    private static bool Matches(CdcWorkerInspection evidence, CdcDeploymentRequest request) =>
        !string.IsNullOrWhiteSpace(evidence.ProcessIdentity)
        && !string.IsNullOrWhiteSpace(evidence.ConnectWorkerId)
        && evidence.MetricsEndpoint == request.WorkerMetricsEndpoint
        && CdcQualifiedWorkerImage.Digests.Contains(evidence.ImageDigest)
        && evidence.ImageDigest == request.WorkerPolicy.QualifiedImageDigest
        && evidence.HeapBytes == request.WorkerPolicy.HeapBytes
        && evidence.EffectiveConfiguration.TryGetValue("group.id", out var group)
        && group == request.WorkerPolicy.WorkerKey.Value
        && evidence.EffectiveConfiguration.TryGetValue("offset.storage.topic", out var topic)
        && topic == request.WorkerPolicy.OffsetStorageTopic.Value;

    private static bool Matches(
        CdcConnectStatus evidence,
        CdcWorkerInspection worker,
        CdcDeploymentRequest request
    ) =>
        evidence.IsRunning
        && evidence.WorkerId == worker.ConnectWorkerId
        && evidence.Tasks[0].WorkerId == worker.ConnectWorkerId
        && evidence.Runtime.ContractVersion == CoreCdc.CdcJsonContract.CurrentContractVersion
        && evidence.Runtime.ConnectorName == request.Binding.ConnectorName
        && evidence.Runtime.TargetIdentity == request.TargetIdentity
        && evidence.Runtime.Provider == request.Binding.Provider
        && evidence.Runtime.PhysicalSourceFingerprint == request.Binding.PhysicalSourceFingerprint
        && evidence.Runtime.TaskCount == 1
        && evidence.Runtime.RunningTaskCount == 1
        && evidence.Runtime.SoleTaskState == CoreCdc.CdcConnectorRuntimeState.Running;

    private static void Require(bool condition)
    {
        if (!condition)
        {
            throw new InvalidDataException();
        }
    }

    private static CdcTransportResult<CdcConnectorTelemetryObservation> Failure(
        CdcDeploymentComponent component,
        CdcDeploymentFailure failure
    ) => new CdcTransportResult<CdcConnectorTelemetryObservation>.Unavailable(new(component, failure));

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Sonar",
        "S3871",
        Justification = "Private control flow caught inside this adapter; never escapes its API."
    )]
    private sealed class TelemetryEvidenceException(
        CdcDeploymentComponent component,
        CdcDeploymentFailure failure
    ) : Exception
    {
        internal CdcDeploymentComponent Component { get; } = component;
        internal CdcDeploymentFailure FailureKind { get; } = failure;
    }
}
