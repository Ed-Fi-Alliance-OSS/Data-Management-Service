// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Acme.FixtureContracts;
using EdFi.DataManagementService.FixtureHost;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace EdFi.Api.Plugins.Hosting.Tests.Unit;

/// <summary>
/// Runs the real invoker over real fixture plugins, builds a real container from the collection they
/// contributed to, and runs the real audit against it.
/// </summary>
/// <remarks>
/// Non-parallelizable as a group: the fixtures report construction counts through a static in an
/// assembly the host serves them, so two cases running at once would read each other's counts.
/// </remarks>
internal static class AuditProbe
{
    /// <summary>
    /// The registry these cases audit against: one fan-in contract and one replace contract, both
    /// declared in the host-prefixed fixture assembly like the real ones they stand in for.
    /// </summary>
    internal static PluginContractRegistry Registry { get; } =
        new([
            new PluginContractEntry(typeof(IFixtureFanInContract), Cardinality.FanIn),
            new PluginContractEntry(typeof(IFixtureReplaceContract), Cardinality.Replace),
        ]);

    /// <summary>A registry declaring nothing, for the case that removes an entry from it.</summary>
    internal static PluginContractRegistry EmptyRegistry { get; } = new([]);

    /// <summary>
    /// The collection a host hands the hooks: real logging, a host default for the replace contract,
    /// and a host-owned service the plugins may not touch.
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

    /// <summary>
    /// Loads the fixtures, runs their hooks over a host collection, builds the container, and audits.
    /// </summary>
    internal static async Task<AuditRun> RunAsync(
        TemporaryPluginRoot root,
        string? behavior,
        PluginContractRegistry? registry = null,
        params string[] fixtureNames
    )
    {
        FixtureObservations.Clear();

        LoadedPlugins plugins = ContributionProbe.Load(root, fixtureNames);
        ServiceCollection services = HostCollection();

        PluginAuditInput input = plugins.ContributeServices(
            services,
            ContributionProbe.HookConfiguration(behavior),
            registry ?? Registry,
            new StringWriter()
        );

        ServiceProvider provider = services.BuildServiceProvider();
        PluginAuditResult result = await PluginRegistrationAudit.AuditAsync(input, provider);

        return new AuditRun(input, result, services, provider);
    }
}

internal sealed record AuditRun(
    PluginAuditInput Input,
    PluginAuditResult Result,
    ServiceCollection Services,
    ServiceProvider Provider
) : IDisposable
{
    internal PluginAuditFinding SingleFinding => Result.Findings.Should().ContainSingle().Subject;

    public void Dispose() => Provider.Dispose();
}

[TestFixture]
[NonParallelizable]
public class Given_one_plugin_claiming_a_replace_contract_over_the_hosts_default
{
    private TemporaryPluginRoot _root = null!;
    private AuditRun _run = null!;

    [SetUp]
    public async Task Setup()
    {
        _root = TemporaryPluginRoot.Create();
        _run = await AuditProbe.RunAsync(_root, "replaceClaim", null, PluginFixtures.Contributor);
    }

    [TearDown]
    public void TearDown()
    {
        _run.Dispose();
        _root.Dispose();
    }

    /// <summary>
    /// The case that must pass. A replace contract exists to displace a host default, so the collection
    /// legitimately holds two descriptors for it when one plugin has claimed it. Counting descriptors
    /// on the collection rather than claims the records attribute fails here.
    /// </summary>
    [Test]
    public void It_is_accepted()
    {
        _run.Result.Findings.Should().BeEmpty();
    }

    [Test]
    public void It_leaves_two_descriptors_on_the_collection_and_one_attributed_claim()
    {
        _run.Services.Count(descriptor => descriptor.ServiceType == typeof(IFixtureReplaceContract))
            .Should()
            .Be(2);

        _run.Input.Records.Should()
            .ContainSingle()
            .Which.Additions.Count(descriptor => descriptor.ServiceType == typeof(IFixtureReplaceContract))
            .Should()
            .Be(1);
    }

    [Test]
    public void It_leaves_the_plugins_claim_as_what_resolves()
    {
        _run.Provider.GetRequiredService<IFixtureReplaceContract>()
            .Describe()
            .Should()
            .Be("FixtureReplaceClaim");
    }
}

