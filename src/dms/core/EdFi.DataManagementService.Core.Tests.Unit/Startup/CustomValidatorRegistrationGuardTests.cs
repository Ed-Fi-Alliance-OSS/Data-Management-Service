// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections;
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

    /// <summary>
    /// The guard strips control characters from type names rather than passing them through the
    /// logging sanitizer, so a nested fixture type keeps the '+' in its full name. Expected messages
    /// apply the same transformation rather than embedding a hand-copied name.
    /// </summary>
    private static string Logged(Type validatorType) =>
        string.Concat((validatorType.FullName ?? validatorType.Name).Where(c => !char.IsControl(c)));

    private static string MissWarning(Type validatorType, string projectName, string resourceName) =>
        $"ICustomResourceValidator '{Logged(validatorType)}' AppliesTo entry ProjectName "
        + $"'{projectName}', ResourceName '{resourceName}' matches no resource in the effective "
        + "ApiSchema, so it will never run. Matching is exact and ordinal. Expected for an extension "
        + "resource this deployment does not carry";

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
            .And.Contain($"'{Logged(typeof(MatchingValidator))}':")
            .And.Contain("lifetime is Scoped")
            .And.NotContain("shared instance")
            .And.NotContain("factory delegate");
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
            .Contain($"'{Logged(typeof(MatchingValidator))}':")
            .And.Contain("supplies a shared instance rather than an implementation type");
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
            .Contain("<registration with no implementation type>")
            .And.Contain("supplies a factory delegate rather than an implementation type")
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
            .Contain($"'{Logged(typeof(MatchingValidator))}':")
            .And.Contain("are not among the")
            .And.Contain(", keyed,")
            .And.NotContain("<registration with no implementation type>");
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
    /// <summary>
    /// A keyed descriptor is invisible to the unkeyed resolution DMS performs, so registering one
    /// beside a valid contract registration hides nothing: the validator still resolves. Rejecting
    /// it for being keyed would refuse to boot over a working registration.
    /// </summary>
    [Test]
    public async Task It_accepts_a_keyed_alias_beside_a_contract_registration()
    {
        GuardRun run = await RunGuard(services =>
        {
            services.TryAddEnumerable(
                ServiceDescriptor.Transient<ICustomResourceValidator, MatchingValidator>()
            );
            services.AddKeyedTransient<ICustomResourceValidator, MatchingValidator>("mine");
        });

        run.Thrown.Should().BeNull();
    }

    /// <summary>
    /// The same reasoning one level up: a keyed registration of the collection type does not
    /// displace the unkeyed collection DMS resolves.
    /// </summary>
    [Test]
    public async Task It_accepts_a_keyed_collection_registration()
    {
        GuardRun run = await RunGuard(services =>
        {
            services.TryAddEnumerable(
                ServiceDescriptor.Transient<ICustomResourceValidator, MatchingValidator>()
            );
            services.AddKeyedSingleton<IEnumerable<ICustomResourceValidator>>("k", (_, _) => []);
        });

        run.Thrown.Should().BeNull();
    }

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
            .And.Contain($"'{Logged(typeof(MatchingValidator))}':")
            .And.Contain($"'{Logged(typeof(NonexistentResourceValidator))}':");
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
                == $"ICustomResourceValidator '{Logged(typeof(MatchingValidator))}' AppliesTo entry: "
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

        // Asserting the entry record, not merely the absence of a warning: without this the test
        // would also pass if extension entries were skipped entirely rather than matched.
        run.Records.Should()
            .Contain(record =>
                record.Message
                == $"ICustomResourceValidator '{Logged(typeof(ExtensionMatchingValidator))}' AppliesTo "
                    + $"entry: ProjectName '{ExtensionProjectName}', ResourceName '{ExtensionResourceName}'"
            );
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
    public async Task It_warns_for_an_entry_whose_name_the_schema_cannot_hold()
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

        run.Warnings.Should().HaveCount(2);
        run.Warnings[0].Should().Contain("matches no resource in the effective ApiSchema");
        // The sanitizer strips the control character, so the name shown is a real resource name. A
        // second record must therefore say the shown name was altered, rather than leaving the miss
        // record to be read as a typo in a resource that does exist.
        run.Warnings[1].Should().Contain("was altered to make it safe to log");
        run.Warnings.Should().OnlyContain(warning => !warning.Contains("\u0007"));
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

        run.Warnings.Should().HaveCount(2);
        // The sanitizer drops the quote and comma, so the name shown is 'MissingSchool', which is
        // neither the declared entry nor a real resource. The alteration record is what keeps the
        // miss record from being read as naming the entry in source.
        run.Warnings[0].Should().Contain("ResourceName 'MissingSchool' matches no resource");
        run.Warnings[1].Should().Contain("was altered to make it safe to log");
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

        // LogSanitizer's allowlist admits the backslash, so the name reaches the log unaltered and
        // no alteration record is emitted. The shape gate is what keeps it away from the lookup,
        // whose selector a trailing backslash would make unparseable.
        string warning = run.Warnings.Should().ContainSingle().Subject;
        warning.Should().Contain(@"ResourceName 'School\' matches no resource");
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
                $"ICustomResourceValidator '{Logged(typeof(EmptyAppliesToValidator))}' declares no "
                    + "AppliesTo entries, so it can never run for any resource"
            );
    }

    /// <summary>
    /// design.md "Versioning and Compatibility" relies on reading AppliesTo at startup to surface a
    /// validator compiled against a different version of the contract, which package resolution
    /// unifies without failing the restore. Warning instead of aborting would let exactly that
    /// deployment serve traffic, so a contract violation here is fatal.
    /// </summary>
    [Test]
    public async Task It_aborts_for_a_validator_whose_applies_to_is_null()
    {
        GuardRun run = await RunGuard(services =>
            services.TryAddEnumerable(
                ServiceDescriptor.Transient<ICustomResourceValidator, NullAppliesToValidator>()
            )
        );

        run.Thrown.Should()
            .BeOfType<InvalidOperationException>()
            .Which.Message.Should()
            .Contain("cannot be used")
            .And.Contain($"'{Logged(typeof(NullAppliesToValidator))}'")
            .And.Contain("AppliesTo returned null");
    }

    /// <summary>
    /// The property returns, and the list it returned throws when walked. Enumeration sits inside
    /// the same try as the getter, so this is reported against the validator rather than escaping
    /// to a startup message that names no registration.
    /// </summary>
    [Test]
    public async Task It_aborts_for_a_validator_whose_applies_to_list_throws_when_walked()
    {
        GuardRun run = await RunGuard(services =>
            services.TryAddEnumerable(
                ServiceDescriptor.Transient<ICustomResourceValidator, ThrowingEnumerationValidator>()
            )
        );

        run.Thrown.Should()
            .BeOfType<InvalidOperationException>()
            .Which.Message.Should()
            .Contain($"'{Logged(typeof(ThrowingEnumerationValidator))}'")
            .And.Contain("reading or walking AppliesTo threw");
    }

    /// <summary>
    /// A null element among real ones. The null-list guard exists so the operator never gets a bare
    /// NullReferenceException naming no registration, and dereferencing an element is the same hazard
    /// one level down. The validator is reported as unusable as a whole, so the real entry beside the
    /// null is not inspected.
    /// </summary>
    [Test]
    public async Task It_aborts_for_a_null_applies_to_entry()
    {
        GuardRun run = await RunGuard(services =>
            services.TryAddEnumerable(
                ServiceDescriptor.Transient<ICustomResourceValidator, NullEntryAppliesToValidator>()
            )
        );

        run.Thrown.Should()
            .BeOfType<InvalidOperationException>()
            .Which.Message.Should()
            .Contain($"'{Logged(typeof(NullEntryAppliesToValidator))}'")
            .And.Contain("AppliesTo contains a null entry");
    }

    /// <summary>
    /// AppliesTo is implementer code, so the getter itself can throw. That must not take the process
    /// down with an exception naming no registration.
    /// </summary>
    [Test]
    public async Task It_aborts_for_a_validator_whose_applies_to_getter_throws()
    {
        GuardRun run = await RunGuard(services =>
            services.TryAddEnumerable(
                ServiceDescriptor.Transient<ICustomResourceValidator, ThrowingAppliesToValidator>()
            )
        );

        run.Thrown.Should()
            .BeOfType<InvalidOperationException>()
            .Which.Message.Should()
            .Contain("cannot be used")
            .And.Contain($"'{Logged(typeof(ThrowingAppliesToValidator))}'")
            .And.Contain("reading or walking AppliesTo threw");
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

    /// <summary>
    /// Registering a validator only under a derived interface leaves it unreachable: DMS resolves
    /// the contract and nothing else, so the guard would otherwise report "activated 0" and startup
    /// would succeed with the validator never running.
    /// </summary>
    [Test]
    public async Task It_aborts_for_a_validator_registered_only_under_a_derived_interface()
    {
        GuardRun run = await RunGuard(services =>
            services.TryAddEnumerable(
                ServiceDescriptor.Transient<IImplementerOwnValidator, DerivedInterfaceValidator>()
            )
        );

        run.Thrown.Should()
            .BeOfType<InvalidOperationException>()
            .Which.Message.Should()
            .Contain($"'{Logged(typeof(DerivedInterfaceValidator))}'")
            .And.Contain("are not among the");
    }

    [Test]
    public async Task It_aborts_for_a_validator_registered_only_under_its_concrete_type()
    {
        GuardRun run = await RunGuard(services => services.AddTransient<MatchingValidator>());

        run.Thrown.Should()
            .BeOfType<InvalidOperationException>()
            .Which.Message.Should()
            .Contain($"'{Logged(typeof(MatchingValidator))}'")
            .And.Contain("are not among the");
    }

    /// <summary>
    /// The factory forms of the two rules above. A factory descriptor carries no implementation
    /// type, so the service type is the only type evidence a descriptor sweep has, and for both of
    /// these it is still assignable to the contract.
    /// </summary>
    [Test]
    public async Task It_aborts_for_a_factory_registered_validator_under_its_concrete_type()
    {
        GuardRun run = await RunGuard(services =>
            services.AddTransient<MatchingValidator>(_ => new MatchingValidator())
        );

        run.Thrown.Should()
            .BeOfType<InvalidOperationException>()
            .Which.Message.Should()
            .Contain($"'{Logged(typeof(MatchingValidator))}'")
            .And.Contain("are not among the");
    }

    [Test]
    public async Task It_aborts_for_a_factory_registered_validator_under_a_derived_interface()
    {
        GuardRun run = await RunGuard(services =>
            services.AddTransient<IImplementerOwnValidator>(_ => new DerivedInterfaceValidator())
        );

        run.Thrown.Should()
            .BeOfType<InvalidOperationException>()
            .Which.Message.Should()
            .Contain("are not among the");
    }

    /// <summary>
    /// Aliasing a validator under the implementer's own interface with a factory, while also
    /// registering it against the contract, is legitimate: an instance of the aliased type does
    /// resolve. The reachability check compares against resolved instances rather than against a
    /// list of approved types so that this is not an offense.
    /// </summary>
    [Test]
    public async Task It_accepts_a_derived_interface_alias_beside_a_contract_registration()
    {
        GuardRun run = await RunGuard(services =>
        {
            services.TryAddEnumerable(
                ServiceDescriptor.Transient<ICustomResourceValidator, DerivedInterfaceValidator>()
            );
            services.AddTransient<IImplementerOwnValidator>(provider => new DerivedInterfaceValidator());
        });

        run.Thrown.Should().BeNull();
    }

    /// <summary>
    /// A rejected contract descriptor must not also be reported as unreachable. The two checks are
    /// independent, and the reachability check reads what actually resolved rather than what the
    /// descriptor audit approved, so the wrong-lifetime registration below is one offense and not
    /// two contradictory ones.
    /// </summary>
    [Test]
    public async Task It_reports_a_wrong_lifetime_registration_once_and_not_as_unreachable()
    {
        GuardRun run = await RunGuard(services =>
        {
            services.AddTransient<MatchingValidator>();
            services.AddSingleton<ICustomResourceValidator, MatchingValidator>();
        });

        string message = run.Thrown.Should().BeOfType<InvalidOperationException>().Subject.Message;
        message.Should().Contain("its lifetime is Singleton rather than Transient");
        message.Should().NotContain("would never run");
        message.Should().NotContain("are not among the");
    }

    /// <summary>
    /// The negative case for the rule above: also registering the concrete type is legitimate, since
    /// the contract registration makes the validator reachable. Registering both must not be read as
    /// an offense.
    /// </summary>
    [Test]
    public async Task It_accepts_a_validator_registered_under_both_the_contract_and_its_own_type()
    {
        GuardRun run = await RunGuard(services =>
        {
            services.TryAddEnumerable(
                ServiceDescriptor.Transient<ICustomResourceValidator, MatchingValidator>()
            );
            services.AddTransient<MatchingValidator>();
        });

        run.Thrown.Should().BeNull();
        run.Warnings.Should().BeEmpty();
    }

    /// <summary>
    /// An open generic registration of the collection type is selected ahead of the collection MS DI
    /// would synthesize, so it substitutes or empties the set the same way a closed registration
    /// does, while carrying a service type equal to neither the contract nor its closed collection.
    /// </summary>
    [Test]
    public async Task It_aborts_for_an_open_generic_collection_registration()
    {
        GuardRun run = await RunGuard(services =>
        {
            services.TryAddEnumerable(
                ServiceDescriptor.Transient<ICustomResourceValidator, MatchingValidator>()
            );
            services.AddTransient(typeof(IEnumerable<>), typeof(EmptyBag<>));
        });

        run.Thrown.Should()
            .BeOfType<InvalidOperationException>()
            .Which.Message.Should()
            .Contain($"'{Logged(typeof(MatchingValidator))}'")
            .And.Contain("are not among the");
    }

    /// <summary>
    /// A validator's Dispose is implementer code. It must not abort startup for a registration that
    /// broke no rule, and must not be reported as a validator failure.
    /// </summary>
    [Test]
    public async Task It_warns_without_aborting_when_a_validator_dispose_throws()
    {
        GuardRun run = await RunGuard(services =>
            services.TryAddEnumerable(
                ServiceDescriptor.Transient<ICustomResourceValidator, ThrowingDisposeValidator>()
            )
        );

        run.Thrown.Should().BeNull();
        run.Records.Should()
            .Contain(record =>
                record.Message
                == "Custom validator registration guard audited and activated 1 ICustomResourceValidator registration(s)"
            );
        run.Warnings.Should()
            .ContainSingle()
            .Which.Should()
            .Contain("Disposing the ICustomResourceValidator activation scope threw");
    }

    /// <summary>
    /// An empty resource name reaches the miss path. The contract's own documentation anticipates an
    /// empty ValidatedResource, and a message claiming the name "carries" an unusable character would
    /// be false for a name that carries no character at all.
    /// </summary>
    [Test]
    public async Task It_warns_for_an_empty_resource_name()
    {
        GuardRun run = await RunGuard(services =>
            services.TryAddEnumerable(
                ServiceDescriptor.Transient<ICustomResourceValidator, EmptyResourceNameValidator>()
            )
        );

        run.Thrown.Should().BeNull();

        string warning = run.Warnings.Should().ContainSingle().Subject;
        warning.Should().Contain("matches no resource in the effective ApiSchema");
        warning.Should().NotContain("carries");
        warning.Should().NotContain("was altered to make it safe to log");
    }

    /// <summary>
    /// The project-name half of the sanitizer requirement. Without a fixture carrying an unsafe
    /// project name, removing the sanitizer call from that component would leave every test green.
    /// </summary>
    [Test]
    public async Task It_sanitizes_the_project_name_in_its_records()
    {
        GuardRun run = await RunGuard(services =>
            services.TryAddEnumerable(
                ServiceDescriptor.Transient<ICustomResourceValidator, ControlCharacterProjectNameValidator>()
            )
        );

        run.Thrown.Should().BeNull();
        run.Records.Should()
            .Contain(record =>
                record.Message
                == $"ICustomResourceValidator '{Logged(typeof(ControlCharacterProjectNameValidator))}' "
                    + $"AppliesTo entry: ProjectName 'EdFi', ResourceName '{CoreResourceName}'"
            );
        run.Records.Should().OnlyContain(record => !record.Message.Contains("\u0007"));
    }

    // ---------------------------------------------------------------- fixtures

    private interface IServiceNobodyRegisters { }

    /// <summary>
    /// A derived contract an implementer might define for their own convenience. Registering only
    /// against this leaves the validator unreachable.
    /// </summary>
    private interface IImplementerOwnValidator : ICustomResourceValidator { }

    private sealed class DerivedInterfaceValidator : ValidatorBase, IImplementerOwnValidator
    {
        public override IReadOnlyList<ValidatedResource> AppliesTo =>
            [new ValidatedResource(CoreProjectName, CoreResourceName)];
    }

    /// <summary>
    /// An open generic implementing IEnumerable&lt;T&gt; with no constructor dependency on
    /// IEnumerable&lt;T&gt;, so registering it does not fail on its own the way List&lt;T&gt; does.
    /// </summary>
    private sealed class EmptyBag<T> : IEnumerable<T>
    {
        public IEnumerator<T> GetEnumerator() => Enumerable.Empty<T>().GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class ThrowingDisposeValidator : ValidatorBase, IDisposable
    {
        public override IReadOnlyList<ValidatedResource> AppliesTo =>
            [new ValidatedResource(CoreProjectName, CoreResourceName)];

#pragma warning disable S3877 // Throwing from Dispose is the implementer bug under test here
        public void Dispose() => throw new InvalidOperationException("implementer bug in Dispose");
#pragma warning restore S3877
    }

    private sealed class EmptyResourceNameValidator : ValidatorBase
    {
        public override IReadOnlyList<ValidatedResource> AppliesTo =>
            [new ValidatedResource(CoreProjectName, string.Empty)];
    }

    private sealed class ControlCharacterProjectNameValidator : ValidatorBase
    {
        public override IReadOnlyList<ValidatedResource> AppliesTo =>
            [new ValidatedResource("Ed\u0007Fi", CoreResourceName)];
    }

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
            [new ValidatedResource(CoreProjectName, "Scho\u0007ol")];
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

    private sealed class ThrowingEnumerationValidator : ValidatorBase
    {
        public override IReadOnlyList<ValidatedResource> AppliesTo => new ThrowingList();

        private sealed class ThrowingList : IReadOnlyList<ValidatedResource>
        {
            public ValidatedResource this[int index] =>
                throw new InvalidOperationException("indexer reached");

            public int Count => throw new InvalidOperationException("Count reached");

            public IEnumerator<ValidatedResource> GetEnumerator() =>
                throw new InvalidOperationException("enumerator reached");

            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        }
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
