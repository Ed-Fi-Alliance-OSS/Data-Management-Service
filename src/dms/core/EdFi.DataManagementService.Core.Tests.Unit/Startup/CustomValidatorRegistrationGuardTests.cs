// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.Startup;
using EdFi.DataManagementService.Core.Tests.Unit.TestSupport;
using EdFi.DataManagementService.CustomValidation;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Startup;

/// <summary>
/// Direct tests for CustomValidatorRegistrationGuard. The guard reads descriptors off a
/// closure-captured IServiceCollection and resolves from a built provider, both of which a test can
/// supply itself, so these cases construct the guard rather than booting a host. The frontend test
/// project keeps the small number of cases that genuinely need a real host: that the guard is
/// registered once and runs at its Order during a boot, that the closure sees a registration made
/// after the extension call, and that an abort reaches the process-exit signal.
/// </summary>
[TestFixture]
public class Given_A_Custom_Validator_Registration_Guard
{
    private const string CoreProjectName = "Ed-Fi";
    private const string CoreResourceName = "School";
    private const string ExtensionProjectName = "TPDM";
    private const string ExtensionResourceName = "Candidate";

    /// <summary>
    /// A schema carrying a core project and an extension project. The extension project matters
    /// because ApiSchemaDocuments.FindProjectSchemaForProjectName early-returns for the core project,
    /// so a fixture that only ever names the core project never reaches the extension search that the
    /// unmatched-entry warning's own text is written for.
    /// </summary>
    private static ApiSchemaDocuments SchemaDocuments() =>
        new ApiSchemaBuilder()
            .WithStartProject(CoreProjectName)
            .WithStartResource(CoreResourceName)
            .WithEndResource()
            .WithEndProject()
            .WithStartProject(ExtensionProjectName)
            .WithStartResource(ExtensionResourceName)
            .WithEndResource()
            .WithEndProject()
            .ToApiSchemaDocuments();

    private sealed record GuardRun(
        Exception? Thrown,
        IReadOnlyList<LogRecord> Records,
        IReadOnlyList<string> Warnings
    );

    /// <summary>
    /// Runs the guard over a collection the caller configures, returning whatever it threw and every
    /// record it logged. Nothing is asserted here: an abort and a clean run are both ordinary
    /// outcomes that different tests below assert on.
    /// </summary>
    private static async Task<GuardRun> RunGuard(Action<IServiceCollection> configureValidators)
    {
        ServiceCollection services = [];
        configureValidators(services);

        await using ServiceProvider rootProvider = services.BuildServiceProvider();

        var schemaProvider = A.Fake<IEffectiveApiSchemaProvider>();
        A.CallTo(() => schemaProvider.Documents).Returns(SchemaDocuments());

        RecordingLogger<CustomValidatorRegistrationGuard> logger = new();

        var guard = new CustomValidatorRegistrationGuard(services, rootProvider, schemaProvider, logger);

        Exception? thrown = null;
        try
        {
            await guard.ExecuteAsync(CancellationToken.None);
        }
        catch (Exception exception)
        {
            thrown = exception;
        }

        IReadOnlyList<LogRecord> records = logger.Records;
        return new GuardRun(
            thrown,
            records,
            [.. records.Where(record => record.Level == LogLevel.Warning).Select(record => record.Message)]
        );
    }

    private static string MissWarning(Type validatorType, string projectName, string resourceName) =>
        $"ICustomResourceValidator '{validatorType.FullName}' AppliesTo entry ProjectName "
        + $"'{projectName}', ResourceName '{resourceName}' matches no resource in the effective "
        + "ApiSchema and will never run. Expected for an extension resource this deployment lacks; "
        + "otherwise check for a typo or case mismatch, since matching is exact and ordinal";

    // ---------------------------------------------------------------- descriptor audit

    [Test]
    public async Task It_accepts_a_transient_implementation_type_descriptor()
    {
        GuardRun run = await RunGuard(services =>
            services.TryAddEnumerable(
                ServiceDescriptor.Transient<ICustomResourceValidator, MatchingValidator>()
            )
        );

        run.Thrown.Should().BeNull();
        run.Warnings.Should().BeEmpty();
        run.Records.Should()
            .Contain(record =>
                record.Message
                == "Custom validator registration guard audited and activated 1 ICustomResourceValidator registration(s)"
            );
    }