[TestFixture]
[NonParallelizable]
public class Given_two_plugins_claiming_one_replace_contract
{
    private TemporaryPluginRoot _root = null!;
    private AuditRun _run = null!;

    [SetUp]
    public async Task Setup()
    {
        _root = TemporaryPluginRoot.Create();
        _run = await AuditProbe.RunAsync(
            _root,
            "replaceClaim",
            null,
            PluginFixtures.Contributor,
            PluginFixtures.SecondContributor
        );
    }

    [TearDown]
    public void TearDown()
    {
        _run.Dispose();
        _root.Dispose();
    }

    [Test]
    public void It_is_fatal_naming_both_plugins_and_the_contract()
    {
        PluginAuditFinding finding = _run.SingleFinding;

        finding.Reason.Should().Be(PluginAuditFailure.ReplaceContractClaimedMoreThanOnce);
        finding.Contract.Should().Be(typeof(IFixtureReplaceContract));
        finding.PluginNames.Should().Equal(PluginFixtures.Contributor, PluginFixtures.SecondContributor);
        finding.Message.Should().Contain(PluginFixtures.Contributor);
        finding.Message.Should().Contain(PluginFixtures.SecondContributor);
        finding.Message.Should().Contain(nameof(IFixtureReplaceContract));
    }
}

[TestFixture]
[NonParallelizable]
public class Given_one_plugin_claiming_a_replace_contract_twice
{
    private TemporaryPluginRoot _root = null!;
    private AuditRun _run = null!;

    [SetUp]
    public async Task Setup()
    {
        _root = TemporaryPluginRoot.Create();
        _run = await AuditProbe.RunAsync(_root, "replaceClaimTwice", null, PluginFixtures.Contributor);
    }

    [TearDown]
    public void TearDown()
    {
        _run.Dispose();
        _root.Dispose();
    }

    /// <summary>
    /// Fatal on its own, without a second plugin. Without this the case passes and the container's
    /// last-wins picks one, which is the outcome a cardinality of "0 or 1" exists to refuse.
    /// </summary>
    [Test]
    public void It_is_fatal_naming_the_plugin_and_the_contract()
    {
        PluginAuditFinding finding = _run.SingleFinding;

        finding.Reason.Should().Be(PluginAuditFailure.ReplaceContractClaimedMoreThanOnce);
        finding.PluginNames.Should().Equal(PluginFixtures.Contributor);
        finding.Contract.Should().Be(typeof(IFixtureReplaceContract));
    }
}

[TestFixture]
[NonParallelizable]
public class Given_a_plugin_registering_a_host_owned_service_type
{
    private TemporaryPluginRoot _root = null!;
    private AuditRun _run = null!;

    [SetUp]
    public async Task Setup()
    {
        _root = TemporaryPluginRoot.Create();
        _run = await AuditProbe.RunAsync(_root, "hostTypeClaim", null, PluginFixtures.Contributor);
    }

    [TearDown]
    public void TearDown()
    {
        _run.Dispose();
        _root.Dispose();
    }

    [Test]
    public void It_is_fatal_naming_the_plugin_and_the_type()
    {
        PluginAuditFinding finding = _run.SingleFinding;

        finding.Reason.Should().Be(PluginAuditFailure.HostOwnedServiceTypeClaimed);
        finding.PluginNames.Should().Equal(PluginFixtures.Contributor);
        finding.Message.Should().Contain(nameof(IFixtureHostUnclaimedService));
    }
}

[TestFixture]
[NonParallelizable]
public class Given_a_plugin_registering_a_declared_contract_declared_in_a_host_assembly
{
    // A root each: a plugin root is assembled per run, and staging the same fixture into one twice is
    // a file collision rather than a second run.
    private TemporaryPluginRoot _acceptedRoot = null!;
    private TemporaryPluginRoot _refusedRoot = null!;
    private AuditRun _accepted = null!;
    private AuditRun _refused = null!;

