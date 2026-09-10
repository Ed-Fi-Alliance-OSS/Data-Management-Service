// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Acme.FixtureContracts;
using EdFi.DataManagementService.FixtureHost;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace EdFi.Api.Plugins.Hosting.Tests.Unit;

/// <summary>
/// Loads real fixture plugins from a staged root and runs their hooks through the real invoker, which
/// is the only way to assert what the wrapper and the diff do to a plugin's own code.
/// </summary>
internal static class ContributionProbe
{
    /// <summary>A registry with one replace contract, which is all these cases need it for.</summary>
    internal static PluginContractRegistry Registry { get; } =
        new([new PluginContractEntry(typeof(IFixtureReplaceContract), Cardinality.Replace)]);

    /// <summary>
    /// A registry that declares the fan-in contract, for the cases whose hook registers one.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="Registry"/> rather than added to it. The swallowed-refusal cases turn on
    /// the contribution beside the refusal being a genuine declared contract, because what they assert
    /// is that the host fails anyway on work every other check accepts. Under a registry that does not
    /// declare it, those cases would still fail at the invoker but for a weaker reason, and the claim
    /// that nothing downstream would have objected would be untrue.
    /// </remarks>
    internal static PluginContractRegistry RegistryDeclaringFanIn { get; } =
        new([new PluginContractEntry(typeof(IFixtureFanInContract), Cardinality.FanIn)]);

    /// <summary>
    /// A collection the host has populated: a replace-contract descriptor, a host-owned default, two of
    /// the plugin's own service types, and real logging.
    /// </summary>
    internal static ServiceCollection HostCollection()
    {
        ServiceCollection services = new();
        services.AddLogging(builder => builder.AddConsole());
        services.AddSingleton<IFixtureReplaceContract, FixtureHostReplaceDefault>();
        services.AddScoped<IFixtureHostService, HostOwnedDefault>();
        services.AddSingleton<IAcmeSecondService, AcmeService>();
        return services;
    }

    internal static IConfiguration HookConfiguration(string? behavior) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Fixture:Behavior"] = behavior })
            .Build();

    /// <summary>Stages the named fixtures under a root of the test's own and loads them.</summary>
    internal static LoadedPlugins Load(TemporaryPluginRoot root, params string[] fixtureNames)
    {
        foreach (string fixtureName in fixtureNames)
        {
            root.Add(fixtureName);
        }

        PluginLoaderRun run = PluginLoaderProbe.Run(root.RootPath, string.Join(",", fixtureNames));

        run.Failure.Should().BeNull("the fixtures for these cases are well formed");

        return run.Result!;
    }
}

internal sealed class FixtureHostReplaceDefault : IFixtureReplaceContract
{
    public string Describe() => nameof(FixtureHostReplaceDefault);
}

internal sealed class HostOwnedDefault : IFixtureHostService
{
    public string Describe() => nameof(HostOwnedDefault);
}

[TestFixture]
public class Given_two_plugins_contributing_to_one_collection
{
    private TemporaryPluginRoot _root = null!;
    private ServiceCollection _services = null!;
    private PluginAuditInput _audit = null!;

    [SetUp]
    public void Setup()
    {
        _root = TemporaryPluginRoot.Create();
        LoadedPlugins plugins = ContributionProbe.Load(
            _root,
            PluginFixtures.Contributor,
            PluginFixtures.SecondContributor
        );

        _services = ContributionProbe.HostCollection();
        _audit = plugins.ContributeServices(
            _services,
            ContributionProbe.HookConfiguration(null),
            ContributionProbe.Registry,
            new StringWriter()
        );
    }

    [TearDown]
    public void TearDown() => _root.Dispose();

    [Test]
    public void It_records_one_plugin_per_hook_in_allowlist_order()
    {
        _audit
            .Records.Select(record => record.PluginName)
            .Should()
            .Equal(PluginFixtures.Contributor, PluginFixtures.SecondContributor);
    }

    [Test]
    public void It_attributes_to_each_plugin_only_the_descriptors_that_plugin_added()
    {
        _audit
            .Records[0]
            .Additions.Select(descriptor => descriptor.ServiceType)
            .Should()
            .Equal(typeof(IAcmeFirstService), typeof(IAcmeSecondService));

        _audit
            .Records[1]
            .Additions.Select(descriptor => descriptor.ServiceType)
            .Should()
            .Equal(typeof(IAcmeThirdService));
    }

    [Test]
    public void It_attributes_nothing_the_host_registered_before_the_hooks()
    {
        foreach (PluginContributionRecord record in _audit.Records)
        {
            record
                .Additions.Should()
                .NotContain(descriptor => descriptor.ServiceType == typeof(IFixtureReplaceContract));
            record.Removals.Should().BeEmpty();
            record.ReplacedServiceTypes.Should().BeEmpty();
        }
    }

    [Test]
    public void It_carries_the_registry_the_host_supplied()
    {
        _audit.Registry.Should().BeSameAs(ContributionProbe.Registry);
    }

    [Test]
    public void It_carries_the_descriptors_as_they_stood_when_the_last_hook_returned()
    {
        _audit.DescriptorsAfterContribution.Should().Equal(_services);
    }
}

