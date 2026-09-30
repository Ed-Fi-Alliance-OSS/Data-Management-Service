// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.Api.Plugins.Hosting;
using EdFi.DmsConfigurationService.Backend.Deploy;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using PostgresqlDatabaseDeploy = EdFi.DmsConfigurationService.Backend.Postgresql.Deploy.DatabaseDeploy;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Infrastructure;

/// <summary>
/// The host-owned predicate over a real Configuration Service service type, rather than over the
/// synthetic host-named assemblies the plugin hosting tests use.
/// </summary>
[TestFixture]
public class Given_a_real_configuration_service_service_type
{
    [Test]
    public void It_is_host_owned()
    {
        HostOwnedServiceTypes.IsHostOwned(typeof(IDatabaseDeploy)).Should().BeTrue();
    }

    [Test]
    public void It_is_the_type_the_fixture_plugins_guard_behaviors_name()
    {
        typeof(IDatabaseDeploy).FullName.Should().Be(CmsPluginBoot.HostServiceTypeName);
    }
}

/// <summary>
/// The recording wrapper a plugin's service hook receives, over a collection holding the descriptor
/// the Configuration Service registers for a real service type of its own, refusing a removal or an
/// indexer overwrite of it.
/// </summary>
/// <remarks>
/// Driven directly because a boot cannot show it: a refused hook ends host creation before any test
/// callback sees the collection. What this adds to the boot cases is that the refusal happened before
/// the call reached the real collection, which an exception alone does not establish.
/// </remarks>
[TestFixture("remove")]
[TestFixture("overwrite")]
public class Given_a_plugin_displacing_a_pre_existing_configuration_service_descriptor(string operation)
{
    private const string PluginName = "Acme.CmsContributor";

    private readonly ServiceCollection _inner = [];
    private readonly StringWriter _diagnostics = new();
    private ServiceDescriptor _original = null!;
    private ServiceDescriptor _replacement = null!;
    private RecordingServiceCollection _wrapper = null!;
    private PluginCompositionException? _refusal;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _inner.AddSingleton<IDatabaseDeploy, PostgresqlDatabaseDeploy>();
        _original = _inner[0];
        _replacement = ServiceDescriptor.Singleton(typeof(IDatabaseDeploy), _ => new object());
        _wrapper = new RecordingServiceCollection(_inner, PluginName, _diagnostics);

        try
        {
            if (operation == "remove")
            {
                _wrapper.Remove(_original);
            }
            else
            {
                _wrapper[0] = _replacement;
            }
        }
        catch (PluginCompositionException refusal)
        {
            _refusal = refusal;
        }
    }

    [OneTimeTearDown]
    public void OneTimeTearDown() => _diagnostics.Dispose();

    [Test]
    public void It_refuses_the_call_as_a_host_owned_descriptor_displacement()
    {
        _refusal.Should().NotBeNull();
        _refusal!.Reason.Should().Be(PluginCompositionFailure.HostOwnedDescriptorDisplaced);
        _refusal.PluginName.Should().Be(PluginName);
        _wrapper.FirstRefusal.Should().BeSameAs(_refusal);
    }

    [Test]
    public void It_writes_the_refusal_naming_the_plugin_and_the_type()
    {
        _diagnostics
            .ToString()
            .Should()
            .Contain(
                $"plugin composition refused: plugin '{PluginName}' removed or overwrote the pre-existing "
                    + $"host descriptor for '{typeof(IDatabaseDeploy).FullName}'."
            );
    }

    [Test]
    public void It_leaves_the_hosts_descriptor_in_place_on_the_real_collection()
    {
        _inner.Should().ContainSingle().Which.Should().BeSameAs(_original);
        _inner.Should().NotContain(_replacement);
    }
}