    [SetUp]
    public async Task Setup()
    {
        _acceptedRoot = TemporaryPluginRoot.Create();
        _accepted = await AuditProbe.RunAsync(
            _acceptedRoot,
            "declaredValidatorOnly",
            null,
            PluginFixtures.Contributor
        );

        // The identical registration, audited against a registry that no longer declares the contract.
        _refusedRoot = TemporaryPluginRoot.Create();
        _refused = await AuditProbe.RunAsync(
            _refusedRoot,
            "declaredValidatorOnly",
            AuditProbe.EmptyRegistry,
            PluginFixtures.Contributor
        );
    }

    [TearDown]
    public void TearDown()
    {
        _accepted.Dispose();
        _refused.Dispose();
        _acceptedRoot.Dispose();
        _refusedRoot.Dispose();
    }

    /// <summary>
    /// The contract's own service type is declared in a host-prefixed assembly, so it matches the
    /// host-owned test. It is admitted only because the declared-contract exemption is evaluated first,
    /// and taking the entry out of the registry is what shows that is the only reason.
    /// </summary>
    [Test]
    public void It_is_admitted_while_the_registry_declares_it()
    {
        _accepted.Result.Findings.Should().BeEmpty();
    }

    [Test]
    public void It_becomes_fatal_once_the_registry_does_not()
    {
        _refused
            .Result.Findings.Should()
            .Contain(finding => finding.Reason == PluginAuditFailure.HostOwnedServiceTypeClaimed);
        _refused
            .Result.Findings.Should()
            .Contain(finding => finding.Message.Contains(nameof(IFixtureFanInContract)));
    }
}

[TestFixture]
[NonParallelizable]
public class Given_a_plugin_that_registered_only_its_own_types
{
    private TemporaryPluginRoot _root = null!;
    private AuditRun _run = null!;

    [SetUp]
    public async Task Setup()
    {
        _root = TemporaryPluginRoot.Create();
        _run = await AuditProbe.RunAsync(_root, "ownTypesOnly", null, PluginFixtures.Contributor);
    }

    [TearDown]
    public void TearDown()
    {
        _run.Dispose();
        _root.Dispose();
    }

    [Test]
    public void It_is_fatal_as_having_contributed_no_declared_contract()
    {
        PluginAuditFinding finding = _run.SingleFinding;

        finding.Reason.Should().Be(PluginAuditFailure.NoDeclaredContractRegistered);
        finding.PluginNames.Should().Equal(PluginFixtures.Contributor);
    }

    [Test]
    public void It_lists_what_the_plugin_did_register_and_names_the_likeliest_cause()
    {
        _run.SingleFinding.Message.Should().Contain(nameof(IAcmeFirstService));
        _run.SingleFinding.Message.Should().Contain("wrong host");
    }
}

[TestFixture]
[NonParallelizable]
public class Given_a_plugin_registering_extras_beside_a_declared_contract
{
    private TemporaryPluginRoot _root = null!;
    private AuditRun _run = null!;

    [SetUp]
    public async Task Setup()
    {
        _root = TemporaryPluginRoot.Create();
        _run = await AuditProbe.RunAsync(_root, "contractPlusExtras", null, PluginFixtures.Contributor);
    }

    [TearDown]
    public void TearDown()
    {
        _run.Dispose();
        _root.Dispose();
    }

    /// <summary>
    /// Its own types, framework options and a hosted service are all ordinary permitted work, which is
    /// what keeps the displacement check from being a ban on registering anything.
    /// </summary>
    [Test]
    public void It_loads_unaffected()
    {
        _run.Result.Findings.Should().BeEmpty();
    }

    [Test]
    public void Its_record_lists_the_hosted_service_it_registered()
    {
        _run.Input.Records.Should()
            .ContainSingle()
            .Which.Additions.Should()
            .Contain(descriptor => descriptor.ServiceType == typeof(IHostedService));
    }

    /// <summary>
    /// The options type is the plugin's own and this assembly cannot name it, so the assertion is on
    /// the framework service type the plugin registered it under, closed over something.
    /// </summary>
    [Test]
    public void Its_record_lists_the_options_configurator_it_registered()
    {
        _run.Input.Records[0]
            .Additions.Should()
            .Contain(descriptor =>
                descriptor.ServiceType.IsGenericType
                && descriptor.ServiceType.GetGenericTypeDefinition() == typeof(IConfigureOptions<>)
            );
    }
}

