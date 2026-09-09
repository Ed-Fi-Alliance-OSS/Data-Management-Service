// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.Api.Plugins.Hosting;
using EdFi.DataManagementService.CustomValidation;
using EdFi.DataManagementService.Frontend.AspNetCore.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit;

/// <summary>
/// A plugin registering its services through the helpers the framework and a vendor SDK actually ship,
/// against a collection the Data Management Service's own <c>AddServices</c> has populated.
/// </summary>
/// <remarks>
/// <para>
/// This is a test against the libraries as published, not against a fixture imitating them. That is
/// the whole point: the recording collection sits in front of every call a plugin makes, and the
/// question is whether the helpers a real plugin reaches for get through it.
/// </para>
/// <para>
/// What it proves and what it does not, stated here so nobody reads more into it. Measured on
/// net10.0, none of <c>AddHttpClient</c>, <c>AddOptions&lt;T&gt;().Bind</c>, <c>AddLogging</c> or
/// <c>AddAzureClients</c> removes or overwrites anything: across those packages the only
/// <c>ServiceCollectionDescriptorExtensions</c> members referenced at all are the TryAdd family. So
/// this proves the wrapper does not reject ordinary registration work, and it proves nothing about the
/// rule being scoped to pre-existing descriptors. That scope is proven by the plugin that registers a
/// descriptor and then replaces it, in the plugin hosting tests, and the logging carve-out is proven
/// by the plugin that calls ClearProviders. Do not repurpose this case for either.
/// </para>
/// </remarks>
[TestFixture]
[NonParallelizable]
public class Given_a_plugin_registering_through_the_real_framework_and_vendor_helpers
{
    private const string RealHelpers = "Acme.RealHelpers";
    private const string HttpClientName = "acme-real-helpers";

    private string _pluginRoot = null!;
    private IServiceCollection _services = null!;
    private List<ServiceDescriptor> _before = null!;
    private PluginAuditInput _auditInput = null!;

    [SetUp]
    public void Setup()
    {
        _pluginRoot = PluginCompositionProbe.CreatePluginRoot();
        LoadedPlugins plugins = PluginCompositionProbe.Load(_pluginRoot, RealHelpers);

        _services = PluginCompositionProbe.DmsPopulatedCollection();
        _before = [.. _services];

        // Through the production seam, over the host's own populated collection, with the host's own
        // configuration handed to the hook so the options builder binds what the host passed in.
        _services.AddPluginServiceContributions(PluginCompositionProbe.HookConfiguration(null), plugins);

        // The last, not the only. AddServices already registered one over the empty set of plugins
        // while populating this collection, and the call above registered a second over the loaded
        // one. Taking the last is what a single-service resolve does, so this is the input the startup
        // check would actually read.
        _auditInput =
            _services
                .Last(descriptor => descriptor.ServiceType == typeof(PluginAuditInput))
                .ImplementationInstance as PluginAuditInput
            ?? throw new AssertionException("the host did not register its audit input");
    }

    [TearDown]
    public void TearDown() => PluginCompositionProbe.DeletePluginRoot(_pluginRoot);

    /// <summary>
    /// Nothing rejected. The hook ran to completion, so no call it made was refused before it landed.
    /// </summary>
    [Test]
    public void It_is_not_rejected_by_the_recording_collection()
    {
        _auditInput.Records.Should().ContainSingle().Which.PluginName.Should().Be(RealHelpers);
    }

    /// <summary>
    /// And it removed nothing, which is what makes the boundary in this fixture's remarks a measurement
    /// rather than an assumption: these helpers do not exercise the pre-existing scope because they do
    /// not remove anything to be scoped.
    /// </summary>
    [Test]
    public void It_removed_and_replaced_nothing()
    {
        PluginContributionRecord record = _auditInput.Records[0];

        record.Removals.Should().BeEmpty();
        record.ReplacedServiceTypes.Should().BeEmpty();

        // Every descriptor the host had before the hook is still there, in the same order, by
        // reference.
        _services.Take(_before.Count).Should().Equal(_before);
    }

    /// <summary>
    /// The audit admits it, because it contributed a declared contract beside all that ordinary work.
    /// </summary>
    [Test]
    public async Task It_is_admitted_by_the_startup_checks()
    {
        await using ServiceProvider provider = _services.BuildServiceProvider();

        PluginAuditResult result = await PluginRegistrationAudit.AuditAsync(_auditInput, provider);

        result.Findings.Should().BeEmpty();
        result.ScopeCleanupFailure.Should().BeNull();
    }

    /// <summary>
    /// The named HTTP client helper actually did its work.
    /// </summary>
    /// <remarks>
    /// Measured while writing this: the host's own AddServices has already called AddHttpClient, so
    /// every factory registration the plugin's call makes is a TryAdd that declines, and the plugin's
    /// record correctly attributes none of them to it. What lands is the configuration of its own named
    /// client, which is asserted by the declaring assembly of the service type for the same reason the
    /// cloud case is. That decline is the design's invisible-TryAdd property showing up in a real
    /// setting rather than in a fixture built to demonstrate it.
    /// </remarks>
    [Test]
    public void It_left_the_http_client_registrations_behind()
    {
        // The configuration of a named client is registered as an options configurator over a type the
        // HTTP client package declares, so the package shows up in the generic argument rather than in
        // the service type itself.
        _auditInput
            .Records[0]
            .Additions.SelectMany(descriptor => descriptor.ServiceType.GenericTypeArguments)
            .Should()
            .Contain(argument => argument.Assembly.GetName().Name == "Microsoft.Extensions.Http");

        using ServiceProvider provider = _services.BuildServiceProvider();

        provider
            .GetRequiredService<IHttpClientFactory>()
            .CreateClient(HttpClientName)
            .Timeout.Should()
            .Be(TimeSpan.FromSeconds(30), "the plugin configured its own named client");
    }

    /// <summary>
    /// The options builder bound a section of the configuration the host passed in, rather than one the
    /// plugin built for itself.
    /// </summary>
    [Test]
    public void It_left_the_options_registrations_behind()
    {
        _auditInput
            .Records[0]
            .Additions.Should()
            .Contain(descriptor =>
                descriptor.ServiceType.IsGenericType
                && descriptor.ServiceType.GetGenericTypeDefinition() == typeof(IOptionsChangeTokenSource<>)
            );
    }

    /// <summary>
    /// The vendor helper registered its factory infrastructure. That is the registration
    /// AddAzureClients exists to make, and it is what a plugin talking to a cloud service ends up with.
    /// </summary>
    /// <remarks>
    /// Asserted by the declaring assembly of the service type rather than by naming that type, so the
    /// vendor package's closure stays inside the fixture that has to call its helper and does not reach
    /// this project as well. What it establishes is the same thing: the vendor's own helper registered
    /// the vendor's own service types, through the wrapper, without being refused.
    /// </remarks>
    [Test]
    public void It_left_the_cloud_client_registrations_behind()
    {
        _auditInput
            .Records[0]
            .Additions.Select(descriptor => descriptor.ServiceType)
            .Should()
            .Contain(serviceType => serviceType.Assembly.GetName().Name == "Microsoft.Extensions.Azure");
    }

    /// <summary>
    /// And the declared contract, which is what the audit admitted it for.
    /// </summary>
    [Test]
    public void It_left_the_declared_contract_registration_behind()
    {
        _auditInput
            .Records[0]
            .Additions.Should()
            .Contain(descriptor => descriptor.ServiceType == typeof(ICustomResourceValidator));

        using ServiceProvider provider = _services.BuildServiceProvider();

        provider.GetServices<ICustomResourceValidator>().Should().ContainSingle();
    }
}