/// <summary>
/// A diagnostic channel that takes a number from the sequence the fixtures also draw on, the first
/// time it is written to.
/// </summary>
/// <remarks>
/// Written text alone cannot distinguish an announcement made on entry to a hook from one made on the
/// way out of a catch block: both leave the same line behind. Ordering the two channels against a
/// shared counter can, because the plugin takes its own number at hook entry.
/// </remarks>
internal sealed class SequencedDiagnostics : StringWriter
{
    private bool _taken;

    internal int? AnnouncedAt { get; private set; }

    public override void WriteLine(string? value)
    {
        if (
            !_taken
            && value is not null
            && value.Contains("invoking ContributeServices", StringComparison.Ordinal)
        )
        {
            _taken = true;
            AnnouncedAt = FixtureObservations.Next();
        }

        base.WriteLine(value);
    }
}

[TestFixture]
[NonParallelizable]
public class Given_a_hook_that_throws
{
    private TemporaryPluginRoot _root = null!;
    private SequencedDiagnostics _diagnostics = null!;
    private PluginCompositionException _failure = null!;

    [SetUp]
    public void Setup()
    {
        FixtureObservations.Clear();
        _root = TemporaryPluginRoot.Create();
        LoadedPlugins plugins = ContributionProbe.Load(_root, PluginFixtures.HookThrows);

        _diagnostics = new SequencedDiagnostics();
        _failure = Assert.Throws<PluginCompositionException>(() =>
            plugins.ContributeServices(
                ContributionProbe.HostCollection(),
                ContributionProbe.HookConfiguration(null),
                ContributionProbe.Registry,
                _diagnostics
            )
        )!;
    }

    [TearDown]
    public void TearDown()
    {
        _diagnostics.Dispose();
        _root.Dispose();
    }

    [Test]
    public void It_announced_the_plugin()
    {
        _diagnostics
            .ToString()
            .Should()
            .Contain($"invoking ContributeServices on {PluginFixtures.HookThrows}");
    }

    /// <summary>
    /// The announcement is on the channel before the hook is entered, so a hook that never returns
    /// still leaves the plugin named. Asserted by ordering rather than by presence: the fixture takes a
    /// number from a shared counter as the first thing it does, and an announcement written on the way
    /// out of a catch block would order after that number rather than before it.
    /// </summary>
    [Test]
    public void It_announced_the_plugin_before_the_hook_was_entered()
    {
        int? announcedAt = _diagnostics.AnnouncedAt;
        string? enteredAt = FixtureObservations.Read("enteredAt");

        announcedAt.Should().NotBeNull("the channel should have carried the announcement");
        enteredAt.Should().NotBeNull("the hook should have run");
        announcedAt!.Value.Should().BeLessThan(int.Parse(enteredAt!));
    }

    [Test]
    public void It_becomes_a_fatal_naming_the_plugin_and_the_composition_phase()
    {
        _failure.Reason.Should().Be(PluginCompositionFailure.ContributeServicesThrew);
        _failure.PluginName.Should().Be(PluginFixtures.HookThrows);
        _failure.Message.Should().Contain(PluginFixtures.HookThrows);
        _failure.Message.Should().Contain("ContributeServices");
    }

    [Test]
    public void It_keeps_the_original_exception()
    {
        _failure.InnerException.Should().BeOfType<InvalidOperationException>();
        _failure.InnerException!.Message.Should().Be("Acme.HookThrows could not read its own configuration");
    }

    [Test]
    public void It_reports_the_failure_on_the_diagnostic_channel()
    {
        _diagnostics.ToString().Should().Contain("Acme.HookThrows could not read its own configuration");
    }
}

[TestFixture]
public class Given_no_plugin_was_allowlisted
{
    private ServiceCollection _services = null!;
    private PluginAuditInput _audit = null!;

    [SetUp]
    public void Setup()
    {
        _services = ContributionProbe.HostCollection();
        _audit = LoadedPlugins.Empty.ContributeServices(
            _services,
            ContributionProbe.HookConfiguration(null),
            ContributionProbe.Registry,
            new StringWriter()
        );
    }

    [Test]
    public void It_returns_an_input_carrying_no_records()
    {
        _audit.Records.Should().BeEmpty();
    }

    [Test]
    public void It_still_carries_the_registry_and_the_collection()
    {
        _audit.Registry.Should().BeSameAs(ContributionProbe.Registry);
        _audit.DescriptorsAfterContribution.Should().Equal(_services);
    }

    [Test]
    public void It_registers_nothing_of_its_own()
    {
        int countBefore = _services.Count;

        LoadedPlugins.Empty.ContributeServices(
            _services,
            ContributionProbe.HookConfiguration(null),
            ContributionProbe.Registry,
            new StringWriter()
        );

        _services.Count.Should().Be(countBefore);
        _services.Should().NotContain(descriptor => descriptor.ServiceType == typeof(PluginAuditInput));
    }
}

[TestFixture]
public class Given_a_hook_that_resolves_its_dependencies_for_the_first_time
{
    private TemporaryPluginRoot _root = null!;
    private PluginContributionRecord _record = null!;
    private IReadOnlyList<PluginInventoryRow> _inventoryBefore = null!;
    private IReadOnlyList<HostFirstSubstitution> _substitutionsBefore = null!;

