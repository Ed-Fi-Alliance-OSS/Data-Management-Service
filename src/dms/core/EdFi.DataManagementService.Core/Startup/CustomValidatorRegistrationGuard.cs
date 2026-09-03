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
/// implementer is asked to follow. It applies two independent checks:
/// <list type="number">
/// <item>A descriptor registered under the contract carries a shape the contract permits. The
/// properties a ServiceDescriptor can hold are finite, so this check is complete by construction
/// rather than by enumerating mistakes.</item>
/// <item>Every type a descriptor shows to be a validator is represented among the instances the
/// container actually returns. This compares intent against the resolution DMS itself performs, so
/// it does not depend on knowing the ways a registration can fail to reach that resolution.</item>
/// </list>
/// The first aborts before the second resolves anything, so a descriptor rejected for its shape is
/// never also described as unreachable.
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

        List<string> shapeOffenses = AuditContractDescriptorShapes();

        if (shapeOffenses.Count > 0)
        {
            throw new InvalidOperationException(
                $"Startup aborted: {shapeOffenses.Count} ICustomResourceValidator registration(s) are "
                    + "invalid. Register each validator against ICustomResourceValidator with a Transient "
                    + "lifetime and an implementation type, for example "
                    + "services.TryAddEnumerable(ServiceDescriptor.Transient<ICustomResourceValidator, "
                    + "MyValidator>()). To supply configuration to a validator, bind an options type and "
                    + "take IOptions<T> in its constructor rather than registering a factory. Correct "
                    + "every registration listed below, then restart DMS: "
                    + string.Join(" | ", shapeOffenses)
            );
        }

        AsyncServiceScope scope = rootServiceProvider.CreateAsyncScope();
        try
        {
            List<ICustomResourceValidator> resolvedValidators = ResolveValidators(scope);

            VerifyEveryRegisteredValidatorResolved(resolvedValidators);
            InspectAppliesTo(resolvedValidators);

            logger.LogInformation(
                "Custom validator registration guard audited and activated {ValidatorCount} "
                    + "ICustomResourceValidator registration(s)",
                resolvedValidators.Count
            );
        }
        finally
        {
            // A validator's own Dispose is implementer code, and MS DI abandons the rest of the
            // scope's disposables after the first one throws. Letting that propagate would replace
            // whatever outcome the body reached, including a successful audit, with a dispose
            // failure.
            try
            {
                await scope.DisposeAsync();
            }
            catch (Exception disposeException)
            {
                logger.LogWarning(
                    disposeException,
                    "Disposing the ICustomResourceValidator activation scope threw. Any validator "
                        + "instance the scope had not yet disposed stays undisposed"
                );
            }
        }
    }

    /// <summary>
    /// Check 1. A descriptor registered under the contract, or under the collection type DMS
    /// resolves, has to carry a shape the contract permits.
    /// </summary>
    private List<string> AuditContractDescriptorShapes()
    {
        List<string> offenses = [];

        foreach (ServiceDescriptor descriptor in services)
        {
            if (descriptor.ServiceType == typeof(ICustomResourceValidator))
            {
                AuditContractDescriptor(descriptor, offenses);
            }
            else if (!descriptor.IsKeyedService && IsValidatorCollectionServiceType(descriptor.ServiceType))
            {
                offenses.Add(
                    $"'{DescribeImplementation(descriptor)}': registered against "
                        + $"{DescribeServiceType(descriptor.ServiceType)} rather than ICustomResourceValidator. "
                        + "Registering a collection type replaces the collection DMS resolves"
                );
            }
        }

        return offenses;
    }

    private static void AuditContractDescriptor(ServiceDescriptor descriptor, List<string> offenses)
    {
        List<string> brokenRules = [];

        if (descriptor.Lifetime != ServiceLifetime.Transient)
        {
            brokenRules.Add($"its lifetime is {descriptor.Lifetime} rather than Transient");
        }

        if (descriptor.ImplementationInstance is not null)
        {
            brokenRules.Add("it supplies a shared instance rather than an implementation type");
        }

        // A factory descriptor records what a delegate returns, not whether the delegate constructs
        // anything, so a Transient factory closing over one instance is indistinguishable from one
        // that constructs per resolution. Resolving cannot separate them either, which is why this
        // stays a descriptor rule.
        if (descriptor.ImplementationFactory is not null)
        {
            brokenRules.Add("it supplies a factory delegate rather than an implementation type");
        }

        if (brokenRules.Count > 0)
        {
            offenses.Add($"'{DescribeImplementation(descriptor)}': {string.Join("; ", brokenRules)}");
        }
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
                    + LoggingSanitizer.SanitizeForLogging(activationException.Message),
                activationException
            );
        }
    }

    /// <summary>
    /// Check 2. Every type the registrations show to be a validator has to be represented among the
    /// instances DMS's own resolution returns.
    /// </summary>
    /// <remarks>
    /// Comparison is against resolved instances rather than against a list of approved types, so
    /// aliasing a validator under an implementer's own interface beside a contract registration is
    /// not an offense: an instance satisfying the alias does resolve.
    /// </remarks>
    private void VerifyEveryRegisteredValidatorResolved(List<ICustomResourceValidator> resolvedValidators)
    {
        List<string> unreachable = [];

        foreach (ServiceDescriptor descriptor in services)
        {
            Type? registeredValidatorType = ValidatorTypeShownBy(descriptor);

            if (
                registeredValidatorType is null
                || resolvedValidators.Exists(registeredValidatorType.IsInstanceOfType)
            )
            {
                continue;
            }

            unreachable.Add(
                $"'{TypeNameForLog(registeredValidatorType)}': registered against "
                    + $"{DescribeServiceType(descriptor.ServiceType)}"
                    + (descriptor.IsKeyedService ? ", keyed" : string.Empty)
                    + $", {descriptor.Lifetime} lifetime"
            );
        }

        if (unreachable.Count == 0)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Startup aborted: {unreachable.Count} registered type(s) assignable to "
                + "ICustomResourceValidator are not among the "
                + $"{resolvedValidators.Count} validator(s) DMS resolved. DMS resolves "
                + "IEnumerable<ICustomResourceValidator>, so only a registration contributing to that "
                + "collection runs. Register each validator with "
                + "services.TryAddEnumerable(ServiceDescriptor.Transient<ICustomResourceValidator, "
                + "MyValidator>()), then restart DMS: "
                + string.Join(" | ", unreachable)
        );
    }

    /// <summary>
    /// The validator type a descriptor shows, or null when it shows none. An implementation type or
    /// instance names the concrete type directly. A factory names nothing, leaving the service type
    /// as the only evidence, which is worth reporting when it is assignable to the contract but is
    /// not the contract itself: a factory registered under the contract contributes to the resolved
    /// collection, so it is check 1's business and not this one's.
    /// </summary>
    /// <remarks>
    /// The residual is a factory under a service type unrelated to the contract, registered through
    /// the single-generic-argument overload, whose delegate is a Func&lt;IServiceProvider, TService&gt; and
    /// so names only the unrelated service type. The two-generic-argument overload does expose the
    /// implementation through the delegate's return type, but reading it means trusting a delegate
    /// signature rather than a descriptor property, and the reachability comparison below already
    /// catches that registration whenever the validator resolves nowhere else.
    /// </remarks>
    private static Type? ValidatorTypeShownBy(ServiceDescriptor descriptor)
    {
        Type? implementationType = ImplementationTypeOf(descriptor);

        if (implementationType is not null)
        {
            return typeof(ICustomResourceValidator).IsAssignableFrom(implementationType)
                ? implementationType
                : null;
        }

        return
            descriptor.ServiceType != typeof(ICustomResourceValidator)
            && typeof(ICustomResourceValidator).IsAssignableFrom(descriptor.ServiceType)
            ? descriptor.ServiceType
            : null;
    }

    private void InspectAppliesTo(List<ICustomResourceValidator> resolvedValidators)
    {
        ApiSchemaDocuments effectiveSchemaDocuments = effectiveApiSchemaProvider.Documents;
        List<string> unusableValidators = [];

        foreach (ICustomResourceValidator validator in resolvedValidators)
        {
            string validatorTypeName = TypeNameForLog(validator.GetType());

            // AppliesTo is implementer code, and so is the list it hands back: reading the
            // property, counting it, and walking it can each throw. All of it sits inside one try
            // so the failure is reported against the validator that produced it rather than
            // escaping to a startup message that names no registration.
            try
            {
                IReadOnlyList<ValidatedResource>? appliesToEntries = validator.AppliesTo;

                if (appliesToEntries is null)
                {
                    unusableValidators.Add(
                        $"'{validatorTypeName}': AppliesTo returned null, which the contract declares "
                            + "it never does"
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
                        "ICustomResourceValidator '{ValidatorType}' declares no AppliesTo entries, so "
                            + "it can never run for any resource",
                        validatorTypeName
                    );
                    continue;
                }

                foreach (ValidatedResource appliesToEntry in appliesToEntries)
                {
                    InspectAppliesToEntry(effectiveSchemaDocuments, validatorTypeName, appliesToEntry);
                }
            }
            catch (Exception appliesToException)
            {
                unusableValidators.Add(
                    $"'{validatorTypeName}': reading or walking AppliesTo threw "
                        + $"{appliesToException.GetType().FullName}: "
                        + LoggingSanitizer.SanitizeForLogging(appliesToException.Message)
                );
            }
        }

        if (unusableValidators.Count > 0)
        {
            throw new InvalidOperationException(
                $"Startup aborted: {unusableValidators.Count} registered ICustomResourceValidator(s) "
                    + "cannot be used: "
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
        // that cannot be looked up and a name that is simply absent are the same outcome.
        logger.LogWarning(
            "ICustomResourceValidator '{ValidatorType}' AppliesTo entry ProjectName '{ProjectName}', "
                + "ResourceName '{ResourceName}' matches no resource in the effective ApiSchema, so it "
                + "will never run. Matching is exact and ordinal. Expected for an extension resource "
                + "this deployment does not carry",
            validatorTypeName,
            projectName,
            resourceName
        );

        if (projectName != appliesToEntry.ProjectName || resourceName != appliesToEntry.ResourceName)
        {
            logger.LogWarning(
                "At least one of the ProjectName and ResourceName in the preceding record for "
                    + "ICustomResourceValidator '{ValidatorType}' was altered to make it safe to log, "
                    + "so compare against the AppliesTo entry in source rather than against that "
                    + "record",
                validatorTypeName
            );
        }
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
        serviceType == typeof(IEnumerable<ICustomResourceValidator>);

    private static string DescribeServiceType(Type serviceType) =>
        serviceType == typeof(IEnumerable<ICustomResourceValidator>)
            ? "IEnumerable<ICustomResourceValidator>"
            : TypeNameForLog(serviceType);

    private static Type? ImplementationTypeOf(ServiceDescriptor descriptor) =>
        descriptor.IsKeyedService
            ? descriptor.KeyedImplementationType ?? descriptor.KeyedImplementationInstance?.GetType()
            : descriptor.ImplementationType ?? descriptor.ImplementationInstance?.GetType();

    private static string DescribeImplementation(ServiceDescriptor descriptor)
    {
        Type? implementationType = ImplementationTypeOf(descriptor);

        return implementationType is null
            ? "<registration with no implementation type>"
            : TypeNameForLog(implementationType);
    }

    /// <summary>
    /// A type name for a log record. Type names come from assembly metadata, so the only hazard one
    /// carries is a control character forging a record; stripping just those leaves '+' and '`'
    /// intact, which LoggingSanitizer removes, so a nested or generic name stays searchable in the
    /// source it came from.
    /// </summary>
    private static string TypeNameForLog(Type type)
    {
        string name = type.FullName ?? type.Name;

        return string.Concat(name.Where(static character => !char.IsControl(character)));
    }
}