[TestFixture]
[NonParallelizable]
public class Given_a_declared_contract_registration_that_cannot_be_constructed
{
    private TemporaryPluginRoot _root = null!;
    private AuditRun _run = null!;

    [SetUp]
    public async Task Setup()
    {
        _root = TemporaryPluginRoot.Create();
        _run = await AuditProbe.RunAsync(_root, "unsatisfiable", null, PluginFixtures.Contributor);
    }

    [TearDown]
    public void TearDown()
    {
        _run.Dispose();
        _root.Dispose();
    }

    [Test]
    public void It_is_fatal_naming_the_contributing_plugin_and_the_contract()
    {
        PluginAuditFinding finding = _run.SingleFinding;

        finding.Reason.Should().Be(PluginAuditFailure.DeclaredContractRegistrationNotActivatable);
        finding.Contract.Should().Be(typeof(IFixtureFanInContract));
        finding.PluginNames.Should().Equal(PluginFixtures.Contributor);
    }

    [Test]
    public void It_carries_the_original_activation_exception()
    {
        _run.SingleFinding.ActivationException.Should().BeOfType<InvalidOperationException>();
        _run.SingleFinding.Message.Should().Contain(nameof(IFixtureMissingDependency));
    }

    /// <summary>
    /// One resolution of a group cannot prove which descriptor failed, and an activation failure can
    /// originate in a dependency no plugin registered. So even with a single contributor the message
    /// names the contributor and says so, rather than declaring a cause.
    /// </summary>
    [Test]
    public void It_names_the_contributor_rather_than_declaring_a_cause()
    {
        _run.SingleFinding.Message.Should().Contain("contributed");
        _run.SingleFinding.Message.Should().Contain("rather than the cause");
    }
}

[TestFixture]
[NonParallelizable]
public class Given_a_declared_contract_registered_by_a_factory_that_throws
{
    private TemporaryPluginRoot _root = null!;
    private AuditRun _run = null!;

    [SetUp]
    public async Task Setup()
    {
        _root = TemporaryPluginRoot.Create();
        _run = await AuditProbe.RunAsync(_root, "throwingFactory", null, PluginFixtures.Contributor);
    }

    [TearDown]
    public void TearDown()
    {
        _run.Dispose();
        _root.Dispose();
    }

    [Test]
    public void It_is_fatal()
    {
        _run.SingleFinding.Reason.Should().Be(PluginAuditFailure.DeclaredContractRegistrationNotActivatable);
    }

    /// <summary>
    /// Exactly once. Nothing re-runs a registration to work out who owned it, so a factory with a side
    /// effect is invoked no more times than a boot would invoke it.
    /// </summary>
    [Test]
    public void It_invokes_the_factory_once()
    {
        FixtureObservations.CountOf("throwingFactory").Should().Be(1);
    }
}

[TestFixture]
[NonParallelizable]
public class Given_a_declared_contract_registered_under_two_concrete_keys
{
    private TemporaryPluginRoot _root = null!;
    private AuditRun _run = null!;

    [SetUp]
    public async Task Setup()
    {
        _root = TemporaryPluginRoot.Create();
        _run = await AuditProbe.RunAsync(_root, "twoKeyedContracts", null, PluginFixtures.Contributor);
    }

    [TearDown]
    public void TearDown()
    {
        _run.Dispose();
        _root.Dispose();
    }

    [Test]
    public void It_is_accepted()
    {
        _run.Result.Findings.Should().BeEmpty();
    }

    /// <summary>
    /// One activation per keyed group and no more. Groups are disjoint, so a key resolved twice, or a
    /// wildcard resolve added beside the concrete ones, would show up here as an extra construction.
    /// </summary>
    [Test]
    public void It_activates_each_keyed_registration_exactly_once()
    {
        FixtureObservations.CountOf("fanIn.constructed").Should().Be(2);
    }
}

[TestFixture]
[NonParallelizable]
public class Given_a_declared_contract_registered_under_the_wildcard_key
{
    private TemporaryPluginRoot _root = null!;
    private AuditRun _run = null!;

