// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Acme.FixtureContracts;
using EdFi.Api.Plugins;
using EdFi.DataManagementService.FixtureHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Acme.Contributor;

/// <summary>
/// Contributes what the host's <c>Fixture:Behavior</c> key asks for.
/// </summary>
/// <remarks>
/// Everything here is code a real plugin could contain: it registers its own services, occasionally
/// edits its own registrations, and in several cases does something the host refuses. What the host
/// refuses is refused at the wrapper, so those branches never complete. A few branches then catch the
/// refusal and carry on, which is the shape that must still fail the composition.
/// </remarks>
public sealed class ContributorPlugin : EdFiApiPlugin
{
    public const string BehaviorKey = "Fixture:Behavior";

    public override string Name => "Acme.Contributor";

    public override void ContributeServices(IServiceCollection services, IConfiguration configuration)
    {
        switch (configuration[BehaviorKey])
        {
            case "enumerate":
                RecordWhatTheCollectionLooksLike(services);
                services.AddSingleton<IAcmeFirstService, AcmeService>();
                break;

            case "removeAll":
                services.RemoveAll<IAcmeSecondService>();
                services.AddSingleton<IAcmeFirstService, AcmeService>();
                break;

            case "frameworkRemove":
                services.RemoveAll(typeof(IOptions<>));
                services.AddSingleton<IAcmeFirstService, AcmeService>();
                break;

            case "ownReplace":
                services.AddSingleton<IAcmeFirstService, AcmeService>();
                services.Replace(ServiceDescriptor.Singleton<IAcmeFirstService, SecondAcmeService>());
                break;

            case "duplicateDescriptor":
                // The same descriptor instance, added twice. A diff that de-duplicated by reference
                // would call this one registration, and the replace-cardinality count downstream reads
                // these occurrences.
                ServiceDescriptor descriptor = ServiceDescriptor.Singleton<IAcmeFirstService, AcmeService>();
                services.Add(descriptor);
                services.Add(descriptor);
                break;

            case "clearProviders":
                services.AddLogging(builder => builder.ClearProviders());
                break;

            case "addProvider":
                services.AddLogging(builder => builder.AddProvider(new FixtureLoggerProvider()));
                services.AddSingleton<IAcmeFirstService, AcmeService>();
                break;

            case "replaceHostDefault":
                services.Replace(
                    ServiceDescriptor.Singleton<IFixtureHostService, FixtureHostServiceStandIn>()
                );
                break;

            case "replacePreExistingOwnKind":
                // A pre-existing descriptor for a service type nobody's host owns, replaced rather than
                // merely removed. Permitted, and the only shape that lands in all three of the record's
                // lists at once.
                services.Replace(ServiceDescriptor.Singleton<IAcmeSecondService, SecondAcmeService>());
                services.AddSingleton<IAcmeFirstService, AcmeService>();
                break;

            case "replaceClaim":
                // A single claim on the replace-cardinality contract, over the host default the host
                // registered before the hooks ran. Add rather than TryAdd, which is the rule for that
                // cardinality.
                services.Add(ServiceDescriptor.Singleton<IFixtureReplaceContract, FixtureReplaceClaim>());
                break;

            case "replaceClaimTwice":
                services.Add(ServiceDescriptor.Singleton<IFixtureReplaceContract, FixtureReplaceClaim>());
                services.Add(
                    ServiceDescriptor.Singleton<IFixtureReplaceContract, SecondFixtureReplaceClaim>()
                );
                break;

            case "hostTypeClaim":
                // A host-owned service type that is no declared contract, beside a declared contract, so
                // the refusal is unambiguously about the displacement rather than about contributing
                // nothing.
                services.AddSingleton<IFixtureHostUnclaimedService, FixtureHostServiceStandIn>();
                services.TryAddEnumerable(ServiceDescriptor.Transient<IFixtureFanInContract, FixtureFanIn>());
                break;

            case "declaredValidatorOnly":
                services.TryAddEnumerable(ServiceDescriptor.Transient<IFixtureFanInContract, FixtureFanIn>());
                break;

            case "ownTypesOnly":
                services.AddSingleton<IAcmeFirstService, AcmeService>();
                services.AddSingleton<IAcmeSecondService, SecondAcmeService>();
                break;

            case "contractPlusExtras":
                services.TryAddEnumerable(ServiceDescriptor.Transient<IFixtureFanInContract, FixtureFanIn>());
                services.AddSingleton<IAcmeFirstService, AcmeService>();
                services.Configure<FixtureContributorOptions>(options => options.Enabled = true);
                services.AddSingleton<IHostedService, FixtureHostedService>();
                break;

            case "unsatisfiable":
            // The broken half of the two-plugin fan-in pair. The same registration either way: what
            // the pair varies is which plugin the allowlist puts first.
            case "fanInPair":
                services.TryAddEnumerable(
                    ServiceDescriptor.Transient<IFixtureFanInContract, FixtureUnsatisfiableFanIn>()
                );
                break;

            // The broken half of the pair whose failure happens while the group is being materialized
            // rather than while its call sites are built, which is what leaves earlier elements of the
            // group constructed.
            case "fanInPairFactory":
            case "throwingFactory":
                // Add rather than TryAddEnumerable: a descriptor built from an untyped factory names no
                // implementation type, and TryAddEnumerable refuses one it cannot compare. A factory
                // descriptor is what this case is about, so it is registered the way one can be.
                services.Add(
                    ServiceDescriptor.Transient<IFixtureFanInContract>(_ =>
                    {
                        FixtureObservations.Count("throwingFactory");
                        throw new InvalidOperationException("the plugin's factory failed");
                    })
                );
                break;

            case "singletonContract":
                services.AddSingleton<IFixtureFanInContract, FixtureFanIn>();
                break;

            case "scopedContract":
                services.AddScoped<IFixtureFanInContract, FixtureDisposableFanIn>();
                break;

            case "transientContract":
                services.AddTransient<IFixtureFanInContract, FixtureFanIn>();
                break;

            case "disposableTransientContract":
                // A transient the container has to release: a transient resolved from a scope is
                // tracked by that scope, so this is what shows the probe's scope releasing one.
                services.AddTransient<IFixtureFanInContract, FixtureDisposableFanIn>();
                break;

            case "brokenKeyedContract":
                services.AddKeyedTransient<IFixtureFanInContract, FixtureUnsatisfiableFanIn>("broken-key");
                break;

            case "healthyFactoryContract":
                services.Add(
                    ServiceDescriptor.Transient<IFixtureFanInContract>(_ =>
                    {
                        FixtureObservations.Count("healthyFactory");
                        return new FixtureFanIn();
                    })
                );
                break;

            case "instanceContract":
                // A descriptor carrying an instance the plugin already built. Nothing activates it, so
                // the audit's resolution has to hand back that same object.
                services.AddSingleton<IFixtureFanInContract>(new FixtureFanIn());
                break;

            case "keyedContract":
                services.AddKeyedTransient<IFixtureFanInContract, FixtureFanIn>("first");
                break;

            case "twoKeyedContracts":
                services.AddKeyedTransient<IFixtureFanInContract, FixtureFanIn>("first");
                services.AddKeyedTransient<IFixtureFanInContract, FixtureFanIn>("second");
                break;

            case "anyKeyContract":
                services.AddKeyedTransient<IFixtureFanInContract, FixtureFanIn>(KeyedService.AnyKey);
                break;

            case "anyKeyAndConcreteContract":
                services.AddKeyedTransient<IFixtureFanInContract, FixtureFanIn>(KeyedService.AnyKey);
                services.AddKeyedTransient<IFixtureFanInContract, FixtureFanIn>("first");
                break;

            case "anyKeyOwnService":
                // A wildcard-keyed registration of a service type no host declares a contract for,
                // which stays ordinary permitted work.
                services.AddKeyedTransient<IAcmeFirstService, AcmeService>(KeyedService.AnyKey);
                services.TryAddEnumerable(ServiceDescriptor.Transient<IFixtureFanInContract, FixtureFanIn>());
                break;

            case "swallowClear":
                // The shape the host's latch exists for: a plugin that wraps setup in a catch-all, so
                // the refusal never leaves the hook. It catches Exception rather than the host's own
                // exception type because a plugin that references the hosting assembly is the rarer
                // case; this is ordinary defensive registration code.
                try
                {
                    services.Clear();
                }
                catch (Exception)
                {
                    // Swallowed on purpose. A real plugin would be treating its own setup as
                    // best-effort and would not know the host had refused anything.
                }

                services.TryAddEnumerable(ServiceDescriptor.Transient<IFixtureFanInContract, FixtureFanIn>());
                break;

            case "swallowClearProviders":
                try
                {
                    services.AddLogging(builder => builder.ClearProviders());
                }
                catch (Exception)
                {
                    // As above: the plugin believes it merely failed to install its own sink.
                }

                services.TryAddEnumerable(ServiceDescriptor.Transient<IFixtureFanInContract, FixtureFanIn>());
                break;

            case "swallowClearThenThrow":
                try
                {
                    services.Clear();
                }
                catch (Exception)
                {
                    // Swallowed, and then the hook fails for a reason of its own.
                }

                throw new InvalidOperationException("the plugin failed after swallowing the refusal");

            case "swallowClearThenDisplaceHostDefault":
                try
                {
                    services.Clear();
                }
                catch (Exception)
                {
                    // Swallowed, and then the hook trips a second, different refusal that it does not
                    // catch, so both of the invoker's exception paths are exercised by this pair.
                }

                services.Replace(
                    ServiceDescriptor.Singleton<IFixtureHostService, FixtureHostServiceStandIn>()
                );
                break;

            case "permittedRemovalAndProvider":
                // Everything a plugin is allowed to do that neighbours a refusal: removing a
                // pre-existing descriptor for a service type no host owns, adding a sink of its own,
                // and registering a declared contract. Nothing here may latch a refusal.
                services.RemoveAll<IAcmeSecondService>();
                services.AddLogging(builder => builder.AddProvider(new FixtureLoggerProvider()));
                services.TryAddEnumerable(ServiceDescriptor.Transient<IFixtureFanInContract, FixtureFanIn>());
                break;

            case "decliningTryAdd":
                // Declines, because the host default already holds the contract, and contributes
                // nothing else. The decline itself is invisible at the seam.
                services.TryAdd(ServiceDescriptor.Singleton<IFixtureReplaceContract, FixtureReplaceClaim>());
                break;

            case "decliningTryAddPlusFanIn":
                services.TryAdd(ServiceDescriptor.Singleton<IFixtureReplaceContract, FixtureReplaceClaim>());
                services.TryAddEnumerable(ServiceDescriptor.Transient<IFixtureFanInContract, FixtureFanIn>());
                break;

            default:
                services.AddSingleton<IAcmeFirstService, AcmeService>();
                services.AddSingleton<IAcmeSecondService, SecondAcmeService>();
                break;
        }
    }

