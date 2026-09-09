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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Acme.Contributor;

/// <summary>
/// Contributes what the host's <c>Fixture:Behavior</c> key asks for.
/// </summary>
/// <remarks>
/// Everything here is code a real plugin could contain: it registers its own services, occasionally
/// edits its own registrations, and in two cases does something the host refuses. What the host
/// refuses is refused at the wrapper, so those branches never complete.
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
public sealed class FixtureHostServiceStandIn : IFixtureHostService
{
    public string Describe() => nameof(FixtureHostServiceStandIn);
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