    [SetUp]
    public void Setup()
    {
        FixtureObservations.Clear();
        _root = TemporaryPluginRoot.Create();
        LoadedPlugins plugins = ContributionProbe.Load(_root, PluginFixtures.HookTouch);

        _inventoryBefore = plugins.Plugins[0].MaterializeInventory();
        _substitutionsBefore = plugins.Plugins[0].MaterializeSubstitutions();

        PluginAuditInput audit = plugins.ContributeServices(
            ContributionProbe.HostCollection(),
            ContributionProbe.HookConfiguration(null),
            ContributionProbe.Registry,
            new StringWriter()
        );

        _record = audit.Records[0];
    }

    [TearDown]
    public void TearDown() => _root.Dispose();

    [Test]
    public void It_actually_resolved_them()
    {
        FixtureObservations.Read("private").Should().NotBeNull();
        FixtureObservations.Read("hostShared").Should().NotBeNull();
    }

    /// <summary>
    /// Both facts are false when the loader finishes and true when the hook returns. The record reads
    /// them now rather than holding a copy taken earlier, which is what keeps one source behind the
    /// guard's checks and the inventory event.
    /// </summary>
    [Test]
    public void It_reports_the_private_assembly_as_loaded_only_after_the_hook_ran()
    {
        _inventoryBefore
            .Single(row => row.FileName == "Acme.Private.dll")
            .LoadState.Should()
            .Be(PluginFileLoadState.NotLoaded);

        _record
            .MaterializeInventory()
            .Single(row => row.FileName == "Acme.Private.dll")
            .LoadState.Should()
            .Be(PluginFileLoadState.Loaded);
    }

    [Test]
    public void It_reports_the_host_first_substitution_only_after_the_hook_ran()
    {
        _substitutionsBefore
            .Should()
            .NotContain(substitution => substitution.AssemblyName == "Acme.HostShared");

        _record
            .MaterializeSubstitutions()
            .Should()
            .Contain(substitution => substitution.AssemblyName == "Acme.HostShared");
    }

    [Test]
    public void It_reads_both_through_the_loaded_plugin_the_record_holds()
    {
        _record.MaterializeInventory().Should().Equal(_record.Plugin.MaterializeInventory());
        _record.MaterializeSubstitutions().Should().Equal(_record.Plugin.MaterializeSubstitutions());
    }
}

[TestFixture]
public class Given_a_hook_that_adds_the_same_descriptor_instance_twice
{
    private TemporaryPluginRoot _root = null!;
    private PluginContributionRecord _record = null!;

    [SetUp]
    public void Setup()
    {
        _root = TemporaryPluginRoot.Create();
        LoadedPlugins plugins = ContributionProbe.Load(_root, PluginFixtures.Contributor);

        _record = plugins
            .ContributeServices(
                ContributionProbe.HostCollection(),
                ContributionProbe.HookConfiguration("duplicateDescriptor"),
                ContributionProbe.Registry,
                new StringWriter()
            )
            .Records[0];
    }

    [TearDown]
    public void TearDown() => _root.Dispose();

    /// <summary>
    /// Two registrations, not one. A comparison that de-duplicated by reference would report a single
    /// addition, and the cardinality check downstream counts what a plugin registered.
    /// </summary>
    [Test]
    public void It_counts_both_occurrences()
    {
        _record.Additions.Should().HaveCount(2);
        _record.Additions[0].Should().BeSameAs(_record.Additions[1]);
        _record.Additions[0].ServiceType.Should().Be(typeof(IAcmeFirstService));
    }

    [Test]
    public void It_records_no_removal()
    {
        _record.Removals.Should().BeEmpty();
        _record.ReplacedServiceTypes.Should().BeEmpty();
    }
}

[TestFixture]
public class Given_a_hook_that_replaces_a_descriptor_it_added_itself
{
    private TemporaryPluginRoot _root = null!;
    private PluginContributionRecord _record = null!;

    [SetUp]
    public void Setup()
    {
        _root = TemporaryPluginRoot.Create();
        LoadedPlugins plugins = ContributionProbe.Load(_root, PluginFixtures.Contributor);

        _record = plugins
            .ContributeServices(
                ContributionProbe.HostCollection(),
                ContributionProbe.HookConfiguration("ownReplace"),
                ContributionProbe.Registry,
                new StringWriter()
            )
            .Records[0];
    }

    [TearDown]
    public void TearDown() => _root.Dispose();

    /// <summary>
    /// The descriptor it removed was one it added inside the same hook, so it was never on the
    /// collection when the hook began and neither end of the comparison sees it.
    /// </summary>
    [Test]
    public void It_records_the_surviving_registration_and_no_removal()
    {
        _record
            .Additions.Should()
            .ContainSingle()
            .Which.ImplementationType.Should()
            .Be(typeof(SecondAcmeService));
        _record.Removals.Should().BeEmpty();
        _record.ReplacedServiceTypes.Should().BeEmpty();
    }
}

[TestFixture]
public class Given_a_hook_that_replaces_a_pre_existing_descriptor_it_is_allowed_to_replace
{
    private TemporaryPluginRoot _root = null!;
    private ServiceDescriptor _hostDescriptor = null!;
    private PluginContributionRecord _record = null!;