    /// <summary>
    /// Reads the collection the way a plugin inspecting the host's registrations would, and reports
    /// whether every read agrees with the sequence it enumerated.
    /// </summary>
    private static void RecordWhatTheCollectionLooksLike(IServiceCollection services)
    {
        List<ServiceDescriptor> enumerated = [.. services];

        bool countAgrees = services.Count == enumerated.Count;
        bool indexerAgrees = true;
        bool indexOfAgrees = true;
        bool containsAgrees = true;

        for (int index = 0; index < enumerated.Count; index++)
        {
            indexerAgrees &= ReferenceEquals(services[index], enumerated[index]);
            indexOfAgrees &= services.IndexOf(enumerated[index]) == index;
            containsAgrees &= services.Contains(enumerated[index]);
        }

        FixtureObservations.Record("count", countAgrees.ToString());
        FixtureObservations.Record("indexer", indexerAgrees.ToString());
        FixtureObservations.Record("indexOf", indexOfAgrees.ToString());
        FixtureObservations.Record("contains", containsAgrees.ToString());
        FixtureObservations.Record("sequence", string.Join(",", enumerated.Select(d => d.ServiceType.Name)));

        // The replace-cardinality contract the host registered before this hook. An earlier design
        // masked these from a plugin's view; this records that nothing is hidden.
        FixtureObservations.Record(
            "sawReplaceContract",
            enumerated.Exists(d => d.ServiceType == typeof(IFixtureReplaceContract)).ToString()
        );
    }
}