    [Test]
    public async Task It_aborts_for_a_non_transient_registration()
    {
        GuardRun run = await RunGuard(services =>
            services.Add(ServiceDescriptor.Scoped<ICustomResourceValidator, MatchingValidator>())
        );

        run.Thrown.Should()
            .BeOfType<InvalidOperationException>()
            .Which.Message.Should()
            .Contain("Startup aborted: 1 ICustomResourceValidator registration(s) are invalid.")
            .And.Contain($"'{typeof(MatchingValidator).FullName}':")
            .And.Contain("lifetime is Scoped")
            .And.NotContain("ImplementationInstance is set")
            .And.NotContain("ImplementationFactory delegate");
    }

    [Test]
    public async Task It_aborts_for_an_implementation_instance_descriptor()
    {
        MatchingValidator instance = new();

        GuardRun run = await RunGuard(services =>
            services.Add(ServiceDescriptor.Singleton<ICustomResourceValidator>(instance))
        );

        run.Thrown.Should()
            .BeOfType<InvalidOperationException>()
            .Which.Message.Should()
            .Contain($"'{typeof(MatchingValidator).FullName}':")
            .And.Contain("registered as a shared instance");
    }

    [Test]
    public async Task It_aborts_for_a_factory_descriptor()
    {
        GuardRun run = await RunGuard(services =>
            services.Add(ServiceDescriptor.Transient<ICustomResourceValidator>(_ => new MatchingValidator()))
        );

        run.Thrown.Should()
            .BeOfType<InvalidOperationException>()
            .Which.Message.Should()
            .Contain("<factory-based registration with no implementation type>")
            .And.Contain("ImplementationFactory delegate")
            .And.NotContain("lifetime is");
    }

    /// <summary>
    /// A keyed descriptor carries the contract as its ServiceType so it reaches the audit, but every
    /// unkeyed Implementation accessor on one returns null instead of throwing, so it breaks no other
    /// rule, and the unkeyed resolution DMS performs never yields it. Unrejected it would be audited
    /// clean, skip the activation probe, never be AppliesTo-checked, and never run.
    /// </summary>
    [Test]
    public async Task It_aborts_for_a_keyed_registration()
    {
        GuardRun run = await RunGuard(services =>
            services.AddKeyedTransient<ICustomResourceValidator, MatchingValidator>("plugin-key")
        );

        run.Thrown.Should()
            .BeOfType<InvalidOperationException>()
            .Which.Message.Should()
            .Contain($"'{typeof(MatchingValidator).FullName}':")
            .And.Contain("registered as a keyed service")
            .And.NotContain("<factory-based registration with no implementation type>");
    }

    /// <summary>
    /// An explicit IEnumerable&lt;ICustomResourceValidator&gt; registration supersedes the collection
    /// MS DI would otherwise synthesize from the individual descriptors. Unrejected, the validators
    /// it supplies are the ones that actually resolve while never being lifetime-audited, and every
    /// correctly registered validator is silently dropped.
    /// </summary>
    [Test]
    public async Task It_aborts_for_a_validator_collection_registration()
    {
        GuardRun run = await RunGuard(services =>
            services.AddSingleton<IEnumerable<ICustomResourceValidator>>(
                new ICustomResourceValidator[] { new MatchingValidator() }
            )
        );

        run.Thrown.Should()
            .BeOfType<InvalidOperationException>()
            .Which.Message.Should()
            .Contain("Startup aborted: 1 ICustomResourceValidator registration(s) are invalid.")
            .And.Contain("IEnumerable<ICustomResourceValidator>");
    }

    /// <summary>
    /// The displacement half of the same defect: a correctly registered validator coexisting with a
    /// collection registration must still abort, because otherwise the correct one stops resolving
    /// while the guard reports success.
    /// </summary>
    [Test]
    public async Task It_aborts_for_a_collection_registration_that_hides_a_valid_one()
    {
        GuardRun run = await RunGuard(services =>
        {
            services.TryAddEnumerable(
                ServiceDescriptor.Transient<ICustomResourceValidator, MatchingValidator>()
            );
            services.AddSingleton<IEnumerable<ICustomResourceValidator>>(
                new ICustomResourceValidator[] { new MatchingValidator() }
            );
        });

        run.Thrown.Should()
            .BeOfType<InvalidOperationException>()
            .Which.Message.Should()
            .Contain("IEnumerable<ICustomResourceValidator>");
    }