    [SetUp]
    public void Setup()
    {
        _root = TemporaryPluginRoot.Create();
        LoadedPlugins plugins = ContributionProbe.Load(_root, PluginFixtures.Contributor);

        ServiceCollection services = ContributionProbe.HostCollection();
        _hostDescriptor = services.Single(descriptor => descriptor.ServiceType == typeof(IAcmeSecondService));

        _record = plugins
            .ContributeServices(
                services,
                ContributionProbe.HookConfiguration("replacePreExistingOwnKind"),
                ContributionProbe.Registry,
                new StringWriter()
            )
            .Records[0];
    }

    [TearDown]
    public void TearDown() => _root.Dispose();

    /// <summary>
    /// The positive case for the replacement list, which the own-new-descriptor case cannot reach: the
    /// descriptor displaced here was on the collection before the hook, so both ends of the comparison
    /// see it and the service type lands in all three lists at once.
    /// </summary>
    [Test]
    public void It_appears_in_the_removals_the_additions_and_the_replaced_service_types()
    {
        _record
            .Removals.Should()
            .ContainSingle(displacement => displacement.ServiceType == typeof(IAcmeSecondService))
            .Which.Descriptor.Should()
            .BeSameAs(_hostDescriptor);

        _record
            .Additions.Should()
            .Contain(descriptor =>
                descriptor.ServiceType == typeof(IAcmeSecondService)
                && descriptor.ImplementationType == typeof(SecondAcmeService)
            );

        _record.ReplacedServiceTypes.Should().Contain(typeof(IAcmeSecondService));
    }

    [Test]
    public void It_records_the_implementation_the_replacement_displaced()
    {
        _record
            .Removals.Single(displacement => displacement.ServiceType == typeof(IAcmeSecondService))
            .DisplacedImplementationType.Should()
            .Be(typeof(AcmeService));
    }
}

[TestFixture]
public class Given_a_hook_that_removes_a_pre_existing_framework_descriptor
{
    private TemporaryPluginRoot _root = null!;
    private ServiceCollection _services = null!;
    private PluginContributionRecord _record = null!;

    [SetUp]
    public void Setup()
    {
        _root = TemporaryPluginRoot.Create();
        LoadedPlugins plugins = ContributionProbe.Load(_root, PluginFixtures.Contributor);

        _services = ContributionProbe.HostCollection();
        _record = plugins
            .ContributeServices(
                _services,
                ContributionProbe.HookConfiguration("frameworkRemove"),
                ContributionProbe.Registry,
                new StringWriter()
            )
            .Records[0];
    }

    [TearDown]
    public void TearDown() => _root.Dispose();

    [Test]
    public void It_is_permitted()
    {
        _services.Should().NotContain(descriptor => descriptor.ServiceType == typeof(IOptions<>));
    }

    /// <summary>
    /// Permitted and recorded. The inventory event this feeds is the only trace such a removal leaves,
    /// which is why the record carries the implementation it displaced and not just the service type.
    /// </summary>
    [Test]
    public void It_is_recorded_with_the_implementation_it_displaced()
    {
        PluginDescriptorDisplacement removal = _record
            .Removals.Should()
            .ContainSingle(displacement => displacement.ServiceType == typeof(IOptions<>))
            .Subject;

        removal.DisplacedImplementationType.Should().NotBeNull();
        removal.Descriptor.ServiceType.Should().Be(typeof(IOptions<>));
    }
}

[TestFixture]
public class Given_a_hook_that_reads_the_collection_it_was_handed
{
    private TemporaryPluginRoot _root = null!;

    [SetUp]
    public void Setup()
    {
        FixtureObservations.Clear();
        _root = TemporaryPluginRoot.Create();
        LoadedPlugins plugins = ContributionProbe.Load(_root, PluginFixtures.Contributor);

        plugins.ContributeServices(
            ContributionProbe.HostCollection(),
            ContributionProbe.HookConfiguration("enumerate"),
            ContributionProbe.Registry,
            new StringWriter()
        );
    }

    [TearDown]
    public void TearDown() => _root.Dispose();

    /// <summary>
    /// The pass-through, asserted from inside a real hook rather than against a wrapper a test built.
    /// </summary>
    [TestCase("count")]
    [TestCase("indexer")]
    [TestCase("indexOf")]
    [TestCase("contains")]
    public void It_sees_reads_that_agree_with_what_it_enumerated(string observation)
    {
        FixtureObservations.Read(observation).Should().Be(bool.TrueString);
    }

    [Test]
    public void It_sees_the_replace_contract_descriptor_the_host_registered()
    {
        FixtureObservations.Read("sawReplaceContract").Should().Be(bool.TrueString);
    }
}

[TestFixture]
public class Given_a_hook_that_calls_remove_all_for_one_of_its_own_service_types
{
    private TemporaryPluginRoot _root = null!;
    private ServiceCollection _services = null!;
    private List<Type> _before = null!;

    [SetUp]
    public void Setup()
    {
        _root = TemporaryPluginRoot.Create();
        LoadedPlugins plugins = ContributionProbe.Load(_root, PluginFixtures.Contributor);

        _services = ContributionProbe.HostCollection();
        _before = [.. _services.Select(descriptor => descriptor.ServiceType)];

        plugins.ContributeServices(
            _services,
            ContributionProbe.HookConfiguration("removeAll"),
            ContributionProbe.Registry,
            new StringWriter()
        );
    }

    [TearDown]
    public void TearDown() => _root.Dispose();

