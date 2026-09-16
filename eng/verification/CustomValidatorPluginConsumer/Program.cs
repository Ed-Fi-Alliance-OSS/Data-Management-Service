// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

// The assertions. Compiling the samples proves the two packed contracts carry the dependency
// closure their signatures need; running these proves the samples do what the readme says they do.
//
// Everything below goes through the real ContributeServices call and the real ValidateAsync call.
// Nothing asserts about the source text.

using System.Text.Json.Nodes;
using Acme.Dms.StudentIdentity;
using EdFi.Api.Plugins;
using EdFi.DataManagementService.CustomValidation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CustomValidatorPluginConsumer;

internal static class Program
{
    private const string PluginName = "Acme.Dms.StudentIdentity";
    private const string ConfigurationSection = "StudentIdentity";
    private const string DefaultPrefix = "S";
    private const string ConfiguredPrefix = "SEA-";

    internal static async Task<int> Main()
    {
        AssertContractTypes();
        List<ServiceDescriptor> descriptors = AssertRegistrationShape();
        AssertBothOptionsFormsConfigure(descriptors);
        await AssertSampleRuleBehavior();

        Console.WriteLine(
            "Verified the documented samples compile against the two packed contracts, register "
                + "the validator in the shape DMS's startup guard accepts, configure their options "
                + "through both the Action<TOptions> and the section-binding form, and enforce the "
                + "rule the readme describes."
        );

        return 0;
    }

    /// <summary>
    /// The relationships the readme claims of each sample type.
    /// </summary>
    private static void AssertContractTypes()
    {
        Assert(
            typeof(ICustomResourceValidator).IsAssignableFrom(typeof(StudentIdentityValidator)),
            $"{nameof(StudentIdentityValidator)} does not implement {nameof(ICustomResourceValidator)}."
        );

        Assert(
            typeof(StudentIdentityPlugin).IsSubclassOf(typeof(EdFiApiPlugin)),
            $"{nameof(StudentIdentityPlugin)} does not derive from {nameof(EdFiApiPlugin)}."
        );

        Assert(
            typeof(StudentIdentityPlugin).IsPublic && !typeof(StudentIdentityPlugin).IsAbstract,
            $"{nameof(StudentIdentityPlugin)} must be public and non-abstract for a host to discover it."
        );

        Assert(
            string.Equals(new StudentIdentityPlugin().Name, PluginName, StringComparison.Ordinal),
            $"The sample plugin's Name is not '{PluginName}', which is the directory name the readme "
                + "tells an implementer to publish into and allowlist."
        );
    }

    /// <summary>
    /// Invokes the real contribution hook and holds its output to the registration rules the
    /// readme states: one unkeyed, transient, implementation-type registration of the contract.
    /// </summary>
    private static List<ServiceDescriptor> AssertRegistrationShape()
    {
        FixtureServiceCollection services = [];

        new StudentIdentityPlugin().ContributeServices(
            services,
            new FixtureConfiguration(
                ConfigurationSection,
                new Dictionary<string, string> { ["RequiredPrefix"] = ConfiguredPrefix }
            )
        );

        List<ServiceDescriptor> descriptors = [.. services];

        List<ServiceDescriptor> validatorDescriptors =
        [
            .. descriptors.Where(descriptor => descriptor.ServiceType == typeof(ICustomResourceValidator)),
        ];

        Assert(
            validatorDescriptors.Count == 1,
            $"Expected exactly one {nameof(ICustomResourceValidator)} registration, found "
                + $"{validatorDescriptors.Count}."
        );

        ServiceDescriptor validator = validatorDescriptors[0];

        // The four properties the startup guard reads. A failure on any of them is a failure the
        // guard would raise as a startup abort, so asserting them here is asserting the readme's
        // registration rules against the registration the readme publishes.
        Assert(!validator.IsKeyedService, "The validator registration is keyed; it must be unkeyed.");
        Assert(
            validator.Lifetime == ServiceLifetime.Transient,
            $"The validator registration's lifetime is {validator.Lifetime}; it must be Transient."
        );
        Assert(
            validator.ImplementationType == typeof(StudentIdentityValidator),
            "The validator registration does not name StudentIdentityValidator as its implementation "
                + "type."
        );
        Assert(
            validator.ImplementationInstance is null,
            "The validator registration supplies a shared instance, which the startup guard rejects."
        );
        Assert(
            validator.ImplementationFactory is null,
            "The validator registration supplies a factory delegate, which the startup guard rejects."
        );

        return descriptors;
    }

