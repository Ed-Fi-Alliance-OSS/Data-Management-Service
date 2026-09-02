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
/// Validates that every ICustomResourceValidator registration reaching the built container is
/// transient, constructible, and shaped correctly, and warns when a validator's declared AppliesTo
/// entries name no resource in the effective ApiSchema.
/// Reads registrations from the closure-captured <see cref="IServiceCollection"/> rather than at the
/// registering extension's own call site, because a plugin's registration may run before or after
/// Core's extension runs. The guard must see the collection after every registrant, including the
/// plugin it exists to check, has had its turn.
/// </summary>
internal sealed class CustomValidatorRegistrationGuard(
    IServiceCollection services,
    IServiceProvider rootServiceProvider,
    IEffectiveApiSchemaProvider effectiveApiSchemaProvider,
    ILogger<CustomValidatorRegistrationGuard> logger
) : IDmsStartupTask
{
    // IDmsStartupTask's "Recommended ranges" comment (IDmsStartupTask.cs:25) labels 200-299 "Schema
    // processing" (IDmsStartupTask.cs:27). This guard is not schema processing; that label is a
    // recommendation enforced by nothing, so this value must not be silently "corrected" to fit it
    // later. 250 rather than joining the two sibling registration guards at 50/55 because those run
    // before LoadAndBuildEffectiveSchemaTask (Order => 100, LoadAndBuildEffectiveSchemaTask.cs:19),
    // whose effective ApiSchema this guard's AppliesTo warning reads.
    public int Order => 250;

    public string Name => "Validate Custom Validator Registration";

    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        List<string> offenses = [];

        foreach (ServiceDescriptor descriptor in services)
        {
            if (descriptor.ServiceType != typeof(ICustomResourceValidator))
            {
                continue;
            }

            List<string> brokenRules = [];

            if (descriptor.Lifetime != ServiceLifetime.Transient)
            {
                brokenRules.Add(
                    $"lifetime is {descriptor.Lifetime}, but ICustomResourceValidator registrations must be Transient"
                );
            }

            // A keyed descriptor carries the right ServiceType, so it reaches this audit, but every
            // unkeyed Implementation* accessor on one returns null rather than throwing, so it breaks
            // none of the rules below. Meanwhile the unkeyed GetServices<ICustomResourceValidator>()
            // that the activation probe below and the request path both perform never yields it. Left
            // unrejected it would pass the audit, skip the probe, never be AppliesTo-checked, and
            // never run, while this guard reported success.
            if (descriptor.IsKeyedService)
            {
                brokenRules.Add(
                    "registered as a keyed service, which the unkeyed ICustomResourceValidator "
                        + "resolution DMS performs never sees, so this validator would never run"
                );
            }

            if (descriptor.ImplementationInstance is not null)
            {
                brokenRules.Add(
                    "registered as a shared instance (ImplementationInstance is set), which hands every request "
                        + "the same object regardless of the declared lifetime"
                );
            }

            if (descriptor.ImplementationFactory is not null)
            {
                brokenRules.Add(
                    "registered through an ImplementationFactory delegate, which cannot be proven to construct "
                        + "a new instance on every resolution"
                );
            }

            if (brokenRules.Count == 0)
            {
                continue;
            }

            // The keyed accessors are the mirror image of the unkeyed ones: reading
            // KeyedImplementationType on a non-keyed descriptor throws InvalidOperationException,
            // where reading ImplementationType on a keyed one merely returns null. So which pair is
            // safe to read depends on IsKeyedService and neither can be used as a fallback for the
            // other.
            string? implementationTypeName = descriptor.IsKeyedService
                ? descriptor.KeyedImplementationType?.FullName
                    ?? descriptor.KeyedImplementationInstance?.GetType().FullName
                : descriptor.ImplementationType?.FullName
                    ?? descriptor.ImplementationInstance?.GetType().FullName;

            string descriptorLabel =
                implementationTypeName ?? "<factory-based registration with no implementation type>";

            offenses.Add($"'{descriptorLabel}': {string.Join("; ", brokenRules)}");
        }

        if (offenses.Count > 0)
        {
            throw new InvalidOperationException(
                $"Startup aborted: {offenses.Count} ICustomResourceValidator registration(s) are invalid. "
                    + "Every ICustomResourceValidator must be registered with a Transient lifetime and an "
                    + "implementation type, for example services.AddTransient<ICustomResourceValidator, "
                    + "MyValidator>(). A registration that supplies a shared instance or a factory delegate is "
                    + "rejected because it can hand every request the same object, defeating the transient "
                    + "contract that lets each request-scoped dependency be resolved fresh. Correct every "
                    + $"registration listed below, then restart DMS: {string.Join(" | ", offenses)}"
            );
        }

        // The audit above only proves every descriptor is *shaped* correctly; it never asks MS DI to build
        // one. Left alone, MS DI defers that question indefinitely: constructors resolve lazily and
        // ValidateOnBuild is off outside Development. Nothing on the request path resolves
        // ICustomResourceValidator yet, so today an unsatisfiable constructor dependency would simply
        // sit undetected; once the step that resolves validators per write request lands, it would
        // instead surface as a 500 on a write, long after this task ran. Resolving the full set once,
        // here, from a throwaway scope turns either outcome into a startup failure.
        // This is a constructibility check only, not a disposal boundary: a
        // singleton resolved inside this scope would still be cached in the root container and would stay
        // the instance production uses, so this does not guarantee anything about a validator's dependence
        // on per-request state, only that it can be constructed.
        // CreateAsyncScope, not CreateScope: a validator (or anything its constructor pulls in) that
        // implements only IAsyncDisposable is tracked by this scope, and disposing such a scope
        // synchronously throws InvalidOperationException from the dispose rather than from the
        // resolution, so it would escape the catch below and abort startup for a registration that
        // breaks no rule.
        await using AsyncServiceScope scope = rootServiceProvider.CreateAsyncScope();

        List<ICustomResourceValidator> resolvedValidators;
        try
        {
            resolvedValidators = [.. scope.ServiceProvider.GetServices<ICustomResourceValidator>()];
        }
        catch (Exception activationException)
        {
            // MS DI's own message already names both the unresolvable dependency and the type being
            // activated ("Unable to resolve service for type 'X' while attempting to activate 'Y'"),
            // and re-activating each audited type individually through ActivatorUtilities reproduces
            // that same string rather than improving on it: for a dependency more than one hop away
            // both forms name the intermediate service, not the validator. ActivatorUtilities also
            // applies different constructor-selection rules than the container, so a second pass can
            // attribute a failure differently from the one it is explaining.
            throw new InvalidOperationException(
                "Startup aborted: resolving the registered ICustomResourceValidator instances from a "
                    + "throwaway scope failed, which is the check that stands in for the per-request "
                    + "resolution a validator would otherwise fail on the first write reaching it. "
                    + $"Underlying activation exception: {activationException.GetType().FullName}: "
                    + activationException.Message,
                activationException
            );
        }

        // AppliesTo names resources as data (reference/design/custom-validation-DMS-1345/design.md,
        // "Resource Applicability"), so a typo'd or
        // wrong-cased entry only surfaces by resolving it here against the same effective ApiSchema the
        // fan-in step matches at request time. A miss warns rather than fails - an entry may legitimately
        // name an extension resource this deployment does not carry. Matching stays exact and ordinal by
        // design; a case-insensitive or endpoint-name fallback would hide an entry that will never match
        // at request time either.
        // Read once, outside the per-entry work below. This accessor throws
        // InvalidOperationException when the effective schema has not been built yet, and the
        // per-entry path deliberately treats a lookup miss as a warning: reading it inside that path
        // would turn an ordering regression into a benign-looking "matches no resource" warning on
        // every entry while startup carried on, which is the ordering invariant the Order comment
        // above exists to defend.
        ApiSchemaDocuments effectiveSchemaDocuments = effectiveApiSchemaProvider.Documents;

        foreach (ICustomResourceValidator validator in resolvedValidators)
        {
            string validatorTypeName = validator.GetType().FullName ?? validator.GetType().Name;

            // AppliesTo is implementer-authored code running outside this repository, so it can
            // return null despite the interface declaring a non-null list. Null and empty mean the
            // same thing operationally - the validator can never run for any resource - which is the
            // same silently-absent-validation outcome the unmatched-entry warning below surfaces.
            IReadOnlyList<ValidatedResource> appliesToEntries = validator.AppliesTo ?? [];

            if (appliesToEntries.Count == 0)
            {
                logger.LogWarning(
                    "ICustomResourceValidator '{ValidatorType}' declares no AppliesTo entries, so it "
                        + "can never run for any resource",
                    validatorTypeName
                );
                continue;
            }

            foreach (ValidatedResource appliesToEntry in appliesToEntries)
            {
                string sanitizedProjectName = LoggingSanitizer.SanitizeForLogging(appliesToEntry.ProjectName);
                string sanitizedResourceName = LoggingSanitizer.SanitizeForLogging(
                    appliesToEntry.ResourceName
                );

                logger.LogInformation(
                    "ICustomResourceValidator '{ValidatorType}' AppliesTo entry: ProjectName "
                        + "'{ProjectName}', ResourceName '{ResourceName}'",
                    validatorTypeName,
                    sanitizedProjectName,
                    sanitizedResourceName
                );

                // Both lookups below build a JsonPath by interpolating these names into a quoted
                // bracket selector, so a name carrying a character that selector cannot hold is not
                // merely unmatchable. A control character or a trailing backslash makes the query
                // fail to parse, and the lookup surfaces that as an exception that would abort
                // startup. A name embedding a double quote is worse, because it can close the
                // selector and open a second one naming a real resource, parse cleanly, and match
                // that resource instead, reporting a match for an entry that could never match at
                // request time. Neither kind of name can identify a real resource in any case,
                // because request-time matching is exact and ordinal. So an unsafe name is reported
                // as the miss it is, without attempting the lookup at all, which also keeps the raw
                // value away from the JsonPath helper's own error log, where the interpolated query
                // is recorded unsanitized.
                bool namesAreLookupSafe =
                    IsLookupSafeName(appliesToEntry.ProjectName)
                    && IsLookupSafeName(appliesToEntry.ResourceName);

                JsonNode? resourceSchemaNode = null;

                if (namesAreLookupSafe)
                {
                    ProjectSchema? projectSchema = effectiveSchemaDocuments.FindProjectSchemaForProjectName(
                        new ProjectName(appliesToEntry.ProjectName)
                    );

                    resourceSchemaNode = projectSchema?.FindResourceSchemaNodeByResourceName(
                        new ResourceName(appliesToEntry.ResourceName)
                    );
                }

                if (resourceSchemaNode is null)
                {
                    logger.LogWarning(
                        "ICustomResourceValidator '{ValidatorType}' AppliesTo entry ProjectName "
                            + "'{ProjectName}', ResourceName '{ResourceName}' matches no resource in the "
                            + "effective ApiSchema and will never run. Expected for an extension resource "
                            + "this deployment lacks; otherwise check for a typo or case mismatch, since "
                            + "matching is exact and ordinal",
                        validatorTypeName,
                        sanitizedProjectName,
                        sanitizedResourceName
                    );
                }
            }
        }

        logger.LogInformation(
            "Custom validator registration guard audited and activated {ValidatorCount} "
                + "ICustomResourceValidator registration(s)",
            resolvedValidators.Count
        );
    }

    /// <summary>
    /// Whether a name can be interpolated into the quoted bracket selector the schema lookups build
    /// and still mean only itself. Deliberately narrower than LoggingSanitizer's allowlist, which
    /// permits a backslash for Windows file paths, and a name ending in one escapes the closing
    /// quote of the selector and leaves the query unterminated. Letters, digits, dash and underscore
    /// cover every name a lookup could match: across Data Standard 5.2 all 349 resourceName values
    /// are alphanumeric and the only projectName is "Ed-Fi". An unsafe name is reported as a miss,
    /// which it is, rather than failing startup.
    /// </summary>
    private static bool IsLookupSafeName(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        foreach (char character in name)
        {
            if (!char.IsLetterOrDigit(character) && character is not ('-' or '_'))
            {
                return false;
            }
        }

        return true;
    }
}