    /// <summary>
    /// The regression with a named cause, run from inside a hook. A projection over the collection made
    /// this call remove descriptors nobody asked about, including the replace-contract descriptor.
    /// </summary>
    [Test]
    public void It_removes_only_the_descriptors_for_that_type()
    {
        List<Type> expected = [.. _before.Where(serviceType => serviceType != typeof(IAcmeSecondService))];
        expected.Add(typeof(IAcmeFirstService));

        _services.Select(descriptor => descriptor.ServiceType).Should().Equal(expected);
    }
}

[TestFixture]
public class Given_a_hook_that_displaces_the_hosts_own_default
{
    private TemporaryPluginRoot _root = null!;
    private ServiceCollection _services = null!;
    private PluginCompositionException _failure = null!;

    [SetUp]
    public void Setup()
    {
        _root = TemporaryPluginRoot.Create();
        LoadedPlugins plugins = ContributionProbe.Load(_root, PluginFixtures.Contributor);

        _services = ContributionProbe.HostCollection();
        _failure = Assert.Throws<PluginCompositionException>(() =>
            plugins.ContributeServices(
                _services,
                ContributionProbe.HookConfiguration("replaceHostDefault"),
                ContributionProbe.Registry,
                new StringWriter()
            )
        )!;
    }

    [TearDown]
    public void TearDown() => _root.Dispose();

    [Test]
    public void It_is_refused_naming_the_plugin_and_the_service_type()
    {
        _failure.Reason.Should().Be(PluginCompositionFailure.HostOwnedDescriptorDisplaced);
        _failure.PluginName.Should().Be(PluginFixtures.Contributor);
        _failure.Message.Should().Contain(nameof(IFixtureHostService));
    }

    [Test]
    public void It_leaves_the_hosts_descriptor_in_place()
    {
        _services
            .Should()
            .ContainSingle(descriptor => descriptor.ServiceType == typeof(IFixtureHostService))
            .Which.ImplementationType.Should()
            .Be(typeof(HostOwnedDefault));
    }
}

[TestFixture]
public class Given_a_hook_that_adds_a_logging_provider_of_its_own
{
    private TemporaryPluginRoot _root = null!;
    private ServiceCollection _services = null!;

    [SetUp]
    public void Setup()
    {
        _root = TemporaryPluginRoot.Create();
        LoadedPlugins plugins = ContributionProbe.Load(_root, PluginFixtures.Contributor);

        _services = ContributionProbe.HostCollection();
        plugins.ContributeServices(
            _services,
            ContributionProbe.HookConfiguration("addProvider"),
            ContributionProbe.Registry,
            new StringWriter()
        );
    }

    [TearDown]
    public void TearDown() => _root.Dispose();

    [Test]
    public void It_loads_unaffected()
    {
        _services
            .Count(descriptor => descriptor.ServiceType == typeof(ILoggerProvider))
            .Should()
            .BeGreaterThan(1);
    }
}

/// <summary>
/// The one case that drives the public overload, so the channel under test is the process's own
/// <see cref="Console.Error"/> rather than a writer a test injected.
/// </summary>
/// <remarks>
/// Non-parallelizable because it redirects a process-wide writer. Without it the injected-writer cases
/// above would be the only evidence, and they would pass just as happily against an overload wired to
/// the wrong channel.
/// </remarks>
[TestFixture]
[NonParallelizable]
public class Given_a_hook_refused_while_the_host_is_using_the_real_error_channel
{
    private TemporaryPluginRoot _root = null!;
    private TextWriter _originalError = null!;
    private StringWriter _captured = null!;
    private ServiceCollection _services = null!;
    private List<ServiceDescriptor> _providersBefore = null!;
    private PluginCompositionException _failure = null!;

    [SetUp]
    public void Setup()
    {
        _root = TemporaryPluginRoot.Create();
        LoadedPlugins plugins = ContributionProbe.Load(_root, PluginFixtures.Contributor);

        _services = ContributionProbe.HostCollection();
        _providersBefore =
        [
            .. _services.Where(descriptor => descriptor.ServiceType == typeof(ILoggerProvider)),
        ];

        _originalError = Console.Error;
        _captured = new StringWriter();
        Console.SetError(_captured);

        try
        {
            _failure = Assert.Throws<PluginCompositionException>(() =>
                plugins.ContributeServices(
                    _services,
                    ContributionProbe.HookConfiguration("clearProviders"),
                    ContributionProbe.Registry
                )
            )!;
        }
        finally
        {
            Console.SetError(_originalError);
        }
    }

    [TearDown]
    public void TearDown()
    {
        _captured.Dispose();
        _root.Dispose();
    }

    [Test]
    public void It_announced_the_plugin_on_the_real_error_channel()
    {
        _captured.ToString().Should().Contain($"invoking ContributeServices on {PluginFixtures.Contributor}");
    }

    [Test]
    public void It_reported_the_refusal_on_the_real_error_channel()
    {
        _captured.ToString().Should().Contain(PluginFixtures.Contributor);
        _captured.ToString().Should().Contain(nameof(ILoggerProvider));
    }

    [Test]
    public void It_refused_the_call_naming_the_logging_service_type()
    {
        _failure.Reason.Should().Be(PluginCompositionFailure.LoggingPipelineDescriptorDisplaced);
        _failure.PluginName.Should().Be(PluginFixtures.Contributor);
    }