    [Test]
    public async Task It_aggregates_every_invalid_registration_into_one_message()
    {
        GuardRun run = await RunGuard(services =>
        {
            services.Add(ServiceDescriptor.Singleton<ICustomResourceValidator>(new MatchingValidator()));
            services.Add(ServiceDescriptor.Scoped<ICustomResourceValidator, NonexistentResourceValidator>());
        });

        run.Thrown.Should()
            .BeOfType<InvalidOperationException>()
            .Which.Message.Should()
            .Contain("Startup aborted: 2 ICustomResourceValidator registration(s) are invalid.")
            .And.Contain($"'{typeof(MatchingValidator).FullName}':")
            .And.Contain($"'{typeof(NonexistentResourceValidator).FullName}':");
    }

    // ---------------------------------------------------------------- activation probe

    [Test]
    public async Task It_aborts_for_an_unconstructible_validator()
    {
        GuardRun run = await RunGuard(services =>
            services.TryAddEnumerable(
                ServiceDescriptor.Transient<ICustomResourceValidator, UnconstructibleValidator>()
            )
        );

        // Asserts on MS DI's own attribution. For a dependency one hop from the validator the
        // framework message names both the unresolvable service and the type being activated, so a
        // per-type re-activation pass would only reproduce this same string.
        run.Thrown.Should()
            .BeOfType<InvalidOperationException>()
            .Which.Message.Should()
            .Contain(
                "Startup aborted: resolving the registered ICustomResourceValidator instances from a "
                    + "throwaway scope failed"
            )
            .And.Contain(nameof(UnconstructibleValidator))
            .And.Contain(nameof(IServiceNobodyRegisters));
    }

    /// <summary>
    /// The probe must dispose its throwaway scope asynchronously. A validator implementing only
    /// IAsyncDisposable is tracked by that scope, and a synchronous dispose over one throws from
    /// outside the probe's try/catch. Asserting the validator was actually disposed is what
    /// distinguishes the fix from simply leaking the scope, which would also avoid the throw.
    /// </summary>
    [Test]
    public async Task It_disposes_an_async_only_disposable_validator_without_aborting()
    {
        AsyncDisposableValidator.DisposeAsyncCallCount = 0;

        GuardRun run = await RunGuard(services =>
            services.TryAddEnumerable(
                ServiceDescriptor.Transient<ICustomResourceValidator, AsyncDisposableValidator>()
            )
        );

        run.Thrown.Should().BeNull();
        AsyncDisposableValidator.DisposeAsyncCallCount.Should().Be(1);
    }

    // ---------------------------------------------------------------- AppliesTo

    [Test]
    public async Task It_logs_a_matching_applies_to_entry_without_warning()
    {
        GuardRun run = await RunGuard(services =>
            services.TryAddEnumerable(
                ServiceDescriptor.Transient<ICustomResourceValidator, MatchingValidator>()
            )
        );

        run.Warnings.Should().BeEmpty();
        run.Records.Should()
            .Contain(record =>
                record.Message
                == $"ICustomResourceValidator '{typeof(MatchingValidator).FullName}' AppliesTo entry: "
                    + $"ProjectName '{CoreProjectName}', ResourceName '{CoreResourceName}'"
            );
    }

    /// <summary>
    /// The extension-project path: FindProjectSchemaForProjectName early-returns for the core
    /// project, so only an extension project name reaches the search over extension schemas.
    /// </summary>
    [Test]
    public async Task It_logs_a_matching_extension_applies_to_entry_without_warning()
    {
        GuardRun run = await RunGuard(services =>
            services.TryAddEnumerable(
                ServiceDescriptor.Transient<ICustomResourceValidator, ExtensionMatchingValidator>()
            )
        );

        run.Thrown.Should().BeNull();
        run.Warnings.Should().BeEmpty();
    }

