// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.Api.Plugins;
using EdFi.Api.Plugins.Hosting;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.CustomValidation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Acme.DmsContributor;

/// <summary>
/// Contributes against the host's own contracts, doing whatever the host's <c>Fixture:Behavior</c> key
/// asks for.
/// </summary>
public sealed class DmsContributorPlugin : EdFiApiPlugin
{
    public override string Name => "Acme.DmsContributor";

    public override void ContributeServices(IServiceCollection services, IConfiguration configuration)
    {
        switch (configuration["Fixture:Behavior"])
        {
            case "decoyAudit":
                // A plugin's own audit input, registered before the host registers its own. The host's
                // is last, and a single-service resolve takes the last registration.
                services.AddSingleton(new PluginAuditInput(new PluginContractRegistry([]), [], []));
                AddValidator(services);
                break;

            case "hostTypeAdd":
                // A host service type that is no declared plugin contract, registered beside a declared
                // contract so the refusal is unambiguously the displacement. A factory descriptor,
                // because implementing the repository would be pages of code the check never reaches.
                services.Add(
                    ServiceDescriptor.Scoped<IDocumentStoreRepository>(_ =>
                        throw new NotSupportedException("the fixture never intends this to resolve")
                    )
                );
                AddValidator(services);
                break;

            case "hostTypeRemoveAll":
                services.RemoveAll<IDocumentStoreRepository>();
                break;

            case "hostTypeReplace":
                services.Replace(
                    ServiceDescriptor.Scoped<IDocumentStoreRepository>(_ =>
                        throw new NotSupportedException("the fixture never intends this to resolve")
                    )
                );
                break;

            case "hostTypeIndexer":
                AssignOverTheHostsRepositoryDescriptor(services);
                break;

            case "swallowHostTypeReplace":
                // The displacement refused, caught, and followed by a perfectly valid declared-contract
                // registration. Every downstream check passes on what this leaves behind, so the
                // invoker's own record of the refusal is the only thing that can fail it.
                try
                {
                    services.Replace(
                        ServiceDescriptor.Scoped<IDocumentStoreRepository>(_ =>
                            throw new NotSupportedException("the fixture never intends this to resolve")
                        )
                    );
                }
                catch (Exception)
                {
                    // Swallowed the way registration code treating its own setup as best-effort would.
                }

                AddValidator(services);
                break;

            default:
                AddValidator(services);
                break;
        }
    }

    /// <summary>
    /// The registration shape the fan-in cardinality asks for, against the real contract.
    /// </summary>
    private static void AddValidator(IServiceCollection services) =>
        services.TryAddEnumerable(
            ServiceDescriptor.Transient<ICustomResourceValidator, FixtureResourceValidator>()
        );

    /// <summary>
    /// Overwrites the host's repository descriptor through the indexer, which is the third way the
    /// interface lets a plugin displace one.
    /// </summary>
    private static void AssignOverTheHostsRepositoryDescriptor(IServiceCollection services)
    {
        for (int index = 0; index < services.Count; index++)
        {
            if (services[index].ServiceType != typeof(IDocumentStoreRepository))
            {
                continue;
            }

            services[index] = ServiceDescriptor.Scoped<IDocumentStoreRepository>(_ =>
                throw new NotSupportedException("the fixture never intends this to resolve")
            );

            return;
        }

        throw new InvalidOperationException(
            "the host registered no IDocumentStoreRepository descriptor, so this fixture cannot "
                + "overwrite one and its test would assert nothing"
        );
    }
}

/// <summary>A validator of the plugin's own, against the real contract.</summary>
public sealed class FixtureResourceValidator : ICustomResourceValidator
{
    public IReadOnlyList<ValidatedResource> AppliesTo { get; } = [new ValidatedResource("Ed-Fi", "School")];

    public Task<IReadOnlyList<CustomValidationFailure>> ValidateAsync(
        JsonNode document,
        ValidatedResourceInfo resource,
        CustomValidationOperation operation,
        ValidationScope scope,
        string traceId,
        CancellationToken cancellationToken
    ) => Task.FromResult<IReadOnlyList<CustomValidationFailure>>([]);
}