    /// <summary>
    /// Both options forms, invoked rather than inspected. The framework's options machinery runs
    /// every registered <see cref="IConfigureOptions{TOptions}"/> in registration order, so running
    /// them in that order against a fresh instance is what the framework itself would do.
    /// </summary>
    private static void AssertBothOptionsFormsConfigure(List<ServiceDescriptor> descriptors)
    {
        List<IConfigureOptions<StudentIdentityOptions>> configurers =
        [
            .. descriptors
                .Where(descriptor =>
                    descriptor.ServiceType == typeof(IConfigureOptions<StudentIdentityOptions>)
                )
                .Select(descriptor =>
                    descriptor.ImplementationInstance as IConfigureOptions<StudentIdentityOptions>
                    ?? throw new InvalidOperationException(
                        "An IConfigureOptions<StudentIdentityOptions> registration carries no instance, "
                            + "so the sample's Configure call is not the shape this check assumed."
                    )
                ),
        ];

        Assert(
            configurers.Count == 2,
            "Expected the sample to register two IConfigureOptions<StudentIdentityOptions>, one per "
                + $"documented form, found {configurers.Count}."
        );

        // The Action<TOptions> form alone. This is what a deployment that configures nothing gets.
        StudentIdentityOptions fromActionOnly = new();
        configurers[0].Configure(fromActionOnly);
        Assert(
            string.Equals(fromActionOnly.RequiredPrefix, DefaultPrefix, StringComparison.Ordinal),
            $"The Action<TOptions> form did not set RequiredPrefix to '{DefaultPrefix}'; it is "
                + $"'{fromActionOnly.RequiredPrefix}'."
        );

        // Both forms, in registration order. The section supplies a value, so it overrides the
        // default, which is the behavior the sample's comment claims.
        StudentIdentityOptions fromBoth = new();
        foreach (IConfigureOptions<StudentIdentityOptions> configurer in configurers)
        {
            configurer.Configure(fromBoth);
        }
        Assert(
            string.Equals(fromBoth.RequiredPrefix, ConfiguredPrefix, StringComparison.Ordinal),
            $"The section-binding form did not override RequiredPrefix with '{ConfiguredPrefix}'; it "
                + $"is '{fromBoth.RequiredPrefix}'."
        );

        // The same pair against a configuration carrying no such section. The default has to
        // survive, or the "sets a default, then lets a deployment override it" claim is backwards.
        FixtureServiceCollection unconfiguredServices = [];
        new StudentIdentityPlugin().ContributeServices(
            unconfiguredServices,
            new FixtureConfiguration(ConfigurationSection, new Dictionary<string, string>())
        );

        StudentIdentityOptions fromEmptySection = new();
        foreach (
            ServiceDescriptor descriptor in unconfiguredServices.Where(descriptor =>
                descriptor.ServiceType == typeof(IConfigureOptions<StudentIdentityOptions>)
            )
        )
        {
            ((IConfigureOptions<StudentIdentityOptions>)descriptor.ImplementationInstance!).Configure(
                fromEmptySection
            );
        }
        Assert(
            string.Equals(fromEmptySection.RequiredPrefix, DefaultPrefix, StringComparison.Ordinal),
            "An absent configuration section overwrote the Action<TOptions> default instead of "
                + $"leaving it; RequiredPrefix is '{fromEmptySection.RequiredPrefix}'."
        );
    }

