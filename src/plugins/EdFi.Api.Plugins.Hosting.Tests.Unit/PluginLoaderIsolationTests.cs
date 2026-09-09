// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Runtime.Loader;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using NUnit.Framework;

namespace EdFi.Api.Plugins.Hosting.Tests.Unit;

/// <summary>
/// A host configuration for the hook to read, separate from the one that configures the loader.
/// </summary>
/// <remarks>
/// The loader's own configuration carries the <c>Plugins</c> section and nothing a plugin should see.
/// What a plugin is handed is the host's whole configuration, so these cases build one of their own
/// rather than reusing the loader's.
/// </remarks>
internal static class HostConfiguration
{
    internal static IConfiguration WithSection(string section, IReadOnlyDictionary<string, string?> values)
    {
        Dictionary<string, string?> prefixed = new(StringComparer.Ordinal);

        foreach (KeyValuePair<string, string?> value in values)
        {
            prefixed[$"{section}:{value.Key}"] = value.Value;
        }

        return new ConfigurationBuilder().AddInMemoryCollection(prefixed).Build();
    }
}

/// <summary>
/// Two plugins carrying different majors of one library the host does not have.
/// </summary>
/// <remarks>
/// This is what host-first keeps. It gives up a plugin overriding an assembly the host carries; what
/// it does not give up is the case real collisions live in, where two vendors ship different versions
/// of the same third-party library and the host has no opinion because it has no copy.
/// </remarks>
[TestFixture]
public class Given_two_plugins_shipping_different_majors_of_one_private_dependency
{
    private TemporaryPluginRoot _root = null!;
    private PluginLoaderRun _run = null!;
    private Type _libV1 = null!;
    private Type _libV2 = null!;

    [SetUp]
    public void Setup()
    {
        _root = TemporaryPluginRoot.Create();
        _root.Add(PluginFixtures.LibV1);
        _root.Add(PluginFixtures.LibV2);

        _run = PluginLoaderProbe.Run(_root.RootPath, $"{PluginFixtures.LibV1},{PluginFixtures.LibV2}");

        _run.Failure.Should().BeNull();

        _libV1 = _run.Result!.Plugins.Single(plugin => plugin.Name == PluginFixtures.LibV1)
            .Instance.GetType();
        _libV2 = _run.Result!.Plugins.Single(plugin => plugin.Name == PluginFixtures.LibV2)
            .Instance.GetType();
    }

    [TearDown]
    public void TearDown() => _root.Dispose();

    private static string Describe(Type pluginType) =>
        (string)pluginType.GetMethod("DescribeSharedDependency")!.Invoke(null, null)!;

    private static Type SharedMarkerType(Type pluginType) =>
        (Type)pluginType.GetMethod("SharedMarkerType")!.Invoke(null, null)!;

    [Test]
    public void It_is_a_library_the_host_carries_at_no_version()
    {
        // The premise, asserted rather than assumed. If the host carried Acme.Shared at any version,
        // host-first would serve that copy to both plugins and every assertion below would be about
        // something else entirely.
        HostAssemblies.TryLoad("Acme.Shared", out _).Should().BeFalse();
    }

    [Test]
    public void It_serves_each_plugin_the_copy_that_plugin_shipped()
    {
        Describe(_libV1).Should().Be("Acme.Shared 1.0.0");
        Describe(_libV2).Should().Be("Acme.Shared 2.0.0");
    }

    [Test]
    public void It_gives_the_two_copies_separate_type_identities()
    {
        // Both types have the same full name, so a comparison by name would report one type. The Type
        // objects are what the runtime actually uses, and there are two of them.
        Type first = SharedMarkerType(_libV1);
        Type second = SharedMarkerType(_libV2);

        first.FullName.Should().Be(second.FullName);
        first.Should().NotBeSameAs(second);
        first.Assembly.Should().NotBeSameAs(second.Assembly);
    }