/// <summary>A stand-in the plugin would install over the host's, which the wrapper refuses.</summary>
public sealed class FixtureHostServiceStandIn : IFixtureHostService, IFixtureHostUnclaimedService
{
    public string Describe() => nameof(FixtureHostServiceStandIn);
}

/// <summary>The plugin's claim on the replace-cardinality contract.</summary>
public sealed class FixtureReplaceClaim : IFixtureReplaceContract
{
    public string Describe() => nameof(FixtureReplaceClaim);
}

/// <summary>A second claim, for the case where one plugin registers two.</summary>
public sealed class SecondFixtureReplaceClaim : IFixtureReplaceContract
{
    public string Describe() => nameof(SecondFixtureReplaceClaim);
}

/// <summary>
/// A constructible implementation of the fan-in contract, which counts its own constructions so a
/// test can assert how many times the host activated it.
/// </summary>
public sealed class FixtureFanIn : IFixtureFanInContract
{
    public FixtureFanIn() => FixtureObservations.Count("fanIn.constructed");

    public string Describe() => nameof(FixtureFanIn);
}

/// <summary>The same, and disposable, so the scoped case can assert what the probe's scope released.</summary>
public sealed class FixtureDisposableFanIn : IFixtureFanInContract, IDisposable
{
    public FixtureDisposableFanIn() => FixtureObservations.Count("fanIn.constructed");

    public string Describe() => nameof(FixtureDisposableFanIn);

    public void Dispose() => FixtureObservations.Count("fanIn.disposed");
}

/// <summary>
/// An implementation of the fan-in contract taking a service nobody registered, which is the shape the
/// activation probe exists to catch.
/// </summary>
public sealed class FixtureUnsatisfiableFanIn(IFixtureMissingDependency missing) : IFixtureFanInContract
{
    public string Describe() => missing.Describe();
}

/// <summary>Options of the plugin's own, which the exemption permits.</summary>
public sealed class FixtureContributorOptions
{
    public bool Enabled { get; set; }
}

/// <summary>
/// A background task of the plugin's own. Permitted, and listed in the plugin's record, which is the
/// only visibility the design offers into it.
/// </summary>
public sealed class FixtureHostedService : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>A sink of the plugin's own, which the logging carve-out permits.</summary>
public sealed class FixtureLoggerProvider : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) =>
        Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

    public void Dispose()
    {
        // Nothing to release: the loggers this hands out are the framework's null loggers.
    }
}
