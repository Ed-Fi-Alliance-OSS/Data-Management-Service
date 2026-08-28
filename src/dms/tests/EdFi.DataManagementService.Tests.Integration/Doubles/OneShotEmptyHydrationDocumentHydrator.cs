// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using Microsoft.Extensions.DependencyInjection;

namespace EdFi.DataManagementService.Tests.Integration.Doubles;

/// <summary>
/// A hydrator decorator that empties the body of the <em>first</em> page whose selection really chose
/// keys, while leaving that page's selected maximum in place.
/// </summary>
/// <remarks>
/// It exists to make one approved contract observable end to end: the continuation header is gated on a
/// non-null selected maximum, not on the response body, so a page whose rows all disappeared before
/// hydration still advances a walk past the keys it selected. A client that stopped on an empty body
/// would stop early and silently skip the rest of the collection.
/// <para>
/// The real hydrator runs first, so provider page-selection SQL executes for real and the selected
/// maximum this decorator preserves is the one the database produced. Only the hydrated rows are
/// dropped afterwards.
/// </para>
/// <para>
/// This simulates the concurrent delete at the hydrator boundary rather than racing the database.
/// Selection and projection are statements inside one command batch, so nothing outside the process can
/// land a delete between them, and forcing that interleaving would need a production seam. What the
/// decorator does make real is everything downstream of hydration: the Core handler, the header rule,
/// response assembly, and the HTTP response a client receives.
/// </para>
/// <para>
/// Suppression is one-shot. A decorator that emptied every page would leave a walk unable to prove the
/// continuation still works, because every following page would be empty too for the same artificial
/// reason.
/// </para>
/// </remarks>
internal sealed class OneShotEmptyHydrationDocumentHydrator(
    IDocumentHydrator inner,
    HydrationSuppressionSwitch suppression
) : IDocumentHydrator
{
    private readonly IDocumentHydrator _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    private readonly HydrationSuppressionSwitch _suppression =
        suppression ?? throw new ArgumentNullException(nameof(suppression));

    public async Task<HydratedPage> HydrateAsync(
        ResourceReadPlan plan,
        PageKeysetSpec keyset,
        HydrationExecutionOptions executionOptions,
        CancellationToken ct
    )
    {
        HydratedPage page = await _inner.HydrateAsync(plan, keyset, executionOptions, ct);

        // Only a page that really selected keys and really hydrated rows is worth emptying: suppressing
        // an already-empty page would prove nothing, and would burn the one-shot on a request the test
        // was not aiming at.
        if (page.HighestSelectedAnchor is null || page.DocumentMetadata.Count == 0 || !_suppression.TryTake())
        {
            return page;
        }

        // The rows are emptied, not the result-set structure: the batch returns one entry per table in
        // the plan and one per descriptor projection, and reconstitution reads them positionally
        // against that plan. Dropping the entries themselves would model a batch the database never
        // returns and would fail for a reason unrelated to the contract under test.
        return page with
        {
            DocumentMetadata = [],
            TableRowsInDependencyOrder =
            [
                .. page.TableRowsInDependencyOrder.Select(static table => table with { Rows = [] }),
            ],
            DescriptorRowsInPlanOrder =
            [
                .. page.DescriptorRowsInPlanOrder.Select(static descriptors =>
                    descriptors with
                    {
                        Rows = [],
                    }
                ),
            ],
            DocumentReferenceLookup = page.DocumentReferenceLookup is null
                ? null
                : page.DocumentReferenceLookup with
                {
                    Rows = [],
                },
        };
    }
}

/// <summary>
/// Holds the one-shot suppression state for a host.
/// </summary>
/// <remarks>
/// Registered as a singleton on purpose. The hydrator is not, so the decorator wrapping it is rebuilt
/// per request scope; keeping the flag inside the decorator would make every request the first one and
/// empty every page of a walk, which would look like a broken continuation rather than a one-shot.
/// One instance per host also keeps concurrently running tests, each with its own host, independent.
/// </remarks>
internal sealed class HydrationSuppressionSwitch
{
    private int _taken;

    /// <summary>Takes the single suppression, returning false once it is gone.</summary>
    public bool TryTake() => Interlocked.Exchange(ref _taken, 1) == 0;
}

internal static class EmptyHydrationServiceCollectionExtensions
{
    /// <summary>
    /// Wraps the registered <see cref="IDocumentHydrator"/> so the first page that hydrates rows returns
    /// none of them while keeping its selected maximum.
    /// </summary>
    public static void SuppressHydratedRowsOnce(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        var descriptor =
            services.LastOrDefault(static service => service.ServiceType == typeof(IDocumentHydrator))
            ?? throw new InvalidOperationException(
                $"{nameof(IDocumentHydrator)} must be registered before hydration suppression can wrap it."
            );

        services.Remove(descriptor);
        services.AddSingleton<HydrationSuppressionSwitch>();
        services.Add(
            ServiceDescriptor.Describe(
                typeof(IDocumentHydrator),
                serviceProvider => new OneShotEmptyHydrationDocumentHydrator(
                    CreateInner(serviceProvider, descriptor),
                    serviceProvider.GetRequiredService<HydrationSuppressionSwitch>()
                ),
                descriptor.Lifetime
            )
        );
    }

    private static IDocumentHydrator CreateInner(
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