    /// <summary>
    /// The property that makes the refusal reportable at all: the host's providers are still registered
    /// when the message is written, so the channel the message would otherwise have needed is intact.
    /// </summary>
    [Test]
    public void It_left_the_hosts_provider_descriptors_in_place()
    {
        _providersBefore.Should().NotBeEmpty();
        _services
            .Where(descriptor => descriptor.ServiceType == typeof(ILoggerProvider))
            .Should()
            .Equal(_providersBefore);
    }
}

/// <summary>
/// A hook that caught the refusal of <c>Clear</c> and went on to register a valid declared contract.
/// </summary>
/// <remarks>
/// The case the invoker's record of the refusal exists for. Nothing downstream can notice this one:
/// the wrapper refused before the call landed, so the per-hook diff sees no removal, and the audit sees
/// a plugin that registered a declared contract like any other.
/// </remarks>
[TestFixture]
public class Given_a_hook_that_swallowed_the_refusal_of_clear
{
    private TemporaryPluginRoot _root = null!;
    private ServiceCollection _services = null!;
    private List<ServiceDescriptor> _hostDescriptorsBefore = null!;
    private PluginCompositionException _failure = null!;

    [SetUp]
    public void Setup()
    {
        _root = TemporaryPluginRoot.Create();
        LoadedPlugins plugins = ContributionProbe.Load(_root, PluginFixtures.Contributor);

        _services = ContributionProbe.HostCollection();
        _hostDescriptorsBefore = [.. _services];

        _failure = Assert.Throws<PluginCompositionException>(() =>
            plugins.ContributeServices(
                _services,
                ContributionProbe.HookConfiguration("swallowClear"),
                ContributionProbe.RegistryDeclaringFanIn,
                new StringWriter()
            )
        )!;
    }

    [TearDown]
    public void TearDown() => _root.Dispose();

    [Test]
    public void It_still_refuses_the_composition_naming_the_plugin()
    {
        _failure.Reason.Should().Be(PluginCompositionFailure.ServiceCollectionCleared);
        _failure.PluginName.Should().Be(PluginFixtures.Contributor);
    }

    /// <summary>
    /// The rule that fired, not a generic hook failure. A refusal carries no inner exception, which is
    /// what separates it from the exception the invoker raises when a hook throws.
    /// </summary>
    [Test]
    public void It_reports_the_rule_that_fired_rather_than_a_hook_failure()
    {
        _failure.Reason.Should().NotBe(PluginCompositionFailure.ContributeServicesThrew);
        _failure.InnerException.Should().BeNull();
    }

    /// <summary>
    /// Every descriptor the host held before the hook is still there, by reference. The hook's own
    /// legal registration is left where it is: refusing the composition is the host's answer, and
    /// rolling a plugin's permitted additions back is not part of it.
    /// </summary>
    [Test]
    public void It_left_every_descriptor_the_host_had_registered_in_place()
    {
        _hostDescriptorsBefore.Should().NotBeEmpty();
        _services.Should().ContainInOrder(_hostDescriptorsBefore);
    }

    /// <summary>
    /// The hook really did carry on and register a contract this host declares, which is what makes
    /// this the case it claims to be: every check downstream of the invoker would have accepted it.
    /// </summary>
    [Test]
    public void It_registered_the_declared_contract_the_refusal_did_not_stop()
    {
        _services.Should().Contain(descriptor => descriptor.ServiceType == typeof(IFixtureFanInContract));
    }
}

/// <summary>
/// The same swallow over the logging carve-out, through the real <c>ClearProviders</c> call.
/// </summary>
[TestFixture]
public class Given_a_hook_that_swallowed_the_refusal_of_clear_providers
{
    private TemporaryPluginRoot _root = null!;
    private ServiceCollection _services = null!;
    private List<ServiceDescriptor> _providersBefore = null!;
    private PluginCompositionException _failure = null!;

    [SetUp]
    public void Setup()
    {
        _root = TemporaryPluginRoot.Create();
        LoadedPlugins plugins = ContributionProbe.Load(_root, PluginFixtures.Contributor);

        _services = ContributionProbe.HostCollection();
        _providersBefore =
        [
            .. _services.Where(descriptor => descriptor.ServiceType == typeof(ILoggerProvider)),
        ];

        _failure = Assert.Throws<PluginCompositionException>(() =>
            plugins.ContributeServices(
                _services,
                ContributionProbe.HookConfiguration("swallowClearProviders"),
                ContributionProbe.RegistryDeclaringFanIn,
                new StringWriter()
            )
        )!;
    }

    [TearDown]
    public void TearDown() => _root.Dispose();

    [Test]
    public void It_still_refuses_the_composition_naming_the_logging_service_type()
    {
        _failure.Reason.Should().Be(PluginCompositionFailure.LoggingPipelineDescriptorDisplaced);
        _failure.PluginName.Should().Be(PluginFixtures.Contributor);
        _failure.Message.Should().Contain(nameof(ILoggerProvider));
    }

    [Test]
    public void It_left_the_hosts_provider_descriptors_in_place()
    {
        _providersBefore.Should().NotBeEmpty();
        _services
            .Where(descriptor => descriptor.ServiceType == typeof(ILoggerProvider))
            .Should()
            .Equal(_providersBefore);
    }

