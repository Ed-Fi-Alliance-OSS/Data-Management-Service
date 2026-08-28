// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using Microsoft.Extensions.DependencyInjection;

namespace EdFi.DataManagementService.Tests.Integration.Doubles;

/// <summary>
/// Holds a request open at hydration until a test releases it, and announces when it arrives.
/// </summary>
/// <remarks>
/// Hydration is the last database work a read does, and it runs long after the effective target was
/// selected and the repository query executed against it. A request parked here is therefore provably
/// in flight against the configuration it started with - which is the whole point: publishing a new
/// configuration and hoping a request was mid-flight proves nothing, because every request may have
/// observed only the new one and the assertion would still pass.
/// </remarks>
public sealed class HydrationGate
{
    private readonly TaskCompletionSource _arrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _armed;

    /// <summary>Arms the gate for exactly one hydration; later hydrations pass straight through.</summary>
    public void Arm() => Interlocked.Exchange(ref _armed, 1);

    /// <summary>Completes once a request has reached hydration while the gate was armed.</summary>
    public Task Arrived => _arrived.Task;

    /// <summary>Lets the held request finish.</summary>
    public void Release() => _release.TrySetResult();

    internal async Task WaitIfArmedAsync()
    {
        if (Interlocked.Exchange(ref _armed, 0) == 0)
        {
            return;
        }

        _arrived.TrySetResult();
        await _release.Task;
    }
}

internal sealed class GatedDocumentHydrator(IDocumentHydrator inner, HydrationGate gate) : IDocumentHydrator
{
    public async Task<HydratedPage> HydrateAsync(
        ResourceReadPlan plan,
        PageKeysetSpec keyset,
        HydrationExecutionOptions executionOptions,
        CancellationToken ct
    )
    {
        await gate.WaitIfArmedAsync();

        return await inner.HydrateAsync(plan, keyset, executionOptions, ct);
    }
}

internal static class HydrationGateServiceCollectionExtensions
{
    public static void AddHydrationGate(this IServiceCollection services, HydrationGate gate)
    {
        ServiceDescriptor descriptor =
            services.LastOrDefault(static service => service.ServiceType == typeof(IDocumentHydrator))
            ?? throw new InvalidOperationException($"No {nameof(IDocumentHydrator)} registration to gate.");

        services.Remove(descriptor);
        services.AddSingleton(gate);
        services.Add(
            ServiceDescriptor.Describe(
                typeof(IDocumentHydrator),
                serviceProvider => new GatedDocumentHydrator(
                    (IDocumentHydrator)CreateInner(serviceProvider, descriptor),
                    gate
                ),
                descriptor.Lifetime
            )
        );
    }

    private static object CreateInner(IServiceProvider serviceProvider, ServiceDescriptor descriptor) =>
        descriptor.ImplementationInstance
        ?? descriptor.ImplementationFactory?.Invoke(serviceProvider)
        ?? ActivatorUtilities.CreateInstance(serviceProvider, descriptor.ImplementationType!);
}
