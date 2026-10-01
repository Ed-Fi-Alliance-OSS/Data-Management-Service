// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace EdFi.DataManagementService.Tests.E2E.Cdc;

internal sealed record CdcRestartWorkItem(long DocumentId, long RequiredContentVersion);

internal sealed record CdcRestartPage(int PageSize, IReadOnlyList<CdcRestartWorkItem> Items);

internal sealed record CdcRestartSnapshot(
    IReadOnlyList<CdcRestartPage> Pages,
    int DroppedPages,
    int BaselineBoundaries,
    int BaselinePages,
    int InventoryPages
);

/// <summary>Installed before replacement initialization; observes real provider operations for only
/// that runtime. Holds the second nonempty work page before processing, after the first page drained.
/// Baseline/scrub calls are counted at entry and always delegated, including failed invocations.</summary>
internal sealed class CdcRestartObservations : IAsyncDisposable
{
    private readonly Lock _sync = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly TaskCompletionSource _held = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<CdcRestartPage> _pages = [];
    private readonly TimeSpan _timeout;
    private TaskCompletionSource _idle = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _active;
    private bool _disposed;
    private bool _holding;
    private Task _disposal = Task.CompletedTask;
    private int _dropped;
    private int _boundaries;
    private int _baseline;
    private int _inventory;

