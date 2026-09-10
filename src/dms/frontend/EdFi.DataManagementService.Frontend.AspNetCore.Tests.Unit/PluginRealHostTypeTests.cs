// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.Api.Plugins.Hosting;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.CustomValidation;
using EdFi.DataManagementService.Frontend.AspNetCore.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit;

/// <summary>
/// The host-owned rules against the real service the design names, on a collection the Data Management
/// Service's own <c>AddServices</c> has populated.
/// </summary>
/// <remarks>
/// The plugin hosting tests exercise the same rules against a synthetic host-prefixed assembly, which
/// is the only way to reach the near-miss assembly names the predicate turns on. These are the other
/// half: the real repository, really registered by the host, really displaced through each of the three
/// members that can displace one.
/// </remarks>
[TestFixture]
[NonParallelizable]
public class Given_a_plugin_displacing_the_hosts_real_document_store_repository
{
    private string _pluginRoot = null!;

    [SetUp]
    public void Setup() => _pluginRoot = PluginCompositionProbe.CreatePluginRoot();

    [TearDown]
    public void TearDown() => PluginCompositionProbe.DeletePluginRoot(_pluginRoot);

    [TestCase("hostTypeRemoveAll", TestName = "It_refuses_a_remove_all_over_the_repository")]
    [TestCase("hostTypeReplace", TestName = "It_refuses_a_replace_over_the_repository")]
    [TestCase("hostTypeIndexer", TestName = "It_refuses_an_indexer_assignment_over_the_repository")]
    public void It_refuses_the_displacement_before_it_reaches_the_collection(string behavior)
    {
        LoadedPlugins plugins = PluginCompositionProbe.Load(_pluginRoot);
        IServiceCollection services = PluginCompositionProbe.DmsPopulatedCollection();

        List<ServiceDescriptor> repositoryDescriptorsBefore =
        [
            .. services.Where(descriptor => descriptor.ServiceType == typeof(IDocumentStoreRepository)),
        ];
        int countBefore = services.Count;

        Action contribution = () =>
            services.AddPluginServiceContributions(
                PluginCompositionProbe.HookConfiguration(behavior),
                plugins
            );

        contribution
            .Should()
            .Throw<PluginCompositionException>()
            .Where(exception =>
                exception.Reason == PluginCompositionFailure.HostOwnedDescriptorDisplaced
                && exception.PluginName == PluginCompositionProbe.DmsContributor
            )
            .WithMessage($"*{nameof(IDocumentStoreRepository)}*");

        // Refused before the call reached the collection, so the host's registrations are exactly what
        // they were. Compared by reference, so an overwrite with an equivalent descriptor would fail.
        repositoryDescriptorsBefore.Should().NotBeEmpty();
        services
            .Where(descriptor => descriptor.ServiceType == typeof(IDocumentStoreRepository))
            .Should()
            .Equal(repositoryDescriptorsBefore);
        services.Count.Should().Be(countBefore);
    }
}

/// <summary>
/// The displacement refused, caught by the plugin, and followed by a valid declared-contract
/// registration, against the real repository on a collection <c>AddServices</c> has populated.
/// </summary>
/// <remarks>
/// Registration code that treats its own setup as best-effort catches the host's refusal without ever
/// knowing there was one, and every downstream check passes on what such a hook leaves behind: the
/// wrapper refused before the write landed so the diff records no removal, and the plugin registered a
/// declared contract like any other. The host keeps its own fatal decision instead.
/// </remarks>
[TestFixture]
[NonParallelizable]
public class Given_a_plugin_that_swallowed_the_refusal_of_displacing_the_real_repository
{
    private string _pluginRoot = null!;

    [SetUp]
    public void Setup() => _pluginRoot = PluginCompositionProbe.CreatePluginRoot();

    [TearDown]
    public void TearDown() => PluginCompositionProbe.DeletePluginRoot(_pluginRoot);

    [Test]
    public void It_still_refuses_the_composition_and_leaves_the_repository_alone()
    {
        LoadedPlugins plugins = PluginCompositionProbe.Load(_pluginRoot);
        IServiceCollection services = PluginCompositionProbe.DmsPopulatedCollection();

        List<ServiceDescriptor> repositoryDescriptorsBefore =
        [
            .. services.Where(descriptor => descriptor.ServiceType == typeof(IDocumentStoreRepository)),
        ];

        Action contribution = () =>
            services.AddPluginServiceContributions(
                PluginCompositionProbe.HookConfiguration("swallowHostTypeReplace"),
                plugins
            );

        contribution
            .Should()
            .Throw<PluginCompositionException>()
            .Where(exception =>
                exception.Reason == PluginCompositionFailure.HostOwnedDescriptorDisplaced
                && exception.PluginName == PluginCompositionProbe.DmsContributor
            )
            .WithMessage($"*{nameof(IDocumentStoreRepository)}*");

        // The protected descriptors, by reference. The plugin's own validator registration is left
        // where the hook put it: refusing the composition is the answer, not rolling back the
        // additions a plugin was allowed to make.
        repositoryDescriptorsBefore.Should().NotBeEmpty();
        services
            .Where(descriptor => descriptor.ServiceType == typeof(IDocumentStoreRepository))
            .Should()
            .Equal(repositoryDescriptorsBefore);

        // And the hook did carry on to register the real declared contract after swallowing, so this
        // is a plugin the checks downstream of the invoker would have accepted.
        services.Should().Contain(descriptor => descriptor.ServiceType == typeof(ICustomResourceValidator));
    }
}

[TestFixture]
[NonParallelizable]
public class Given_a_plugin_registering_the_hosts_real_document_store_repository
{
    private string _pluginRoot = null!;

    [SetUp]
    public void Setup() => _pluginRoot = PluginCompositionProbe.CreatePluginRoot();

    [TearDown]
    public void TearDown() => PluginCompositionProbe.DeletePluginRoot(_pluginRoot);

    /// <summary>
    /// Adding is not removing, so the wrapper permits it: nothing pre-existing was touched. It is the
    /// post-container check that refuses it, because the repository is a host service type the host
    /// declares no plugin contract for. That split is the design's, and this is the case that shows
    /// both halves of it.
    /// </summary>
    [Test]
    public async Task It_is_permitted_by_the_wrapper_and_refused_by_the_startup_check()
    {
        LoadedPlugins plugins = PluginCompositionProbe.Load(_pluginRoot);
        IServiceCollection services = PluginCompositionProbe.DmsPopulatedCollection();

        Action contribution = () =>
            services.AddPluginServiceContributions(
                PluginCompositionProbe.HookConfiguration("hostTypeAdd"),
                plugins
            );

        contribution.Should().NotThrow();

        await using ServiceProvider provider = services.BuildServiceProvider();
        PluginAuditResult result = await PluginRegistrationAudit.AuditAsync(
            provider.GetRequiredService<PluginAuditInput>(),
            provider
        );

        result
            .Findings.Should()
            .ContainSingle(finding => finding.Reason == PluginAuditFailure.HostOwnedServiceTypeClaimed)
            .Which.Message.Should()
            .Contain(nameof(IDocumentStoreRepository));
        result.Findings[0].PluginNames.Should().Equal(PluginCompositionProbe.DmsContributor);
    }
}
