// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using Acme.Private;
using EdFi.Api.Plugins;
using EdFi.DataManagementService.CustomValidation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Acme.DmsHookTouch;

/// <summary>
/// Reaches a declared dependency for the first time from inside its hook, and registers the one
/// contract the Data Management Service declares so the real host accepts it.
/// </summary>
/// <remarks>
/// <para>
/// The touch is the subject. Assemblies load lazily, so Acme.Private is not loaded when the loader
/// finishes and is loaded when this returns, which is what separates a flag read where the inventory
/// is emitted from one frozen earlier.
/// </para>
/// <para>
/// What the hook saw is written to a file rather than to a static, because a static in this assembly
/// lives in the plugin's own load context and the host that would read it is in another. The path
/// arrives through the host's configuration, which is the only channel a hook has.
/// </para>
/// </remarks>
public sealed class DmsHookTouchPlugin : EdFiApiPlugin
{
    public override string Name => "Acme.DmsHookTouch";

    public override void ContributeServices(IServiceCollection services, IConfiguration configuration)
    {
        // Forces the private dependency's first load, from inside the hook. Written somewhere the
        // compiler cannot elide.
        RecordLine(configuration, "touched", PrivateMarker.Describe());

        WriteObservedServiceTypes(services, configuration);

        if (configuration["Fixture:Behavior"] == "removeFrameworkDescriptor")
        {
            RemovePreExistingFrameworkDescriptor(services);
        }

        if (configuration["Fixture:Behavior"] == "validatorWrongLifetime")
        {
            // A shape the host's own custom-validator audit refuses at startup. The plugin is not at
            // fault in any way the plugin checks can see, which is exactly the case the inventory
            // event exists for: without it an operator is left with an implementation type name and
            // no way back to the plugin that supplied it.
            services.TryAddEnumerable(
                ServiceDescriptor.Singleton<ICustomResourceValidator, HookTouchResourceValidator>()
            );

            return;
        }

        services.TryAddEnumerable(
            ServiceDescriptor.Transient<ICustomResourceValidator, HookTouchResourceValidator>()
        );
    }

    /// <summary>
    /// Removes one descriptor the host registered before this hook ran, which the design permits.
    /// </summary>
    /// <remarks>
    /// Matched by type name rather than by a reference to the package that declares it, so the fixture
    /// takes on no dependency for a case that is about the removal record. The chosen type is outside
    /// the host-owned set and outside the logging-pipeline set, so the wrapper allows it, and dropping
    /// it only stops HttpClient's own request logging, which nothing in this boot depends on.
    /// </remarks>
    private static void RemovePreExistingFrameworkDescriptor(IServiceCollection services)
    {
        ServiceDescriptor? target = services.FirstOrDefault(descriptor =>
            descriptor.ServiceType.FullName == "Microsoft.Extensions.Http.IHttpMessageHandlerBuilderFilter"
        );

        if (target is null)
        {
            throw new InvalidOperationException(
                "the host registered no IHttpMessageHandlerBuilderFilter, so this fixture cannot "
                    + "remove one and the test over its removal record would assert nothing"
            );
        }

        services.Remove(target);
    }

    /// <summary>
    /// Writes every service type the hook was handed, one per line, when the host asked for it.
    /// </summary>
    private static void WriteObservedServiceTypes(IServiceCollection services, IConfiguration configuration)
    {
        string? path = configuration["Fixture:ObservedServiceTypesPath"];

        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        File.WriteAllLines(
            path,
            services.Select(descriptor => descriptor.ServiceType.FullName ?? descriptor.ServiceType.Name)
        );
    }

    private static void RecordLine(IConfiguration configuration, string key, string value)
    {
        string? path = configuration["Fixture:ObservationPath"];

        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        File.AppendAllLines(path, [$"{key}={value}"]);
    }
}

/// <summary>A validator that passes everything, against the real contract.</summary>
public sealed class HookTouchResourceValidator : ICustomResourceValidator
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