    [SetUp]
    public async Task Setup()
    {
        _root = TemporaryPluginRoot.Create();
        _run = await AuditProbe.RunAsync(_root, "anyKeyContract", null, PluginFixtures.Contributor);
    }

    [TearDown]
    public void TearDown()
    {
        _run.Dispose();
        _root.Dispose();
    }

    /// <summary>
    /// Refused rather than passed over. Measured: no enumerable resolve reaches a wildcard
    /// registration, and only a single-service resolve with some concrete key does, which the host does
    /// not hold. So the startup activation every declared-contract registration is required to survive
    /// cannot be performed for this shape.
    /// </summary>
    [Test]
    public void It_is_fatal_naming_the_plugin_and_the_contract()
    {
        PluginAuditFinding finding = _run.SingleFinding;

        finding.Reason.Should().Be(PluginAuditFailure.DeclaredContractRegisteredUnderWildcardKey);
        finding.PluginNames.Should().Equal(PluginFixtures.Contributor);
        finding.Contract.Should().Be(typeof(IFixtureFanInContract));
        finding.Message.Should().Contain("wildcard service key");
    }

    [Test]
    public void It_activates_nothing()
    {
        FixtureObservations.CountOf("fanIn.constructed").Should().Be(0);
    }
}

[TestFixture]
[NonParallelizable]
public class Given_a_declared_contract_under_the_wildcard_key_beside_a_concrete_one
{
    private TemporaryPluginRoot _root = null!;
    private AuditRun _run = null!;

    [SetUp]
    public async Task Setup()
    {
        _root = TemporaryPluginRoot.Create();
        _run = await AuditProbe.RunAsync(
            _root,
            "anyKeyAndConcreteContract",
            null,
            PluginFixtures.Contributor
        );
    }

    [TearDown]
    public void TearDown()
    {
        _run.Dispose();
        _root.Dispose();
    }

    [Test]
    public void It_is_fatal_on_the_wildcard_registration()
    {
        _run.SingleFinding.Reason.Should().Be(PluginAuditFailure.DeclaredContractRegisteredUnderWildcardKey);
    }

    /// <summary>
    /// A static refusal stops before activation, so nothing on a rejected candidate is constructed,
    /// including the concrete-keyed registration that would have been fine on its own.
    /// </summary>
    [Test]
    public void It_activates_nothing_at_all()
    {
        FixtureObservations.CountOf("fanIn.constructed").Should().Be(0);
    }
}

[TestFixture]
[NonParallelizable]
public class Given_a_plugin_registering_its_own_service_under_the_wildcard_key
{
    private TemporaryPluginRoot _root = null!;
    private AuditRun _run = null!;

    [SetUp]
    public async Task Setup()
    {
        _root = TemporaryPluginRoot.Create();
        _run = await AuditProbe.RunAsync(_root, "anyKeyOwnService", null, PluginFixtures.Contributor);
    }

    [TearDown]
    public void TearDown()
    {
        _run.Dispose();
        _root.Dispose();
    }

    /// <summary>
    /// The wildcard refusal is about declared contracts and nothing wider. A plugin's own service under
    /// a wildcard key is ordinary permitted work and nothing here inspects it.
    /// </summary>
    [Test]
    public void It_is_permitted()
    {
        _run.Result.Findings.Should().BeEmpty();
    }
}

[TestFixture]
[NonParallelizable]
public class Given_a_declared_contract_registered_with_each_lifetime
{
    private TemporaryPluginRoot _root = null!;

    [TearDown]
    public void TearDown() => _root.Dispose();

    /// <summary>
    /// The probe is an activation check and not a disposal boundary. A singleton it resolved is cached
    /// in the root container, so resolving it afterwards constructs nothing further and production gets
    /// the instance the probe made.
    /// </summary>
    [Test]
    public async Task It_leaves_a_singleton_as_the_instance_production_uses()
    {
        _root = TemporaryPluginRoot.Create();
        using AuditRun run = await AuditProbe.RunAsync(
            _root,
            "singletonContract",
            null,
            PluginFixtures.Contributor
        );

        run.Result.Findings.Should().BeEmpty();
        FixtureObservations.CountOf("fanIn.constructed").Should().Be(1);

        run.Provider.GetRequiredService<IFixtureFanInContract>();

        FixtureObservations.CountOf("fanIn.constructed").Should().Be(1);
    }

