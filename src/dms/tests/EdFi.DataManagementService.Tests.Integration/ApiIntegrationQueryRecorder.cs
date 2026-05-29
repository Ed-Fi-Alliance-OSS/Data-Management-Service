// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace EdFi.DataManagementService.Tests.Integration;

public sealed class ApiIntegrationQueryRecorder
{
    private readonly object _sync = new();
    private readonly List<PageKeysetSpec> _hydrationKeysets = [];

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

    internal void Record(PageKeysetSpec keyset)
    {
        lock (_sync)
        {
            _hydrationKeysets.Add(keyset);
        }
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

internal static class ApiIntegrationQueryRecordingServiceCollectionExtensions
{
    public static void ReplaceDocumentHydratorWithRecorder(this IServiceCollection services)
    {
        var descriptor =
            services.LastOrDefault(static service => service.ServiceType == typeof(IDocumentHydrator))
            ?? throw new InvalidOperationException(
                $"{nameof(IDocumentHydrator)} must be registered before query recording can wrap it."
            );

        services.Remove(descriptor);
        services.Add(
            ServiceDescriptor.Describe(
                typeof(IDocumentHydrator),
                serviceProvider => new RecordingDocumentHydrator(
                    CreateInnerDocumentHydrator(serviceProvider, descriptor),
                    serviceProvider.GetRequiredService<ApiIntegrationQueryRecorder>()
                ),
                descriptor.Lifetime
            )
        );
    }

    private static IDocumentHydrator CreateInnerDocumentHydrator(
        IServiceProvider serviceProvider,
        ServiceDescriptor descriptor
    )
    {
        if (descriptor.ImplementationInstance is IDocumentHydrator instance)
        {
            return instance;
        }

        if (descriptor.ImplementationFactory is not null)
        {
            return (IDocumentHydrator)descriptor.ImplementationFactory(serviceProvider)!;
        }

        if (descriptor.ImplementationType is not null)
        {
            return (IDocumentHydrator)
                ActivatorUtilities.CreateInstance(serviceProvider, descriptor.ImplementationType);
        }

        throw new InvalidOperationException(
            $"{nameof(IDocumentHydrator)} registration does not have an implementation."
        );
    }
}