    [Test]
    public async Task It_warns_for_an_entry_matching_no_resource()
    {
        GuardRun run = await RunGuard(services =>
            services.TryAddEnumerable(
                ServiceDescriptor.Transient<ICustomResourceValidator, NonexistentResourceValidator>()
            )
        );

        run.Thrown.Should().BeNull();
        run.Warnings.Should()
            .ContainSingle()
            .Which.Should()
            .Be(
                MissWarning(
                    typeof(NonexistentResourceValidator),
                    CoreProjectName,
                    "ThisResourceExistsInNoProjectSchema"
                )
            );
    }

    [Test]
    public async Task It_warns_for_a_wrong_cased_entry()
    {
        GuardRun run = await RunGuard(services =>
            services.TryAddEnumerable(
                ServiceDescriptor.Transient<ICustomResourceValidator, WrongCasedValidator>()
            )
        );

        run.Warnings.Should()
            .ContainSingle()
            .Which.Should()
            .Be(
                MissWarning(typeof(WrongCasedValidator), CoreProjectName, CoreResourceName.ToUpperInvariant())
            );
    }

    /// <summary>
    /// A name carrying a character no resource name can contain must not be described as a typo or a
    /// case mismatch. The sanitizer strips such characters rather than replacing them, so the name
    /// shown in the record can be a real resource name: reporting "ResourceName 'School' matches no
    /// resource" for the entry "Scho{BEL}ol" would name a resource that demonstrably does exist.
    /// </summary>
    [Test]
    public async Task It_warns_distinctly_for_an_entry_whose_name_no_resource_can_contain()
    {
        GuardRun run = await RunGuard(services =>
            services.TryAddEnumerable(
                ServiceDescriptor.Transient<
                    ICustomResourceValidator,
                    ControlCharacterInsideRealNameValidator
                >()
            )
        );

        run.Thrown.Should().BeNull();

        string warning = run.Warnings.Should().ContainSingle().Subject;
        warning.Should().Contain("carries a character no resource name can contain");
        warning.Should().NotContain("check for a typo or case mismatch");
        warning.Should().NotContain("");
    }

    /// <summary>
    /// The injection shape: a double quote closes the interpolated bracket selector and opens a
    /// second one naming a real resource, so the query parses and matches. The entry must still be
    /// reported as matching nothing, because request-time matching is exact and ordinal.
    /// </summary>
    [Test]
    public async Task It_warns_for_an_entry_smuggling_a_second_path_selector()
    {
        GuardRun run = await RunGuard(services =>
            services.TryAddEnumerable(
                ServiceDescriptor.Transient<ICustomResourceValidator, InjectedSelectorValidator>()
            )
        );

        run.Thrown.Should().BeNull();
        run.Warnings.Should()
            .ContainSingle()
            .Which.Should()
            .Contain("carries a character no resource name can contain");
    }

    /// <summary>
    /// The sanitizer's allowlist permits a backslash for Windows paths, so "is this safe to log" is
    /// not the same question as "is this safe to interpolate into a bracket selector": a trailing
    /// backslash escapes the closing quote and leaves the query unparseable.
    /// </summary>
    [Test]
    public async Task It_warns_for_an_entry_the_lookup_path_cannot_hold()
    {
        GuardRun run = await RunGuard(services =>
            services.TryAddEnumerable(
                ServiceDescriptor.Transient<ICustomResourceValidator, BackslashValidator>()
            )
        );

        run.Thrown.Should().BeNull();
        run.Warnings.Should()
            .ContainSingle()
            .Which.Should()
            .Contain("carries a character no resource name can contain");
    }

    /// <summary>
    /// A project name is compared by ordinal equality rather than through an interpolated path, so it
    /// carries no injection or parse hazard and must not be rejected by the resource-name rule. It is
    /// simply looked up and missed.
    /// </summary>
    [Test]
    public async Task It_looks_up_a_project_name_the_resource_name_rule_would_reject()
    {
        GuardRun run = await RunGuard(services =>
            services.TryAddEnumerable(
                ServiceDescriptor.Transient<ICustomResourceValidator, SpacedProjectNameValidator>()
            )
        );

        run.Thrown.Should().BeNull();
        run.Warnings.Should()
            .ContainSingle()
            .Which.Should()
            .Be(MissWarning(typeof(SpacedProjectNameValidator), "My Project", CoreResourceName));
    }