    [Test]
    public void It_resolves_each_copy_inside_the_context_of_the_plugin_that_shipped_it()
    {
        AssemblyLoadContext firstContext = AssemblyLoadContext.GetLoadContext(
            SharedMarkerType(_libV1).Assembly
        )!;
        AssemblyLoadContext secondContext = AssemblyLoadContext.GetLoadContext(
            SharedMarkerType(_libV2).Assembly
        )!;

        // Named for the plugin that owns it, which is what makes a runtime diagnostic say where an
        // assembly came from, and neither is the default context.
        firstContext.Name.Should().Be(PluginFixtures.LibV1);
        secondContext.Name.Should().Be(PluginFixtures.LibV2);
        firstContext.Should().NotBeSameAs(secondContext);
        firstContext.Should().NotBeSameAs(AssemblyLoadContext.Default);
        secondContext.Should().NotBeSameAs(AssemblyLoadContext.Default);
    }

    [Test]
    public void It_records_the_two_copies_as_separate_versions()
    {
        SharedMarkerType(_libV1).Assembly.GetName().Version.Should().Be(new Version(1, 0, 0, 0));
        SharedMarkerType(_libV2).Assembly.GetName().Version.Should().Be(new Version(2, 0, 0, 0));
    }

    [Test]
    public void It_carries_both_versions_through_one_container()
    {
        // The half a host cares about: both hooks contribute to one collection, and each contribution
        // carries the version its own copy reported. Isolation that only held while nothing was
        // registered would not be isolation a host could use.
        ServiceCollection services = [];
        IConfiguration configuration = HostConfiguration.WithSection(
            "Acme",
            new Dictionary<string, string?>()
        );

        foreach (LoadedPlugin plugin in _run.Result!.Plugins)
        {
            plugin.Instance.ContributeServices(services, configuration);
        }

        using ServiceProvider provider = services.BuildServiceProvider();

        object first = provider.GetRequiredService(_libV1.Assembly.GetType("Acme.LibV1.LibV1Marker")!);
        object second = provider.GetRequiredService(_libV2.Assembly.GetType("Acme.LibV2.LibV2Marker")!);

        first.GetType().GetProperty("SharedVersion")!.GetValue(first).Should().Be("Acme.Shared 1.0.0");
        second.GetType().GetProperty("SharedVersion")!.GetValue(second).Should().Be("Acme.Shared 2.0.0");
    }
}

/// <summary>
/// The plugin that reads configuration and binds options, loaded by the real loader.
/// </summary>
/// <remarks>
/// design.md calls this the regression test for the <c>IChangeToken</c> split, because it is the most
/// ordinary plugin there is and it is the one an enumerated shared set breaks. The control that breaks
/// it is asserted in the fixture below; this one asserts that host-first does not.
/// </remarks>
[TestFixture]
public class Given_a_plugin_that_binds_options_through_the_hosts_container
{
    private TemporaryPluginRoot _root = null!;
    private ServiceProvider _provider = null!;
    private Type _pluginType = null!;
    private Type _optionsType = null!;

    [SetUp]
    public void Setup()
    {
        _root = TemporaryPluginRoot.Create();
        _root.Add(PluginFixtures.Options);

        PluginLoaderRun run = PluginLoaderProbe.Run(_root.RootPath, PluginFixtures.Options);
        run.Failure.Should().BeNull();

        LoadedPlugin plugin = run.Result!.Plugins.Single();
        _pluginType = plugin.Instance.GetType();
        _optionsType = _pluginType.Assembly.GetType("Acme.Options.AcmeOptions")!;

        ServiceCollection services = [];

        // Invoked directly on the returned instance. The production loader does not call the hook -
        // the story that adds the recording wrapper owns invocation - so a test that wants the hook's
        // behaviour calls it, which is also how design.md defines this regression.
        plugin.Instance.ContributeServices(
            services,
            HostConfiguration.WithSection(
                "Acme",
                new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["Greeting"] = "from the host's configuration",
                }
            )
        );