    public CdcRestartObservations(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(5))
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }
        _timeout = timeout;
    }

    public void ConfigureServices(IServiceCollection services)
    {
        services.PostConfigure<DocumentCacheOptions>(options => options.Projector.PageSize = 2);
        var pagerType =
            services.Last(d => d.ServiceType == typeof(IDocumentProjectionWorkPager)).ImplementationType
            ?? throw new InvalidOperationException("Expected the provider work-pager registration.");
        services.AddSingleton(pagerType);
        services.Replace(
            ServiceDescriptor.Singleton<IDocumentProjectionWorkPager>(provider => new Pager(
                (IDocumentProjectionWorkPager)provider.GetRequiredService(pagerType),
                this
            ))
        );
        var primitivesFactory =
            services
                .Last(d => d.ServiceType == typeof(IDocumentCacheAdministrativePrimitives))
                .ImplementationFactory
            ?? throw new InvalidOperationException(
                "Expected the provider administrative-primitives factory."
            );
        services.Replace(
            ServiceDescriptor.Singleton<IDocumentCacheAdministrativePrimitives>(provider => new Primitives(
                (IDocumentCacheAdministrativePrimitives)primitivesFactory(provider),
                this
            ))
        );
    }

    public Task WaitUntilHeldAsync(CancellationToken token) => _held.Task.WaitAsync(_timeout, token);

    public void Release() => _release.TrySetResult();

    public bool IsHeld
    {
        get
        {
            lock (_sync)
            {
                return _holding;
            }
        }
    }

    public CdcRestartSnapshot Snapshot()
    {
        lock (_sync)
        {
            return new(_pages.ToArray(), _dropped, _boundaries, _baseline, _inventory);
        }
    }

    private async Task<DocumentProjectionWorkPage> ReadAsync(
        IDocumentProjectionWorkPager inner,
        DocumentProjectionWorkPageRequest request,
        CancellationToken token
    )
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_active++ == 0)
            {
                _idle = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }
        bool hold = false;
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token, _shutdown.Token);
            deadline.CancelAfter(_timeout);
            var page = await inner.ReadPageAsync(request, deadline.Token);
            lock (_sync)
            {
                if (!page.IsEmpty)
                {
                    if (_pages.Count < 64)
                    {
                        _pages.Add(
                            new(
                                page.PageSize,
                                page.Items.Select(item => new CdcRestartWorkItem(
                                        item.DocumentId,
                                        item.RequiredContentVersion
                                    ))
                                    .ToArray()
                            )
                        );
                        hold = _pages.Count == 2;
                        if (hold)
                        {
                            _holding = true;
                        }
                    }
                    else
                    {
                        _dropped++;
                    }
                }
            }
            if (hold)
            {
                _held.TrySetResult();
                await _release.Task.WaitAsync(deadline.Token);
            }
            return page;
        }
        finally
        {
            lock (_sync)
            {
                if (hold)
                {
                    _holding = false;
                }
                if (--_active == 0)
                {
                    _idle.TrySetResult();
                }
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            if (!_disposed)
            {
                _disposed = true;
                _held.TrySetCanceled();
                _disposal = StopAsync(_active == 0 ? Task.CompletedTask : _idle.Task);
            }
            return new(_disposal);
        }
    }

    private async Task StopAsync(Task idle)
    {
        await _shutdown.CancelAsync();
        await idle.WaitAsync(_timeout);
        _shutdown.Dispose();
    }

    private sealed class Pager(IDocumentProjectionWorkPager inner, CdcRestartObservations owner)
        : IDocumentProjectionWorkPager
    {
        public RelationalProviderToken ProviderToken => inner.ProviderToken;

        public Task<DocumentProjectionWorkPage> ReadPageAsync(
            DocumentProjectionWorkPageRequest request,
            CancellationToken cancellationToken = default
        ) => owner.ReadAsync(inner, request, cancellationToken);
    }

    private sealed class Primitives(
        IDocumentCacheAdministrativePrimitives inner,
        CdcRestartObservations owner
    ) : IDocumentCacheAdministrativePrimitives
    {
        public RelationalProviderToken ProviderToken => inner.ProviderToken;

        public Task<DocumentCacheLifecycleReadResult> ReadLifecycleAsync(
            IRelationalWriteSession mutexSession,
            DocumentCacheAdministrativeStateLockMode lockMode =
                DocumentCacheAdministrativeStateLockMode.Shared,
            CancellationToken cancellationToken = default
        )
        {
            return inner.ReadLifecycleAsync(mutexSession, lockMode, cancellationToken);
        }

        public Task LockCanonicalDocumentsForGuardedActivationAsync(
            IRelationalWriteSession mutexSession,
            CancellationToken cancellationToken = default
        )
        {
            return inner.LockCanonicalDocumentsForGuardedActivationAsync(mutexSession, cancellationToken);
        }

        public Task<DocumentCacheGuardedNewEmptyActivationState> ReadGuardedNewEmptyActivationStateAsync(
            IRelationalWriteSession mutexSession,
            CancellationToken cancellationToken = default
        )
        {
            return inner.ReadGuardedNewEmptyActivationStateAsync(mutexSession, cancellationToken);
        }

        public Task<DocumentCacheProviderPrerequisiteValidationResult> ValidateActivationPrerequisitesAsync(
            IRelationalWriteSession mutexSession,
            CancellationToken cancellationToken = default
        )
        {
            return inner.ValidateActivationPrerequisitesAsync(mutexSession, cancellationToken);
        }

        public Task<DocumentCacheAdministrativeLifecycleTransitionResult> TryTransitionLifecycleAsync(
            IRelationalWriteSession mutexSession,
            DocumentCacheAdministrativeLifecycleTransitionRequest request,
            CancellationToken cancellationToken = default
        )
        {
            return inner.TryTransitionLifecycleAsync(mutexSession, request, cancellationToken);
        }

        public Task<DocumentCacheAdministrativeClearBatchResult> ClearDocumentCacheBatchAsync(
            IRelationalWriteSession mutexSession,
            DocumentCacheAdministrativeClearBatchRequest request,
            CancellationToken cancellationToken = default
        )
        {
            return inner.ClearDocumentCacheBatchAsync(mutexSession, request, cancellationToken);
        }

        public Task<DocumentCacheAdministrativeClearBatchResult> ClearDocumentProjectionWorkBatchAsync(
            IRelationalWriteSession mutexSession,
            DocumentCacheAdministrativeClearBatchRequest request,
            DocumentCacheAdministrativeWorkClearance clearance,
            CancellationToken cancellationToken = default
        )
        {
            return inner.ClearDocumentProjectionWorkBatchAsync(
                mutexSession,
                request,
                clearance,
                cancellationToken
            );
        }

        public Task<DocumentCacheAdministrativeProjectedStateEmptinessResult> ReadProjectedStateEmptinessAsync(
            IRelationalWriteSession mutexSession,
            CancellationToken cancellationToken = default
        )
        {
            return inner.ReadProjectedStateEmptinessAsync(mutexSession, cancellationToken);
        }

        public Task<DocumentCacheAdministrativeBaselineBoundaryResult> CaptureBaselineBoundaryAsync(
            IRelationalWriteSession mutexSession,
            CancellationToken cancellationToken = default
        )
        {
            lock (owner._sync)
            {
                owner._boundaries++;
            }
            return inner.CaptureBaselineBoundaryAsync(mutexSession, cancellationToken);
        }

        public Task<DocumentCacheAdministrativeWorkHighWaterObservationResult> ObserveWorkHighWaterAsync(
            IRelationalWriteSession mutexSession,
            DocumentCacheAdministrativeWorkHighWaterObservationRequest request,
            CancellationToken cancellationToken = default
        )
        {
            return inner.ObserveWorkHighWaterAsync(mutexSession, request, cancellationToken);
        }

        public Task<DocumentCacheAdministrativeBaselineSeedPageResult> SeedBaselinePageAsync(
            IRelationalWriteSession mutexSession,
            DocumentCacheAdministrativeBaselineSeedPageRequest request,
            CancellationToken cancellationToken = default
        )
        {
            lock (owner._sync)
            {
                owner._baseline++;
            }
            return inner.SeedBaselinePageAsync(mutexSession, request, cancellationToken);
        }

        public Task<DocumentCacheAdministrativeScrubPageResult> ScrubPageAsync(
            IRelationalWriteSession mutexSession,
            DocumentCacheAdministrativeScrubPageRequest request,
            CancellationToken cancellationToken = default
        )
        {
            lock (owner._sync)
            {
                owner._inventory++;
            }
            return inner.ScrubPageAsync(mutexSession, request, cancellationToken);
        }
    }
}