    [Test]
    public async Task It_discards_a_scoped_instance_with_the_probes_scope()
    {
        _root = TemporaryPluginRoot.Create();
        using AuditRun run = await AuditProbe.RunAsync(
            _root,
            "scopedContract",
            null,
            PluginFixtures.Contributor
        );

        run.Result.Findings.Should().BeEmpty();
        FixtureObservations.CountOf("fanIn.constructed").Should().Be(1);
        FixtureObservations.CountOf("fanIn.disposed").Should().Be(1);
    }

    [Test]
    public async Task It_constructs_a_transient_again_for_production()
    {
        _root = TemporaryPluginRoot.Create();
        using AuditRun run = await AuditProbe.RunAsync(
            _root,
            "transientContract",
            null,
            PluginFixtures.Contributor
        );

        run.Result.Findings.Should().BeEmpty();
        FixtureObservations.CountOf("fanIn.constructed").Should().Be(1);

        run.Provider.GetRequiredService<IFixtureFanInContract>();

        FixtureObservations.CountOf("fanIn.constructed").Should().Be(2);
    }

    [Test]
    public async Task It_reports_no_cleanup_failure_when_nothing_throws_on_release()
    {
        _root = TemporaryPluginRoot.Create();
        using AuditRun run = await AuditProbe.RunAsync(
            _root,
            "scopedContract",
            null,
            PluginFixtures.Contributor
        );

        run.Result.ScopeCleanupFailure.Should().BeNull();
    }
}

[TestFixture]
[NonParallelizable]
public class Given_a_plugin_whose_replace_claim_declined_and_contributed_nothing_else
{
    private TemporaryPluginRoot _root = null!;
    private AuditRun _run = null!;

    [SetUp]
    public async Task Setup()
    {
        _root = TemporaryPluginRoot.Create();
        _run = await AuditProbe.RunAsync(_root, "decliningTryAdd", null, PluginFixtures.Contributor);
    }

    [TearDown]
    public void TearDown()
    {
        _run.Dispose();
        _root.Dispose();
    }

    /// <summary>
    /// The decline the seam cannot see, surfacing one check downstream. The call added nothing, so the
    /// plugin registered no declared contract, and the message says it contributed nothing rather than
    /// pretending to know why.
    /// </summary>
    [Test]
    public void It_is_fatal_as_having_contributed_no_declared_contract()
    {
        PluginAuditFinding finding = _run.SingleFinding;

        finding.Reason.Should().Be(PluginAuditFailure.NoDeclaredContractRegistered);
        finding.PluginNames.Should().Equal(PluginFixtures.Contributor);
        finding.Message.Should().Contain("nothing at all");
        finding.Message.Should().Contain("TryAdd");
    }
}

[TestFixture]
[NonParallelizable]
public class Given_a_plugin_whose_replace_claim_declined_beside_a_fan_in_registration
{
    private TemporaryPluginRoot _root = null!;
    private AuditRun _run = null!;

    [SetUp]
    public async Task Setup()
    {
        _root = TemporaryPluginRoot.Create();
        _run = await AuditProbe.RunAsync(_root, "decliningTryAddPlusFanIn", null, PluginFixtures.Contributor);
    }

    [TearDown]
    public void TearDown()
    {
        _run.Dispose();
        _root.Dispose();
    }

    /// <summary>
    /// A characterization test: it pins what the design does <em>not</em> detect. The plugin intended to
    /// replace the host default, its call declined invisibly, and because it also registered a declared
    /// contract no check fires. The boot succeeds with the host default still resolving and nothing
    /// anywhere naming the declined claim. Nobody should later read the no-contract rule as covering
    /// every declined TryAdd.
    /// </summary>
    [Test]
    public void It_boots_with_no_finding_naming_the_declined_claim()
    {
        _run.Result.Findings.Should().BeEmpty();
    }

    [Test]
    public void It_leaves_the_host_default_as_what_resolves()
    {
        _run.Provider.GetRequiredService<IFixtureReplaceContract>()
            .Describe()
            .Should()
            .Be(nameof(FixtureHostReplaceDefault));
    }
}