    [Test]
    public async Task It_warns_for_a_validator_declaring_no_entries()
    {
        GuardRun run = await RunGuard(services =>
            services.TryAddEnumerable(
                ServiceDescriptor.Transient<ICustomResourceValidator, EmptyAppliesToValidator>()
            )
        );

        run.Thrown.Should().BeNull();
        run.Warnings.Should()
            .ContainSingle()
            .Which.Should()
            .Be(
                $"ICustomResourceValidator '{typeof(EmptyAppliesToValidator).FullName}' declares no "
                    + "AppliesTo entries, so it can never run for any resource"
            );
    }

    [Test]
    public async Task It_warns_for_a_validator_whose_applies_to_is_null()
    {
        GuardRun run = await RunGuard(services =>
            services.TryAddEnumerable(
                ServiceDescriptor.Transient<ICustomResourceValidator, NullAppliesToValidator>()
            )
        );

        run.Thrown.Should().BeNull();
        run.Warnings.Should()
            .ContainSingle()
            .Which.Should()
            .Be(
                $"ICustomResourceValidator '{typeof(NullAppliesToValidator).FullName}' declares no "
                    + "AppliesTo entries, so it can never run for any resource"
            );
    }

    /// <summary>
    /// A null element among real ones. The null-list guard exists so the operator never gets a bare
    /// NullReferenceException naming no registration; dereferencing an element is the same hazard one
    /// level down, and the good entry beside it must still be processed.
    /// </summary>
    [Test]
    public async Task It_warns_for_a_null_applies_to_entry_and_still_processes_the_others()
    {
        GuardRun run = await RunGuard(services =>
            services.TryAddEnumerable(
                ServiceDescriptor.Transient<ICustomResourceValidator, NullEntryAppliesToValidator>()
            )
        );

        run.Thrown.Should().BeNull();
        run.Warnings.Should()
            .ContainSingle()
            .Which.Should()
            .Be(
                $"ICustomResourceValidator '{typeof(NullEntryAppliesToValidator).FullName}' declares a "
                    + "null AppliesTo entry, which names no resource and will never run"
            );

        // The real entry beside the null one still reached the matching path.
        run.Records.Should()
            .Contain(record =>
                record.Message
                == $"ICustomResourceValidator '{typeof(NullEntryAppliesToValidator).FullName}' AppliesTo "
                    + $"entry: ProjectName '{CoreProjectName}', ResourceName '{CoreResourceName}'"
            );
    }

    /// <summary>
    /// AppliesTo is implementer code, so the getter itself can throw. That must not take the process
    /// down with an exception naming no registration.
    /// </summary>
    [Test]
    public async Task It_warns_for_a_validator_whose_applies_to_getter_throws()
    {
        GuardRun run = await RunGuard(services =>
            services.TryAddEnumerable(
                ServiceDescriptor.Transient<ICustomResourceValidator, ThrowingAppliesToValidator>()
            )
        );

        run.Thrown.Should().BeNull();
        run.Warnings.Should()
            .ContainSingle()
            .Which.Should()
            .Contain($"'{typeof(ThrowingAppliesToValidator).FullName}'")
            .And.Contain("AppliesTo could not be read");
    }

    [Test]
    public async Task It_reports_the_audited_count_for_a_set_of_validators()
    {
        GuardRun run = await RunGuard(services =>
        {
            services.TryAddEnumerable(
                ServiceDescriptor.Transient<ICustomResourceValidator, MatchingValidator>()
            );
            services.TryAddEnumerable(
                ServiceDescriptor.Transient<ICustomResourceValidator, NonexistentResourceValidator>()
            );
            services.TryAddEnumerable(
                ServiceDescriptor.Transient<ICustomResourceValidator, WrongCasedValidator>()
            );
        });

        run.Thrown.Should().BeNull();
        run.Warnings.Should().HaveCount(2);
        run.Records.Should()
            .Contain(record =>
                record.Message
                == "Custom validator registration guard audited and activated 3 ICustomResourceValidator registration(s)"
            );
    }

    [Test]
    public async Task It_boots_with_no_validators_registered()
    {
        GuardRun run = await RunGuard(_ => { });

        run.Thrown.Should().BeNull();
        run.Warnings.Should().BeEmpty();
        run.Records.Should()
            .Contain(record =>
                record.Message
                == "Custom validator registration guard audited and activated 0 ICustomResourceValidator registration(s)"
            );
    }

