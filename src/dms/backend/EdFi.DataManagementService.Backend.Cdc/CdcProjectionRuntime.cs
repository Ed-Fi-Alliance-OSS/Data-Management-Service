// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.DocumentCacheRuntime;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace EdFi.DataManagementService.Backend.Cdc;

/// <summary>
/// One offline controller invocation owns this runtime. Initialization does not activate tracking or
/// start processing. The controller reserves its binding before calling guarded activation and explicitly
/// starts processing for readiness. Dispose before handing admission to writers, including on failure.
/// </summary>
public interface ICdcProjectionRuntime : IAsyncDisposable
{
    Task<DocumentCacheAdministrativeCommandResult> ActivateAsync(
        DocumentCacheGuardedNewEmptyActivationRequest request,
        CancellationToken cancellationToken
    );
    Task StartProcessingAsync(CancellationToken cancellationToken);
    Task<DocumentCacheStatusResponse> ObserveAsync(CancellationToken cancellationToken);
}

public static class CdcProjectionRuntimeFactory
{
    public static async Task<CdcTransportResult<ICdcProjectionRuntime>> CreateAsync(
        IConfiguration configuration,
        ILogger logger,
        DocumentCacheTargetKey targetKey,
        CancellationToken cancellationToken
    )
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            ServiceCollection services = new();
            services.AddLogging();
            services.AddDocumentCacheRuntimeServices(
                configuration,
                logger,
                targetKey,
                DocumentCacheRuntimeTargetSelection.RequireConfiguredMembership
            );
            services.AddCdcDownstreamPublicationHistory(configuration);
            ServiceProvider provider = services.BuildServiceProvider();
            return await OpenAsync(provider, targetKey, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            return new CdcTransportResult<ICdcProjectionRuntime>.Unavailable(
                CdcDeploymentDiagnostic.FromException(CdcDeploymentComponent.Projection, exception)
            );
        }
    }

    internal static async Task<CdcTransportResult<ICdcProjectionRuntime>> OpenAsync(
        ServiceProvider provider,
        DocumentCacheTargetKey targetKey,
        CancellationToken cancellationToken
    )
    {
        try
        {
            await DocumentCacheRuntimeInitializer
                .InitializeAsync(provider, cancellationToken)
                .ConfigureAwait(false);
            IDocumentCacheTargetRegistry registry =
                provider.GetRequiredService<IDocumentCacheTargetRegistry>();
            DocumentCacheTargetRegistrySnapshot snapshot = await DocumentCacheRuntimeTargetResolver
                .ResolveAsync(registry, targetKey, cancellationToken)
                .ConfigureAwait(false);
            if (
                !DocumentCacheRuntimeTargetResolver.ContainsOnlyTarget(snapshot, targetKey)
                || snapshot.Targets[0].ResolutionState != DocumentCacheTargetResolutionState.Resolved
                || registry.CurrentRuntimeSnapshot.GetExecutionContext(targetKey) is null
            )
            {
                await provider.DisposeAsync().ConfigureAwait(false);
                return new CdcTransportResult<ICdcProjectionRuntime>.Unavailable(
                    new(CdcDeploymentComponent.Projection, CdcDeploymentFailure.ValidationFailed)
                );
            }
            return new CdcTransportResult<ICdcProjectionRuntime>.Observed(
                new CdcProjectionRuntime(
                    provider,
                    targetKey,
                    provider.GetRequiredService<DocumentCacheProjectionSupervisor>()
                )
            );
        }
        catch (Exception exception)
        {
            await provider.DisposeAsync().ConfigureAwait(false);
            return new CdcTransportResult<ICdcProjectionRuntime>.Unavailable(
                CdcDeploymentDiagnostic.FromException(CdcDeploymentComponent.Projection, exception)
            );
        }
    }
}

/// <summary>Single caller owns operations and awaits disposal; the E18 supervisor owns target scopes.</summary>
internal sealed class CdcProjectionRuntime(
    ServiceProvider provider,
    DocumentCacheTargetKey targetKey,
    BackgroundService supervisor
) : ICdcProjectionRuntime
{
    private bool _started;
    private bool _disposed;

    public Task<DocumentCacheAdministrativeCommandResult> ActivateAsync(
        DocumentCacheGuardedNewEmptyActivationRequest request,
        CancellationToken cancellationToken
    )
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (!targetKey.Equals(request.TargetKey.TargetKey))
        {
            throw new ArgumentException(
                "Activation request must identify the selected projection target.",
                nameof(request)
            );
        }
        return provider
            .GetRequiredService<IDocumentCacheGuardedNewEmptyActivationCommand>()
            .ExecuteAsync(request, cancellationToken);
    }

    public async Task StartProcessingAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (_started)
        {
            throw new InvalidOperationException("Projection processing has already started.");
        }
        try
        {
            // Prepare the selected context synchronously so initialization failures reach the caller.
            await provider
                .GetRequiredService<IDocumentCacheProjectionSupervisor>()
                .RefreshAsync(DocumentCacheTargetRefreshReason.Startup, cancellationToken)
                .ConfigureAwait(false);
            _started = true;
            await supervisor.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public Task<DocumentCacheStatusResponse> ObserveAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        // A non-HTTP owner must observe background faults; there is no Host to report them.
        if (supervisor.ExecuteTask is { IsFaulted: true } execution)
        {
            execution.GetAwaiter().GetResult();
        }
        return provider
            .GetRequiredService<IDocumentCacheStatusService>()
            .GetStatusAsync(cancellationToken, DocumentCacheStatusEvaluationMode.StandaloneDirectObservation);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        try
        {
            // Stop also releases contexts created by administrative commands before processing starts.
            await supervisor.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            await provider.DisposeAsync().ConfigureAwait(false);
        }
    }
}