    [Test]
    public void It_registered_the_declared_contract_the_refusal_did_not_stop()
    {
        _services.Should().Contain(descriptor => descriptor.ServiceType == typeof(IFixtureFanInContract));
    }
}

/// <summary>
/// A hook that swallowed a refusal and then failed for a reason of its own.
/// </summary>
/// <remarks>
/// The refusal wins. The later failure is ordinarily a consequence of the work the refusal cut short,
/// so reporting it would name the symptom and hide the host-owned rule that actually fired.
/// </remarks>
[TestFixture]
public class Given_a_hook_that_swallowed_a_refusal_and_then_threw
{
    private TemporaryPluginRoot _root = null!;
    private PluginCompositionException _failure = null!;

    [SetUp]
    public void Setup()
    {
        _root = TemporaryPluginRoot.Create();
        LoadedPlugins plugins = ContributionProbe.Load(_root, PluginFixtures.Contributor);

        _failure = Assert.Throws<PluginCompositionException>(() =>
            plugins.ContributeServices(
                ContributionProbe.HostCollection(),
                ContributionProbe.HookConfiguration("swallowClearThenThrow"),
                ContributionProbe.Registry,
                new StringWriter()
            )
        )!;
    }

    [TearDown]
    public void TearDown() => _root.Dispose();

    [Test]
    public void It_reports_the_swallowed_refusal_rather_than_the_later_failure()
    {
        _failure.Reason.Should().Be(PluginCompositionFailure.ServiceCollectionCleared);
        _failure.PluginName.Should().Be(PluginFixtures.Contributor);
    }

    [Test]
    public void It_does_not_report_the_hook_as_having_thrown()
    {
        _failure.Reason.Should().NotBe(PluginCompositionFailure.ContributeServicesThrew);
        _failure.InnerException.Should().BeNull();
    }
}

/// <summary>
/// A hook that swallowed one refusal and then tripped a second it did not catch.
/// </summary>
/// <remarks>
/// The first is reported, because it is the rule that fired before the rest of the hook ran. This is
/// the other of the invoker's two exception paths: here a refusal is in flight when the second is
/// found, where the case above arrives with an unrelated exception.
/// </remarks>
[TestFixture]
public class Given_a_hook_that_swallowed_a_refusal_and_then_tripped_another
{
    private TemporaryPluginRoot _root = null!;
    private ServiceCollection _services = null!;
    private PluginCompositionException _failure = null!;

    [SetUp]
    public void Setup()
    {
        _root = TemporaryPluginRoot.Create();
        LoadedPlugins plugins = ContributionProbe.Load(_root, PluginFixtures.Contributor);

        _services = ContributionProbe.HostCollection();
        _failure = Assert.Throws<PluginCompositionException>(() =>
            plugins.ContributeServices(
                _services,
                ContributionProbe.HookConfiguration("swallowClearThenDisplaceHostDefault"),
                ContributionProbe.Registry,
                new StringWriter()
            )
        )!;
    }

    [TearDown]
    public void TearDown() => _root.Dispose();

    [Test]
    public void It_reports_the_first_refusal_rather_than_the_second()
    {
        _failure.Reason.Should().Be(PluginCompositionFailure.ServiceCollectionCleared);
        _failure.Reason.Should().NotBe(PluginCompositionFailure.HostOwnedDescriptorDisplaced);
    }

    [Test]
    public void It_left_the_hosts_descriptor_in_place()
    {
        _services
            .Should()
            .ContainSingle(descriptor => descriptor.ServiceType == typeof(IFixtureHostService))
            .Which.ImplementationType.Should()
            .Be(typeof(HostOwnedDefault));
    }
}

/// <summary>
/// The same swallow over the logging reservation: a hook that registers an <c>ILoggerFactory</c> of
/// its own, catches the refusal, and carries on registering.
/// </summary>
/// <remarks>
/// The reservation reuses the wrapper's latch for the reason the other swallow cases give. A hook
/// that treats its own setup as best-effort would otherwise turn a host decision into a log line it
/// never wrote, and here the log line is the thing at stake: the registration under test is what
/// decides which logger the host has.
/// </remarks>
[TestFixture]
public class Given_a_hook_that_swallowed_the_refusal_of_its_own_logger_factory
{
    private TemporaryPluginRoot _root = null!;
    private ServiceCollection _services = null!;
    private List<ServiceDescriptor> _loggingBefore = null!;
    private PluginCompositionException _failure = null!;

    [SetUp]
    public void Setup()
    {
        _root = TemporaryPluginRoot.Create();
        LoadedPlugins plugins = ContributionProbe.Load(_root, PluginFixtures.Contributor);

        _services = ContributionProbe.HostCollection();
        _loggingBefore = [.. _services.Where(descriptor => descriptor.ServiceType == typeof(ILoggerFactory))];

        _failure = Assert.Throws<PluginCompositionException>(() =>
            plugins.ContributeServices(
                _services,
                ContributionProbe.HookConfiguration("swallowLoggerFactory"),
                ContributionProbe.RegistryDeclaringFanIn,
                new StringWriter()
            )
        )!;
    }

    [TearDown]
    public void TearDown() => _root.Dispose();

