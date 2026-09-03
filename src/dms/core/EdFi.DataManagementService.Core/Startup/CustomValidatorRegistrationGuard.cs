// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Utilities;
using EdFi.DataManagementService.CustomValidation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Core.Startup;

/// <summary>
/// Validates ICustomResourceValidator registrations before DMS serves traffic, and warns about
/// registrations that are valid but can never match a resource.
/// </summary>
/// <remarks>
/// Registration code runs outside this repository, so this cannot rely on any convention an
/// implementer is asked to follow. It checks three things that together avoid depending on an
/// enumeration of the ways a registration can be wrong:
/// <list type="number">
/// <item>Every descriptor registered under the contract is transient and unkeyed and carries an
/// implementation type.</item>
/// <item>No type assignable to the contract is registered only under some other service type, where
/// the resolution DMS performs would never find it.</item>
/// <item>The set of instances the container returns is exactly the set the first check approved.
/// Anything that substitutes, hides, or displaces the collection fails here regardless of how it
/// was registered.</item>
/// </list>
/// Descriptors come from the closure-captured collection rather than from a snapshot taken at the
/// registering extension's call site, because an implementer's registration may run either side of
/// Core's and is the party being checked.
/// </remarks>
internal sealed class CustomValidatorRegistrationGuard(
    IServiceCollection services,
    IServiceProvider rootServiceProvider,
    IEffectiveApiSchemaProvider effectiveApiSchemaProvider,
    ILogger<CustomValidatorRegistrationGuard> logger
) : IDmsStartupTask
{
    // IDmsStartupTask.cs recommends 200-299 for schema processing; nothing enforces that label and
    // this guard is not schema processing. The binding constraint is that it run inside a window
    // Program.cs executes and after LoadAndBuildEffectiveSchemaTask, whose effective ApiSchema the
    // AppliesTo check reads. It runs before the backend-mapping and auth-metadata windows, so a
    // validator constructor reading state those initialize would fail here despite being resolvable
    // at request time.
    public int Order => 250;

    public string Name => "Validate Custom Validator Registration";

    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        DescriptorAudit audit = AuditDescriptors();

        if (audit.Offenses.Count > 0)
        {
            throw new InvalidOperationException(
                $"Startup aborted: {audit.Offenses.Count} ICustomResourceValidator registration(s) are "
                    + "invalid. Register each validator against ICustomResourceValidator with a Transient "
                    + "lifetime and an implementation type, for example "
                    + "services.TryAddEnumerable(ServiceDescriptor.Transient<ICustomResourceValidator, "
                    + "MyValidator>()). To supply configuration to a validator, bind an options type and "
                    + "take IOptions<T> in its constructor rather than registering a factory. Correct "
                    + "every registration listed below, then restart DMS: "
                    + string.Join(" | ", audit.Offenses)
            );
        }

        AsyncServiceScope scope = rootServiceProvider.CreateAsyncScope();
        try
        {
            List<ICustomResourceValidator> resolvedValidators = ResolveValidators(scope);

            VerifyResolvedMatchesAudited(resolvedValidators, audit.ImplementationTypes);
            InspectAppliesTo(resolvedValidators);

            logger.LogInformation(
                "Custom validator registration guard audited and activated {ValidatorCount} "
                    + "ICustomResourceValidator registration(s)",
                resolvedValidators.Count
            );
        }
        finally
        {
            // A validator's own Dispose is implementer code. Letting it throw from here would abort
            // startup for a registration that broke no rule, after the success record above had
            // already been written.
            try
            {
                await scope.DisposeAsync();
            }
            catch (Exception disposeException)
            {
                logger.LogWarning(
                    disposeException,
                    "Disposing the ICustomResourceValidator activation scope threw. The validators "
                        + "themselves passed every check; this affects only the throwaway scope this "
                        + "guard used"
                );
            }
        }
    }

    private sealed record DescriptorAudit(
        IReadOnlyList<string> Offenses,
        IReadOnlyList<Type> ImplementationTypes
    );

    private DescriptorAudit AuditDescriptors()
    {
        List<string> offenses = [];
        List<Type> implementationTypes = [];
        List<ServiceDescriptor> otherServiceTypeDescriptors = [];

        foreach (ServiceDescriptor descriptor in services)
        {
            if (descriptor.ServiceType == typeof(ICustomResourceValidator))
            {
                AuditContractDescriptor(descriptor, offenses, implementationTypes);
            }
            else if (IsValidatorCollectionServiceType(descriptor.ServiceType))
            {
                offenses.Add(
                    $"'{DescribeImplementation(descriptor)}': registered against "
                        + $"{DescribeServiceType(descriptor.ServiceType)} rather than ICustomResourceValidator. "
                        + "Registering a "
                        + "collection type replaces the collection DMS resolves, so the validators it "
                        + "supplies are never checked here and separately registered validators stop "
                        + "resolving"
                );
            }
            else
            {
                otherServiceTypeDescriptors.Add(descriptor);
            }
        }

        // Invariant 2, checked after the loop so that registering the same type under the contract
        // as well, which is legitimate, does not read as an offense.
        foreach (ServiceDescriptor descriptor in otherServiceTypeDescriptors)
        {
            Type? implementationType = ImplementationTypeOf(descriptor);

            if (
                implementationType is not null
                && typeof(ICustomResourceValidator).IsAssignableFrom(implementationType)
                && !implementationTypes.Contains(implementationType)
            )
            {
                offenses.Add(
                    $"'{LoggingSanitizer.SanitizeForLogging(implementationType.FullName)}': implements "
                        + "ICustomResourceValidator but is "
                        + $"registered only against {DescribeServiceType(descriptor.ServiceType)}. DMS resolves "
                        + "ICustomResourceValidator, so this validator would never run"
                );
            }
        }

        return new DescriptorAudit(offenses, implementationTypes);
    }

    private static void AuditContractDescriptor(
        ServiceDescriptor descriptor,
        List<string> offenses,
        List<Type> implementationTypes
    )
    {
        List<string> brokenRules = [];

        if (descriptor.Lifetime != ServiceLifetime.Transient)
        {
            brokenRules.Add($"its lifetime is {descriptor.Lifetime} rather than Transient");
        }

        // Reading the unkeyed Implementation accessors on a keyed descriptor returns null rather
        // than throwing, so a keyed registration breaks none of the rules below, while the unkeyed
        // resolution DMS performs never returns it.
        if (descriptor.IsKeyedService)
        {
            brokenRules.Add("it is keyed, and DMS resolves without a key");
        }

        if (descriptor.ImplementationInstance is not null)
        {
            brokenRules.Add("it supplies a shared instance rather than an implementation type");
        }

        if (descriptor.ImplementationFactory is not null)
        {
            brokenRules.Add("it supplies a factory delegate rather than an implementation type");
        }

        if (brokenRules.Count == 0)
        {
            implementationTypes.Add(descriptor.ImplementationType!);
            return;
        }

        offenses.Add($"'{DescribeImplementation(descriptor)}': {string.Join("; ", brokenRules)}");
    }

    private static List<ICustomResourceValidator> ResolveValidators(AsyncServiceScope scope)
    {
        try
        {
            return [.. scope.ServiceProvider.GetServices<ICustomResourceValidator>()];
        }
        catch (Exception activationException)
        {
            // MS DI's own message names the unresolvable service and the type being activated.
            throw new InvalidOperationException(
                "Startup aborted: resolving the registered ICustomResourceValidator instances from a "
                    + "throwaway scope failed, which is the check that stands in for the per-request "
                    + "resolution a validator would otherwise fail on the first write reaching it. "
                    + $"Underlying activation exception: {activationException.GetType().FullName}: "
                    + activationException.Message,
                activationException
            );
        }
    }

    /// <summary>
    /// Invariant 3. Compares what the container returned against what the descriptor audit approved.
    /// </summary>
    /// <remarks>
    /// Internal rather than private so it can be tested directly. It is a backstop: every shape known
    /// to trip it is rejected by the earlier checks first, so no registration reachable through
    /// IServiceCollection gets this far, and an end-to-end test could not distinguish it from a
    /// weaker count comparison.
    /// </remarks>
    internal static void VerifyResolvedMatchesAudited(
        List<ICustomResourceValidator> resolvedValidators,
        IReadOnlyList<Type> auditedImplementationTypes
    )
    {
        List<string> resolved = [.. resolvedValidators.Select(TypeNameOf).Order(StringComparer.Ordinal)];
        List<string> audited =
        [
            .. auditedImplementationTypes
                .Select(static type => type.FullName ?? type.Name)
                .Order(StringComparer.Ordinal),
        ];

        if (resolved.SequenceEqual(audited, StringComparer.Ordinal))
        {
            return;
        }

        throw new InvalidOperationException(
            "Startup aborted: the ICustomResourceValidator instances DMS resolved are not the ones its "
                + "registrations describe, so something is supplying or hiding validators outside the "
                + "registrations this guard can check. Approved by the registration audit: "
                + $"[{string.Join(", ", audited)}]. Actually resolved: [{string.Join(", ", resolved)}]"
        );
    }

    private void InspectAppliesTo(List<ICustomResourceValidator> resolvedValidators)
    {
        ApiSchemaDocuments effectiveSchemaDocuments = effectiveApiSchemaProvider.Documents;
        List<string> unusableValidators = [];

        foreach (ICustomResourceValidator validator in resolvedValidators)
        {
            string validatorTypeName = LoggingSanitizer.SanitizeForLogging(TypeNameOf(validator));

            IReadOnlyList<ValidatedResource>? appliesToEntries;
            try
            {
                appliesToEntries = validator.AppliesTo;
            }
            catch (Exception appliesToException)
            {
                // design.md "Versioning and Compatibility" relies on reading AppliesTo here to
                // surface a validator built against a different version of the contract, which
                // package resolution unifies without failing. Warning would let that reach traffic.
                unusableValidators.Add(
                    $"'{validatorTypeName}': reading AppliesTo threw "
                        + $"{appliesToException.GetType().FullName}: {appliesToException.Message}"
                );
                continue;
            }

            if (appliesToEntries is null)
            {
                unusableValidators.Add(
                    $"'{validatorTypeName}': AppliesTo returned null, which the contract declares it "
                        + "never does"
                );
                continue;
            }

            if (appliesToEntries.Any(static entry => entry is null))
            {
                unusableValidators.Add(
                    $"'{validatorTypeName}': AppliesTo contains a null entry, which the contract "
                        + "declares it never does"
                );
                continue;
            }

            if (appliesToEntries.Count == 0)
            {
                logger.LogWarning(
                    "ICustomResourceValidator '{ValidatorType}' declares no AppliesTo entries, so it can "
                        + "never run for any resource",
                    validatorTypeName
                );
                continue;
            }

            foreach (ValidatedResource appliesToEntry in appliesToEntries)
            {
                InspectAppliesToEntry(effectiveSchemaDocuments, validatorTypeName, appliesToEntry);
            }
        }

        if (unusableValidators.Count > 0)
        {
            throw new InvalidOperationException(
                $"Startup aborted: {unusableValidators.Count} registered ICustomResourceValidator(s) "
                    + "cannot be used. This is the check that surfaces a validator compiled against a "
                    + "different version of the contract, which package resolution unifies without "
                    + "failing: "
                    + string.Join(" | ", unusableValidators)
            );
        }
    }

    private void InspectAppliesToEntry(
        ApiSchemaDocuments effectiveSchemaDocuments,
        string validatorTypeName,
        ValidatedResource appliesToEntry
    )
    {
        string projectName = LoggingSanitizer.SanitizeForLogging(appliesToEntry.ProjectName);
        string resourceName = LoggingSanitizer.SanitizeForLogging(appliesToEntry.ResourceName);

        logger.LogInformation(
            "ICustomResourceValidator '{ValidatorType}' AppliesTo entry: ProjectName '{ProjectName}', "
                + "ResourceName '{ResourceName}'",
            validatorTypeName,
            projectName,
            resourceName
        );

        if (FindResourceSchemaNode(effectiveSchemaDocuments, appliesToEntry) is not null)
        {
            return;
        }

        // One message for every kind of miss. Request-time matching is exact and ordinal, so a name
        // that cannot be looked up and a name that is simply absent are the same outcome, and a
        // single statement of fact cannot contradict itself for one of them.
        bool shownNamesDifferFromDeclared =
            projectName != appliesToEntry.ProjectName || resourceName != appliesToEntry.ResourceName;

        logger.LogWarning(
            "ICustomResourceValidator '{ValidatorType}' AppliesTo entry ProjectName '{ProjectName}', "
                + "ResourceName '{ResourceName}' matches no resource in the effective ApiSchema, so it "
                + "will never run. Matching is exact and ordinal. Expected for an extension resource "
                + "this deployment does not carry.{SanitizationNote}",
            validatorTypeName,
            projectName,
            resourceName,
            shownNamesDifferFromDeclared
                ? " The names above were altered to make them safe to log, so compare against the "
                    + "entry in source rather than against this message."
                : string.Empty
        );
    }

    private static JsonNode? FindResourceSchemaNode(
        ApiSchemaDocuments effectiveSchemaDocuments,
        ValidatedResource appliesToEntry
    )
    {
        // The resource-name lookup interpolates its argument into a quoted bracket selector of a
        // JsonPath. A double quote closes that selector and opens another, which parses and matches
        // a different resource; a backslash or control character makes the query unparseable, which
        // the lookup raises as an exception. A name the schema cannot hold as a resourceNameMapping
        // key is therefore not looked up at all. The project-name lookup compares ordinally against
        // a value read from a fixed path, so it is not restricted here.
        if (!IsSchemaResourceNameShape(appliesToEntry.ResourceName))
        {
            return null;
        }

        ProjectSchema? projectSchema = effectiveSchemaDocuments.FindProjectSchemaForProjectName(
            new ProjectName(appliesToEntry.ProjectName)
        );

        return projectSchema?.FindResourceSchemaNodeByResourceName(
            new ResourceName(appliesToEntry.ResourceName)
        );
    }

    /// <summary>
    /// Whether a name matches the shape JsonSchemaForApiSchema.json requires of resourceNameMapping
    /// keys, which is patternProperties ^[A-Za-z0-9]+$ with additionalProperties false.
    /// </summary>
    private static bool IsSchemaResourceNameShape(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        foreach (char character in name)
        {
            if (!char.IsAsciiLetterOrDigit(character))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsValidatorCollectionServiceType(Type serviceType) =>
        serviceType == typeof(IEnumerable<ICustomResourceValidator>) || serviceType == typeof(IEnumerable<>);

    private static string DescribeServiceType(Type serviceType)
    {
        if (serviceType == typeof(IEnumerable<ICustomResourceValidator>))
        {
            return "IEnumerable<ICustomResourceValidator>";
        }

        return serviceType == typeof(IEnumerable<>)
            ? "the open generic IEnumerable<>"
            : serviceType.FullName ?? serviceType.Name;
    }

    private static Type? ImplementationTypeOf(ServiceDescriptor descriptor) =>
        descriptor.IsKeyedService
            ? descriptor.KeyedImplementationType ?? descriptor.KeyedImplementationInstance?.GetType()
            : descriptor.ImplementationType ?? descriptor.ImplementationInstance?.GetType();

    private static string DescribeImplementation(ServiceDescriptor descriptor)
    {
        Type? implementationType = ImplementationTypeOf(descriptor);

        return implementationType is null
            ? "<registration with no implementation type>"
            : LoggingSanitizer.SanitizeForLogging(implementationType.FullName);
    }

    private static string TypeNameOf(object instance) =>
        instance.GetType().FullName ?? instance.GetType().Name;
}