    // ---------------------------------------------------------------- fixtures

    private interface IServiceNobodyRegisters { }

    private abstract class ValidatorBase : ICustomResourceValidator
    {
        public abstract IReadOnlyList<ValidatedResource> AppliesTo { get; }

        public Task<IReadOnlyList<CustomValidationFailure>> ValidateAsync(
            JsonNode document,
            ValidatedResourceInfo resource,
            CustomValidationOperation operation,
            ValidationScope scope,
            string traceId,
            CancellationToken cancellationToken
        ) => Task.FromResult<IReadOnlyList<CustomValidationFailure>>([]);
    }

    private sealed class MatchingValidator : ValidatorBase
    {
        public override IReadOnlyList<ValidatedResource> AppliesTo =>
            [new ValidatedResource(CoreProjectName, CoreResourceName)];
    }

    private sealed class ExtensionMatchingValidator : ValidatorBase
    {
        public override IReadOnlyList<ValidatedResource> AppliesTo =>
            [new ValidatedResource(ExtensionProjectName, ExtensionResourceName)];
    }

    private sealed class NonexistentResourceValidator : ValidatorBase
    {
        public override IReadOnlyList<ValidatedResource> AppliesTo =>
            [new ValidatedResource(CoreProjectName, "ThisResourceExistsInNoProjectSchema")];
    }

    private sealed class WrongCasedValidator : ValidatorBase
    {
        public override IReadOnlyList<ValidatedResource> AppliesTo =>
            [new ValidatedResource(CoreProjectName, CoreResourceName.ToUpperInvariant())];
    }

    /// <summary>
    /// A control character embedded inside an otherwise real resource name, so that stripping it for
    /// logging yields a name that genuinely exists in the schema. That is what makes a "check for a
    /// typo" diagnosis self-contradictory.
    /// </summary>
    private sealed class ControlCharacterInsideRealNameValidator : ValidatorBase
    {
        public override IReadOnlyList<ValidatedResource> AppliesTo =>
            [new ValidatedResource(CoreProjectName, "School")];
    }

    private sealed class InjectedSelectorValidator : ValidatorBase
    {
        public override IReadOnlyList<ValidatedResource> AppliesTo =>
            [new ValidatedResource(CoreProjectName, $"Missing\",\"{CoreResourceName}")];
    }

    private sealed class BackslashValidator : ValidatorBase
    {
        public override IReadOnlyList<ValidatedResource> AppliesTo =>
            [new ValidatedResource(CoreProjectName, CoreResourceName + "\\")];
    }

    private sealed class SpacedProjectNameValidator : ValidatorBase
    {
        public override IReadOnlyList<ValidatedResource> AppliesTo =>
            [new ValidatedResource("My Project", CoreResourceName)];
    }

    private sealed class EmptyAppliesToValidator : ValidatorBase
    {
        public override IReadOnlyList<ValidatedResource> AppliesTo => [];
    }

    private sealed class NullAppliesToValidator : ValidatorBase
    {
        public override IReadOnlyList<ValidatedResource> AppliesTo => null!;
    }

    private sealed class NullEntryAppliesToValidator : ValidatorBase
    {
        public override IReadOnlyList<ValidatedResource> AppliesTo =>
            [null!, new ValidatedResource(CoreProjectName, CoreResourceName)];
    }

    private sealed class ThrowingAppliesToValidator : ValidatorBase
    {
        public override IReadOnlyList<ValidatedResource> AppliesTo =>
            throw new InvalidOperationException("implementer bug reading AppliesTo");
    }

    private sealed class UnconstructibleValidator(IServiceNobodyRegisters dependency) : ValidatorBase
    {
        public IServiceNobodyRegisters Dependency { get; } = dependency;

        public override IReadOnlyList<ValidatedResource> AppliesTo => [];
    }

    private sealed class AsyncDisposableValidator : ValidatorBase, IAsyncDisposable
    {
        internal static int DisposeAsyncCallCount;

        public override IReadOnlyList<ValidatedResource> AppliesTo =>
            [new ValidatedResource(CoreProjectName, CoreResourceName)];

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref DisposeAsyncCallCount);
            return ValueTask.CompletedTask;
        }
    }
}