    [Test]
    public void It_still_refuses_the_composition_naming_the_logging_service_type()
    {
        _failure.Reason.Should().Be(PluginCompositionFailure.ReservedLoggingServiceRegistered);
        _failure.PluginName.Should().Be(PluginFixtures.Contributor);
        _failure.Message.Should().Contain(nameof(ILoggerFactory));
    }

    /// <summary>
    /// The write never landed, so the host still has exactly the logger factory descriptor it had.
    /// </summary>
    [Test]
    public void It_left_the_hosts_logger_factory_descriptor_alone()
    {
        _loggingBefore.Should().NotBeEmpty();
        _services
            .Where(descriptor => descriptor.ServiceType == typeof(ILoggerFactory))
            .Should()
            .Equal(_loggingBefore);
    }

    /// <summary>
    /// The hook really did carry on and register a contract this host declares, which is what makes
    /// this the case it claims to be: every check downstream of the invoker would have accepted it.
    /// </summary>
    [Test]
    public void It_registered_the_declared_contract_the_refusal_did_not_stop()
    {
        _services.Should().Contain(descriptor => descriptor.ServiceType == typeof(IFixtureFanInContract));
    }
}

/// <summary>
/// A null descriptor through each of the collection's three incoming write paths, refused as an
/// argument fault rather than as a host rule.
/// </summary>
/// <remarks>
/// <para>
/// <c>ServiceCollection</c> accepts null through <c>Add</c>, <c>Insert</c> and the indexer, so without
/// a guard the null sits in the collection until the per-hook diff reads it. That diff runs after the
/// invoker's exception handling has been left behind, so the process died on a dictionary lookup
/// naming no plugin, over a collection the offending plugin had already been credited with.
/// </para>
/// <para>
/// Rejecting it in the wrapper puts the throw back inside the hook, where the invoker's existing
/// catch-all names the plugin and keeps the original exception. That is why this needs no failure
/// reason of its own and does not latch a refusal: nothing here is a host rule about what a plugin may
/// register, it is an argument the collection should never have taken.
/// </para>
/// </remarks>
[TestFixture]
public class Given_a_hook_that_hands_the_collection_a_null_descriptor
{
    /// <summary>What one write path's attempt left behind.</summary>
    private sealed record NullWrite(
        PluginCompositionException Failure,
        string Diagnostics,
        List<ServiceDescriptor> Before,
        List<ServiceDescriptor> After
    );

    private static readonly string[] WritePaths = ["nullAdd", "nullInsert", "nullIndexerAssignment"];

    private TemporaryPluginRoot _root = null!;
    private Dictionary<string, NullWrite> _attempts = null!;

    [SetUp]
    public void Setup()
    {
        _root = TemporaryPluginRoot.Create();
        LoadedPlugins plugins = ContributionProbe.Load(_root, PluginFixtures.Contributor);

        _attempts = [];

        foreach (string writePath in WritePaths)
        {
            ServiceCollection services = ContributionProbe.HostCollection();
            List<ServiceDescriptor> before = [.. services];
            StringWriter diagnostics = new();

            PluginCompositionException failure = Assert.Throws<PluginCompositionException>(() =>
                plugins.ContributeServices(
                    services,
                    ContributionProbe.HookConfiguration(writePath),
                    ContributionProbe.Registry,
                    diagnostics
                )
            )!;

            _attempts[writePath] = new NullWrite(failure, diagnostics.ToString(), before, [.. services]);
        }
    }

    [TearDown]
    public void TearDown() => _root.Dispose();

    [Test]
    public void It_becomes_a_fatal_naming_the_plugin_and_the_composition_phase()
    {
        foreach ((string writePath, NullWrite attempt) in _attempts)
        {
            attempt
                .Failure.Reason.Should()
                .Be(PluginCompositionFailure.ContributeServicesThrew, "of {0}", writePath);
            attempt.Failure.PluginName.Should().Be(PluginFixtures.Contributor, "of {0}", writePath);
            attempt.Failure.Message.Should().Contain(PluginFixtures.Contributor, "of {0}", writePath);
        }
    }

    /// <summary>
    /// The exception the guard threw is kept rather than flattened into the message, which is what
    /// leaves a plugin author the frame inside their own hook that made the call.
    /// </summary>
    [Test]
    public void It_keeps_the_argument_exception_the_guard_threw()
    {
        foreach ((string writePath, NullWrite attempt) in _attempts)
        {
            attempt.Failure.InnerException.Should().BeOfType<ArgumentNullException>("of {0}", writePath);
        }
    }

    [Test]
    public void It_reports_the_refusal_on_the_diagnostic_channel()
    {
        foreach ((string writePath, NullWrite attempt) in _attempts)
        {
            attempt.Diagnostics.Should().Contain("plugin composition refused:", "of {0}", writePath);
            attempt.Diagnostics.Should().Contain(PluginFixtures.Contributor, "of {0}", writePath);
        }
    }

    /// <summary>
    /// Refused before the write reached the real collection, so the host has exactly the descriptors it
    /// had, by reference, and no null among them for anything downstream to read.
    /// </summary>
    [Test]
    public void It_leaves_the_hosts_collection_exactly_as_it_was()
    {
        foreach ((string writePath, NullWrite attempt) in _attempts)
        {
            attempt.Before.Should().NotBeEmpty("of {0}", writePath);
            attempt.After.Should().Equal(attempt.Before, "of {0}", writePath);
        }
    }
}