        _provider = services.BuildServiceProvider();
    }

    [TearDown]
    public void TearDown()
    {
        _provider.Dispose();
        _root.Dispose();
    }

    private object BoundValue()
    {
        Type optionsInterface = typeof(IOptions<>).MakeGenericType(_optionsType);

        return optionsInterface
            .GetProperty("Value")!
            .GetValue(_provider.GetRequiredService(optionsInterface))!;
    }

    [Test]
    public void It_binds_the_hosts_value_over_the_plugins_own_default()
    {
        _optionsType
            .GetProperty("Greeting")!
            .GetValue(BoundValue())
            .Should()
            .Be("from the host's configuration");
    }

    [Test]
    public void It_keeps_the_plugins_own_default_where_the_host_says_nothing()
    {
        // The plugin composed this layer from its own configuration builder, so a value surviving here
        // is evidence its own Microsoft.Extensions.Configuration copy did real work rather than merely
        // sitting in the plugin directory.
        _optionsType.GetProperty("Retries")!.GetValue(BoundValue()).Should().Be(3);
    }

    [Test]
    public void It_serves_one_identity_for_the_type_the_enumerated_set_would_split()
    {
        // IChangeToken as the plugin's own assembly resolves it, against IChangeToken as this host
        // holds it. One assembly instance, so nothing can be split.
        Type asThePluginSeesIt = (Type)_pluginType.GetMethod("ChangeTokenType")!.Invoke(null, null)!;

        asThePluginSeesIt.Should().BeSameAs(typeof(IChangeToken));
    }

    [Test]
    public void It_produces_a_reload_token_the_host_can_consume()
    {
        // The change-token source the plugin's own Options.ConfigurationExtensions copy registered when
        // it bound the host's section. Its GetChangeToken calls IConfiguration.GetReloadToken, which is
        // the exact call the enumerated set splits, and it returns whichever IChangeToken that copy
        // resolved. Resolving and invoking it is what makes this a live assertion rather than a
        // registration count.
        Type sourceInterface = typeof(IOptionsChangeTokenSource<>).MakeGenericType(_optionsType);
        object[] sources = (
            (IEnumerable<object>)
                _provider.GetRequiredService(typeof(IEnumerable<>).MakeGenericType(sourceInterface))
        ).ToArray();

        sources.Should().NotBeEmpty();

        foreach (object source in sources)
        {
            object token = sourceInterface.GetMethod("GetChangeToken")!.Invoke(source, null)!;

            token.Should().BeAssignableTo<IChangeToken>();
        }
    }
}

/// <summary>
/// The same plugin, the same bytes, under the enumerated shared set instead of host-first.
/// </summary>
/// <remarks>
/// design.md rejects an enumerated set on the strength of a measurement, and this is that measurement
/// kept where a change to the decision has to argue with it. The control lives only in this test
/// assembly; nothing in the loader can be configured into it.
/// </remarks>
[TestFixture]
public class Given_the_same_plugin_under_an_enumerated_shared_set
{
    private TemporaryPluginRoot _root = null!;
    private Exception? _failure;

    [SetUp]
    public void Setup()
    {
        _root = TemporaryPluginRoot.Create();
        _root.Add(PluginFixtures.Options);

        _failure = EnumeratedSharedSetContext.InvokeContributeServices(
            Path.Combine(_root.RootPath, PluginFixtures.Options),
            PluginFixtures.Options,
            HostConfiguration.WithSection(
                "Acme",
                new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["Greeting"] = "from the host's configuration",
                }
            )
        );

        TestContext.Out.WriteLine($"enumerated shared set: {_failure?.ToString() ?? "no failure"}");
    }

    [TearDown]
    public void TearDown() => _root.Dispose();

    [Test]
    public void It_ships_its_own_copies_of_the_assemblies_the_enumerated_set_leaves_out()
    {
        // The premise of the whole comparison: these are real files in the plugin directory, so a
        // strategy that does not prefer the host's copies has something else to serve.
        string directory = Path.Combine(_root.RootPath, PluginFixtures.Options);

        File.Exists(Path.Combine(directory, "Microsoft.Extensions.Primitives.dll")).Should().BeTrue();
        File.Exists(Path.Combine(directory, "Microsoft.Extensions.Options.dll")).Should().BeTrue();
        File.Exists(Path.Combine(directory, "Microsoft.Extensions.Configuration.dll")).Should().BeTrue();
    }

    [Test]
    public void It_loads_cleanly_and_fails_only_when_the_hook_runs()
    {
        // Loading is not where an enumerated set goes wrong, which is what makes it such a plausible
        // wrong answer: it looks correct until an ordinary plugin's hook runs.
        _failure.Should().NotBeNull();
    }

    [Test]
    public void It_fails_on_the_change_token_the_enumerated_set_split()
    {
        _failure.Should().BeOfType<TypeLoadException>();
        _failure!.Message.Should().Contain("GetReloadToken");
    }
}
