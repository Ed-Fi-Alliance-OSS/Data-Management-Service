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
