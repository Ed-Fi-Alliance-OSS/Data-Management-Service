// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace EdFi.DataManagementService.Tests.Integration;

public sealed class ApiIntegrationQueryRecorder
{
    private readonly object _sync = new();
    private readonly List<PageKeysetSpec> _hydrationKeysets = [];
    private int _relationalCommandExecutions;

    public IReadOnlyList<PageKeysetSpec> HydrationKeysets
    {
        get
        {
            lock (_sync)
            {
                return [.. _hydrationKeysets];
            }
        }
    }

    /// <summary>Number of page hydrations observed so far.</summary>
    public int HydrationCount
    {
        get
        {
            lock (_sync)
            {
                return _hydrationKeysets.Count;
            }
        }
    }

    /// <summary>
    /// Number of commands issued through <see cref="IRelationalCommandExecutor"/> so far. This is the
    /// seam the partition boundary command, the descriptor read, and the custom-view validation probe
    /// use, none of which are visible as a hydration.
    /// </summary>
    public int RelationalCommandExecutions => Volatile.Read(ref _relationalCommandExecutions);

    /// <summary>
    /// Total database commands observed so far: hydrations plus command-executor commands. The two
    /// seams are disjoint - a document hydrator opens its own connection and runs through
    /// <c>HydrationExecutor</c>, never through <see cref="IRelationalCommandExecutor"/> - so the sum
    /// double-counts nothing. Snapshot it before and after a single request and assert on the delta.
    /// </summary>
    public int DatabaseCommands => HydrationCount + RelationalCommandExecutions;

    internal void Record(PageKeysetSpec keyset)
    {
        lock (_sync)
        {
            _hydrationKeysets.Add(keyset);
        }
    }

    internal void RecordRelationalCommandExecution()
    {
        Interlocked.Increment(ref _relationalCommandExecutions);
    }

    public PageKeysetSpec.Query AssertSingleQueryHydration()
    {
        var hydrationKeysets = HydrationKeysets;

        hydrationKeysets.Should().ContainSingle();
        hydrationKeysets[0].Should().BeOfType<PageKeysetSpec.Query>();

        return (PageKeysetSpec.Query)hydrationKeysets[0];
    }
}

internal sealed class RecordingDocumentHydrator(IDocumentHydrator inner, ApiIntegrationQueryRecorder recorder)
    : IDocumentHydrator
{
    private readonly IDocumentHydrator _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    private readonly ApiIntegrationQueryRecorder _recorder =
        recorder ?? throw new ArgumentNullException(nameof(recorder));

    public async Task<HydratedPage> HydrateAsync(
        ResourceReadPlan plan,
        PageKeysetSpec keyset,
        HydrationExecutionOptions executionOptions,
        CancellationToken ct
    )
    {
        _recorder.Record(keyset);

        return await _inner.HydrateAsync(plan, keyset, executionOptions, ct);
    }
}

internal sealed class RecordingRelationalCommandExecutor(
    IRelationalCommandExecutor inner,
    ApiIntegrationQueryRecorder recorder
) : IRelationalCommandExecutor
{
    private readonly IRelationalCommandExecutor _inner =
        inner ?? throw new ArgumentNullException(nameof(inner));
    private readonly ApiIntegrationQueryRecorder _recorder =
        recorder ?? throw new ArgumentNullException(nameof(recorder));

    public SqlDialect Dialect => _inner.Dialect;

    public Task<TResult> ExecuteReaderAsync<TResult>(
        RelationalCommand command,
        Func<IRelationalCommandReader, CancellationToken, Task<TResult>> readAsync,
        CancellationToken cancellationToken = default
    )
    {
        _recorder.RecordRelationalCommandExecution();

        return _inner.ExecuteReaderAsync(command, readAsync, cancellationToken);
    }
}

internal static class ApiIntegrationQueryRecordingServiceCollectionExtensions
{
    public static void ReplaceDocumentHydratorWithRecorder(this IServiceCollection services)
    {
        services.ReplaceWithRecorder<IDocumentHydrator>(
            static (inner, recorder) => new RecordingDocumentHydrator(inner, recorder)
        );
    }

    public static void ReplaceRelationalCommandExecutorWithRecorder(this IServiceCollection services)
    {
        services.ReplaceWithRecorder<IRelationalCommandExecutor>(
            static (inner, recorder) => new RecordingRelationalCommandExecutor(inner, recorder)
        );
    }

    private static void ReplaceWithRecorder<TService>(
        this IServiceCollection services,
        Func<TService, ApiIntegrationQueryRecorder, TService> createRecordingDecorator
    )
        where TService : class
    {
        var descriptor =
            services.LastOrDefault(static service => service.ServiceType == typeof(TService))
            ?? throw new InvalidOperationException(
                $"{typeof(TService).Name} must be registered before query recording can wrap it."
            );

        services.Remove(descriptor);
        services.Add(
            ServiceDescriptor.Describe(
                typeof(TService),
                serviceProvider =>
                    createRecordingDecorator(
                        CreateInner<TService>(serviceProvider, descriptor),
                        serviceProvider.GetRequiredService<ApiIntegrationQueryRecorder>()
                    ),
                descriptor.Lifetime
            )
        );
    }

    private static TService CreateInner<TService>(
        IServiceProvider serviceProvider,
        ServiceDescriptor descriptor
    )
        where TService : class
    {
        if (descriptor.ImplementationInstance is TService instance)
        {
            return instance;
        }

        if (descriptor.ImplementationFactory is not null)
        {
            return (TService)descriptor.ImplementationFactory(serviceProvider)!;
        }

        if (descriptor.ImplementationType is not null)
        {
            return (TService)
                ActivatorUtilities.CreateInstance(serviceProvider, descriptor.ImplementationType);
        }

        throw new InvalidOperationException(
            $"{typeof(TService).Name} registration does not have an implementation."
        );
    }
}