    /// <summary>
    /// The rule itself, through the real <c>ValidateAsync</c>. A sample the readme presents as a
    /// working rule has to be one.
    /// </summary>
    private static async Task AssertSampleRuleBehavior()
    {
        StudentIdentityValidator validator = new(
            Options.Create(new StudentIdentityOptions { RequiredPrefix = ConfiguredPrefix })
        );

        Assert(
            validator.AppliesTo.Count == 1
                && string.Equals(validator.AppliesTo[0].ProjectName, "Ed-Fi", StringComparison.Ordinal)
                && string.Equals(validator.AppliesTo[0].ResourceName, "Student", StringComparison.Ordinal),
            "The sample validator's AppliesTo does not declare exactly Ed-Fi/Student."
        );

        JsonNode conforming = JsonNode.Parse($"{{\"studentUniqueId\":\"{ConfiguredPrefix}12345\"}}")!;
        Assert(
            (await Validate(validator, conforming)).Count == 0,
            "The sample rejected a student unique id carrying the configured prefix."
        );

        // The document must come back unchanged. Nothing in the type system enforces the
        // read-only rule the readme states, so the sample is held to it here.
        string beforeValidation = conforming.ToJsonString();
        _ = await Validate(validator, conforming);
        Assert(
            string.Equals(conforming.ToJsonString(), beforeValidation, StringComparison.Ordinal),
            "The sample mutated the document it was given."
        );

        IReadOnlyList<CustomValidationFailure> failures = await Validate(
            validator,
            JsonNode.Parse("{\"studentUniqueId\":\"OTHER-12345\"}")!
        );
        Assert(failures.Count == 1, $"Expected one failure for a non-conforming id, got {failures.Count}.");
        Assert(
            failures[0] is CustomValidationFailure.OnPath onPath
                && string.Equals(onPath.JsonPath, "$.studentUniqueId", StringComparison.Ordinal),
            "The failure is not an OnPath failure against $.studentUniqueId."
        );
        Assert(
            failures[0] is CustomValidationFailure.OnPath message
                && !message.Message.Contains("OTHER-12345", StringComparison.Ordinal),
            "The failure message quotes the submitted value back; the readme's sample must not."
        );

        // A writable profile can leave the member out of the profile-effective body entirely, and
        // the readme says the sample tolerates that rather than rejecting the write.
        Assert(
            (await Validate(validator, JsonNode.Parse("{\"studentLastName\":\"Doe\"}")!)).Count == 0,
            "The sample reported a failure for a body that does not carry studentUniqueId at all."
        );

        // Cancellation propagates rather than being reported as a validation failure.
        using CancellationTokenSource cancelled = new();
        await cancelled.CancelAsync();
        try
        {
            _ = await validator.ValidateAsync(
                JsonNode.Parse("{\"studentUniqueId\":\"OTHER-1\"}")!,
                new ValidatedResourceInfo("Ed-Fi", "Student", "5.2.0"),
                CustomValidationOperation.Upsert,
                new ValidationScope(Tenant: null, new Dictionary<string, string>()),
                "trace-cancelled",
                cancelled.Token
            );

            throw new InvalidOperationException(
                "The sample completed normally on a cancelled request instead of observing the token."
            );
        }
        catch (OperationCanceledException)
        {
            // Expected. DMS rethrows this on a request the client aborted rather than answering 500.
        }
    }

    private static Task<IReadOnlyList<CustomValidationFailure>> Validate(
        StudentIdentityValidator validator,
        JsonNode document
    ) =>
        validator.ValidateAsync(
            document,
            new ValidatedResourceInfo("Ed-Fi", "Student", "5.2.0"),
            CustomValidationOperation.Upsert,
            new ValidationScope(Tenant: null, new Dictionary<string, string>()),
            "trace-fixture",
            CancellationToken.None
        );

    private static void Assert(bool condition, string failureMessage)
    {
        if (!condition)
        {
            throw new InvalidOperationException(failureMessage);
        }
    }
}
