// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Ddl;
using EdFi.DataManagementService.Core.DocumentCache.Cdc;

namespace EdFi.DataManagementService.Backend.Cdc;

/// <summary>
/// Fixed initial-enable sequence. The journal selects the next safe entry boundary; each existing
/// stage reconciles current evidence under its controller lock. No checkpoint supplies readiness.
/// </summary>
public sealed class CdcInitialEnableWorkflow(
    string stateRoot,
    ICdcProviderSetupService provider,
    ICdcConnectorTemplateService templates,
    ICdcKafkaAdminAdapter kafka,
    ICdcConnectTransport connect,
    ICdcWorkerInspectionTransport worker,
    ICdcWorkerStartupTransport infrastructure,
    ICdcWorkerMetricsTransport metrics,
    ICdcProviderSourcePositionAdapter positions
)
{
    internal ICdcBindingLifecycleService Bindings { get; init; } =
        new CdcBindingLifecycleService(new LocalCdcBindingStateStore(stateRoot), TimeProvider.System);

    public async Task<CdcTransportResult<CdcWriterPublicationResult>> EnableAsync(
        CdcDeploymentRequest request,
        ICdcProjectionRuntime runtime,
        long lagThresholdMilliseconds,
        CancellationToken cancellationToken = default,
        CancellationToken operationDeadline = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(runtime);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            operationDeadline
        );
        timeout.CancelAfter(request.Timing.WaitTimeout);
        var token = timeout.Token;
        var readiness = new CdcInitialReadiness(
            new(stateRoot),
            Bindings,
            provider,
            templates,
            kafka,
            connect,
            worker,
            metrics,
            positions,
            TimeProvider.System
        );
        bool established = false;
        try
        {
            CdcWorkflowJournal journal;
            await using (
                var session = await new LocalCdcWorkflowJournalStore(stateRoot).AcquireAsync(
                    request.Timing.CallTimeout,
                    request.Timing.PollInterval < request.Timing.CallTimeout
                        ? request.Timing.PollInterval
                        : request.Timing.CallTimeout,
                    token
                )
            )
            {
                journal = await session.ReadAsync(request.TargetIdentity, token);
                CdcWorkflowJournalValidation.Require(
                    journal.Purpose == CdcWorkflowPurpose.InitialCdcProvisioning
                        && journal.Operations.All(o =>
                            o.Effect
                                is CdcWorkflowEffect.CreateDatabase
                                    or CdcWorkflowEffect.AssociateSource
                                    or CdcWorkflowEffect.ReserveBinding
                                    or CdcWorkflowEffect.ActivateProjection
                                    or CdcWorkflowEffect.CreateProvider
                                    or CdcWorkflowEffect.PrepareKafka
                                    or CdcWorkflowEffect.RegisterConnector
                                    or CdcWorkflowEffect.EstablishConnector
                        ),
                    CdcWorkflowStateFailure.Contradictory
                );
                established = journal.Operations.Any(o =>
                    o.Effect == CdcWorkflowEffect.EstablishConnector && o.Completions.Length == 1
                );
                if (established)
                {
                    // Retry consumes fresh terminal evidence under this session before setup guards
                    // can discard it. Initial creation/offset establishment still use the original stages.
                    await readiness.InspectProviderAsync(
                        request,
                        runtime,
                        session,
                        Guid.NewGuid().ToString("D"),
                        _ => { },
                        token,
                        cancellationToken
                    );
                }
            }

            // Provider setup requires completed activation and performs fresh exact-binding, source,
            // history and lifecycle checks. Never send a later workflow back to initial activation.
            if (!journal.Operations.Any(o => o.Effect == CdcWorkflowEffect.CreateProvider))
            {
                Require(await new CdcInitialEnablement(stateRoot).ActivateAsync(request, runtime, token));
            }
            if (!established)
            {
                Require(
                    await new CdcProviderSetupOrchestration(stateRoot, provider, templates).SetupAsync(
                        request,
                        runtime,
                        token
                    )
                );
            }

            // Registration intent means consumption may have begun. The registration controller
            // validates retained topics/offsets and the original payload; no infrastructure repair
            // or worker relaunch is authorized by initial retry after that boundary.
            if (!journal.Operations.Any(o => o.Effect == CdcWorkflowEffect.RegisterConnector))
            {
                var provisioning = new CdcKafkaProvisioning(
                    stateRoot,
                    kafka,
                    runtime,
                    new CdcKafkaProducerInspection(connect, worker)
                );
                Require(await new CdcWorkerStartup(provisioning, infrastructure).StartAsync(request, token));
                Require(await provisioning.ProvisionBindingAsync(request, token));
            }
            if (!established)
            {
                // A completed establishment is revalidated by readiness. Re-entering registration
                // would put another provider-success guard ahead of its continuity observation.
                Require(
                    await new CdcConnectorRegistration(
                        stateRoot,
                        provider,
                        templates,
                        kafka,
                        connect,
                        worker
                    ).RegisterAsync(request, runtime, token)
                );
            }
            return await readiness.PreparePublicationAsync(
                request,
                runtime,
                lagThresholdMilliseconds,
                cancellationToken,
                token
            );
        }
        catch (CdcInitialReadiness.EvidenceException exception)
        {
            return new CdcTransportResult<CdcWriterPublicationResult>.Unavailable(exception.Diagnostic);
        }
        catch (EvidenceException exception)
        {
            return new CdcTransportResult<CdcWriterPublicationResult>.Unavailable(exception.Diagnostics[0]);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failure(CdcDeploymentFailure.Timeout);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (CdcWorkflowStateException exception)
        {
            return Failure(
                exception.Failure switch
                {
                    CdcWorkflowStateFailure.LockTimeout => CdcDeploymentFailure.Timeout,
                    CdcWorkflowStateFailure.Missing or CdcWorkflowStateFailure.Unavailable =>
                        CdcDeploymentFailure.Unavailable,
                    _ => CdcDeploymentFailure.ValidationFailed,
                }
            );
        }
        catch (Exception exception)
        {
            return new CdcTransportResult<CdcWriterPublicationResult>.Unavailable(
                CdcDeploymentDiagnostic.FromException(CdcDeploymentComponent.WorkflowState, exception)
            );
        }
    }

    private static void Require<T>(CdcTransportResult<T> result)
        where T : notnull
    {
        if (result is not CdcTransportResult<T>.Observed)
        {
            throw new EvidenceException(
                result.Diagnostics.Count > 0
                    ? result.Diagnostics
                    : [new(CdcDeploymentComponent.WorkflowState, CdcDeploymentFailure.ValidationFailed)]
            );
        }
    }

    private static CdcTransportResult<CdcWriterPublicationResult> Failure(CdcDeploymentFailure failure) =>
        new CdcTransportResult<CdcWriterPublicationResult>.Unavailable(
            new(CdcDeploymentComponent.WorkflowState, failure)
        );

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Sonar",
        "S3871",
        Justification = "Private control flow caught inside this controller; never escapes its API."
    )]
    private sealed class EvidenceException(IReadOnlyList<CdcDeploymentDiagnostic> diagnostics) : Exception
    {
        public IReadOnlyList<CdcDeploymentDiagnostic> Diagnostics { get; } = diagnostics;
    }
}
