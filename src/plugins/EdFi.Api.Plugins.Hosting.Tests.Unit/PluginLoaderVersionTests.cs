// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Reflection;
using System.Runtime.Loader;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.Api.Plugins.Hosting.Tests.Unit;

[TestFixture]
public class Given_a_plugin_published_self_contained
{
    private TemporaryPluginRoot _root = null!;
    private PluginLoaderRun _run = null!;

    [SetUp]
    public void Setup()
    {
        _root = TemporaryPluginRoot.Create();
        _root.Add(PluginFixtures.SelfContained);
        _run = PluginLoaderProbe.RunExpectingFailure(_root.RootPath, PluginFixtures.SelfContained);
    }

    [TearDown]
    public void TearDown() => _root.Dispose();

    [Test]
    public void It_refuses_the_publish_shape()
    {
        _run.Failure!.Reason.Should().Be(PluginLoadFailure.SelfContainedPublish);
        _run.Failure!.Message.Should().Contain("--no-self-contained");
    }
}

[TestFixture]
public class Given_a_plugin_published_for_a_runtime_identifier
{
    private TemporaryPluginRoot _root = null!;
    private PluginLoaderRun _run = null!;

    [SetUp]
    public void Setup()
    {
        _root = TemporaryPluginRoot.Create();
        _root.Add(PluginFixtures.RidSpecific);
        _run = PluginLoaderProbe.Run(_root.RootPath, PluginFixtures.RidSpecific);
    }

    [TearDown]
    public void TearDown() => _root.Dispose();

    [Test]
    public void It_loads_because_the_discriminator_is_the_runtime_pack_and_not_the_runtime_identifier()
    {
        // A RID-specific framework-dependent publish names the runtime identifier in runtimeTarget and
        // is how a plugin ships native assets, so keying the refusal on that would refuse a legitimate
        // publish.
        _run.Failure.Should().BeNull();
        _run.Result!.Plugins.Should().ContainSingle();
    }
}

[TestFixture]
public class Given_a_plugin_built_against_a_newer_contract_than_the_host_carries
{
    private TemporaryPluginRoot _root = null!;
    private PluginLoaderRun _run = null!;

    [SetUp]
    public void Setup()
    {
        _root = TemporaryPluginRoot.Create();
        _root.Add(PluginFixtures.NewerContract);
        _run = PluginLoaderProbe.RunExpectingFailure(_root.RootPath, PluginFixtures.NewerContract);
    }

    [TearDown]
    public void TearDown() => _root.Dispose();

    [Test]
    public void It_refuses_and_names_the_contract_and_both_versions()
    {
        _run.Failure!.Reason.Should().Be(PluginLoadFailure.ContractVersionSkew);
        _run.Failure!.Message.Should().Contain("EdFi.Api.Plugins").And.Contain("2.0.0").And.Contain("1.0.0");
    }

    [Test]
    public void It_refuses_from_the_metadata_read_rather_than_from_a_type_load()
    {
        // The fixture also exposes a type whose load fails with a recognizable error naming
        // Acme.HostShared. Its absence from the message is what proves the check ran before any type
        // was loaded, which is where it has to run for the message to be actionable.
        _run.Failure!.Message.Should().NotContain("Acme.HostShared");
        _run.Failure!.InnerException.Should().BeNull();
    }
}

[TestFixture]
public class Given_a_contract_set_naming_a_second_contract
{
    private TemporaryPluginRoot _root = null!;

    [SetUp]
    public void Setup()
    {
        _root = TemporaryPluginRoot.Create();
        _root.Add(PluginFixtures.DeclaredSkew);
    }

    [TearDown]
    public void TearDown() => _root.Dispose();

    [Test]
    public void It_catches_a_plugin_skewed_on_the_second_entry_and_names_it()
    {
        PluginLoaderRun run = PluginLoaderProbe.RunExpectingFailure(
            _root.RootPath,
            PluginFixtures.DeclaredSkew,
            contracts: ["EdFi.Api.Plugins", "Acme.HostShared"]
        );

        run.Failure!.Reason.Should().Be(PluginLoadFailure.ContractVersionSkew);
        run.Failure!.Message.Should().Contain("Acme.HostShared");
    }

    [Test]
    public void It_catches_nothing_from_a_set_carrying_a_package_id_where_an_assembly_name_belongs()
    {
        // What an assembly reference carries is a simple assembly name, so a package id in the contract
        // set matches nothing. The plugin is still refused, by the manifest preflight, and the different
        // failure is exactly the evidence: the contract check saw nothing.
        PluginLoaderRun run = PluginLoaderProbe.RunExpectingFailure(
            _root.RootPath,
            PluginFixtures.DeclaredSkew,
            contracts: ["EdFi.Api.Plugins", "Acme.HostShared.Package"]
        );

        run.Failure!.Reason.Should().Be(PluginLoadFailure.DependencyVersionSkew);
    }

    [Test]
    public void It_does_not_manufacture_a_failure_from_a_contract_the_plugin_never_references()
    {
        // A caller entry the plugin does not reference is never probed, so naming one the host could not
        // resolve changes nothing: the failure is still the skew on the contract the plugin does
        // reference.
        PluginLoaderRun run = PluginLoaderProbe.RunExpectingFailure(
            _root.RootPath,
            PluginFixtures.DeclaredSkew,
            contracts: ["Acme.HostShared", "Acme.NoSuchContract.ThisHostHasNever.HeardOf"]
        );

        run.Failure!.Reason.Should().Be(PluginLoadFailure.ContractVersionSkew);
        run.Failure!.Message.Should().Contain("Acme.HostShared");
    }
}

[TestFixture]
public class Given_a_declared_contract_the_plugin_references_and_the_host_cannot_resolve
{
    private TemporaryPluginRoot _root = null!;
    private PluginLoaderRun _run = null!;

    [SetUp]
    public void Setup()
    {
        // Acme.Private is shipped by the plugin and carried by no host, so naming it as a contract is a
        // host that declares something it cannot serve.
        _root = TemporaryPluginRoot.Create();
        _root.Add(PluginFixtures.PrivateDependency);
        _run = PluginLoaderProbe.RunExpectingFailure(
            _root.RootPath,
            PluginFixtures.PrivateDependency,
            contracts: ["EdFi.Api.Plugins", "Acme.Private"]
        );
    }

    [TearDown]
    public void TearDown() => _root.Dispose();

    [Test]
    public void It_refuses_and_names_the_contract()
    {
        // Skipping the comparison would leave the declaration silently unenforced, which is the exact
        // blindness the contract check exists to prevent.
        _run.Failure!.Reason.Should().Be(PluginLoadFailure.ContractAssemblyMissing);
        _run.Failure!.Message.Should().Contain("Acme.Private");
    }
}

[TestFixture]
public class Given_a_plugin_shipping_a_dependency_the_host_does_not_carry
{
    private TemporaryPluginRoot _root = null!;
    private PluginLoaderRun _run = null!;

    [SetUp]
    public void Setup()
    {
        _root = TemporaryPluginRoot.Create();
        _root.Add(PluginFixtures.PrivateDependency);
        _run = PluginLoaderProbe.Run(_root.RootPath, PluginFixtures.PrivateDependency);
    }

    [TearDown]
    public void TearDown() => _root.Dispose();

    [Test]
    public void It_loads_and_resolves_the_dependency_from_the_plugin_directory()
    {
        // What host-first gives up is a plugin overriding an assembly the host has. What it keeps is
        // this: a plugin still gets its own copy of anything the host does not carry, which is where
        // real collisions live.
        _run.Failure.Should().BeNull();

        Type pluginType = _run.Result!.Plugins[0].Instance.GetType();
        string described = (string)pluginType.GetMethod("DescribePrivateDependency")!.Invoke(null, null)!;

        described.Should().Be("Acme.Private 1.0.0");
    }

    [Test]
    public void It_resolves_that_dependency_inside_the_plugins_own_context()
    {
        Type pluginType = _run.Result!.Plugins[0].Instance.GetType();
        pluginType.GetMethod("DescribePrivateDependency")!.Invoke(null, null);

        AssemblyLoadContext pluginContext = AssemblyLoadContext.GetLoadContext(pluginType.Assembly)!;

        pluginContext.Assemblies.Select(assembly => assembly.GetName().Name).Should().Contain("Acme.Private");
    }
}

[TestFixture]
public class Given_a_plugin_declaring_a_higher_version_of_an_assembly_the_host_carries
{
    private TemporaryPluginRoot _root = null!;
    private PluginLoaderRun _run = null!;

    [SetUp]
    public void Setup()
    {
        _root = TemporaryPluginRoot.Create();
        _root.Add(PluginFixtures.DeclaredSkew);
        _run = PluginLoaderProbe.RunExpectingFailure(_root.RootPath, PluginFixtures.DeclaredSkew);
    }

    [TearDown]
    public void TearDown() => _root.Dispose();

    [Test]
    public void It_refuses_even_though_no_executed_code_path_touches_it()
    {
        // The Load override is called when a type resolution first needs an assembly, so a dependency no
        // hook happens to touch would otherwise be discovered on a request rather than at startup, and
        // "fatal at load" would be a promise this design does not keep.
        _run.Failure!.Reason.Should().Be(PluginLoadFailure.DependencyVersionSkew);
        _run.Failure!.Message.Should().Contain("Acme.HostShared").And.Contain("2.0.0").And.Contain("1.0.0");
    }

    [Test]
    public void It_refuses_before_the_plugin_is_constructed()
    {
        // Nothing was returned and the plugin's own copy was never served, which is the identity split
        // host-first resolution exists to prevent.
        _run.Result.Should().BeNull();
    }
}

[TestFixture]
public class Given_a_plugin_whose_skewed_reference_the_manifest_does_not_declare
{
    private TemporaryPluginRoot _root = null!;
    private PluginLoaderRun _run = null!;

    [SetUp]
    public void Setup()
    {
        _root = TemporaryPluginRoot.Create();
        _root.Add(PluginFixtures.BackstopCtor);
        _run = PluginLoaderProbe.RunExpectingFailure(_root.RootPath, PluginFixtures.BackstopCtor);
    }

    [TearDown]
    public void TearDown() => _root.Dispose();

    [Test]
    public void It_is_a_reference_the_preflight_cannot_see()
    {
        // The manifest declares nothing for it and the file is not in the plugin directory, which is
        // what makes this the Load override's case rather than the preflight's. This models the shared
        // framework, whose assemblies a framework-dependent publish also leaves undeclared.
        string manifest = File.ReadAllText(
            Path.Combine(
                _root.RootPath,
                PluginFixtures.BackstopCtor,
                $"{PluginFixtures.BackstopCtor}.deps.json"
            )
        );

        manifest.Should().NotContain("Acme.HostShared");
        File.Exists(Path.Combine(_root.RootPath, PluginFixtures.BackstopCtor, "Acme.HostShared.dll"))
            .Should()
            .BeFalse();
    }

    [Test]
    public void It_reports_the_named_skew_rather_than_the_runtime_wrapper()
    {
        // The runtime wraps anything thrown from inside the Load override in a FileLoadException whose
        // own message names only the version the host carries. An operator reading that would have no
        // idea what the plugin asked for, so the loader unwraps it.
        _run.Failure!.Reason.Should().Be(PluginLoadFailure.DependencyVersionSkew);
        _run.Failure!.Message.Should().Contain("requires Acme.HostShared >= 2.0.0");
        _run.Failure!.Message.Should().Contain("host carries 1.0.0");
        _run.Failure!.Message.Should().NotContain("0x80131500");
    }

    [Test]
    public void It_keeps_the_runtime_failure_as_the_cause()
    {
        _run.Failure!.InnerException.Should().NotBeNull();
    }
}

[TestFixture]
public class Given_a_plugin_carrying_its_own_copy_of_the_contract
{
    private TemporaryPluginRoot _root = null!;
    private PluginLoaderRun _run = null!;

    [SetUp]
    public void Setup()
    {
        _root = TemporaryPluginRoot.Create();
        _root.Add(PluginFixtures.Good);
        _run = PluginLoaderProbe.Run(_root.RootPath, PluginFixtures.Good);
    }

    [TearDown]
    public void TearDown() => _root.Dispose();

    [Test]
    public void It_really_does_carry_one()
    {
        // Every published plugin brings its own copy, so the assertion below is about which of two real
        // files was used rather than about a file that is not there.
        File.Exists(Path.Combine(_root.RootPath, PluginFixtures.Good, "EdFi.Api.Plugins.dll"))
            .Should()
            .BeTrue();
    }

    [Test]
    public void It_is_served_the_host_copy_instead()
    {
        // The same assembly instance, not merely the same version. Two identities for the contract would
        // mean the host could not see the plugin as an EdFiApiPlugin at all.
        _run.Failure.Should().BeNull();

        Assembly contractTheHostCarries = typeof(EdFiApiPlugin).Assembly;
        Assembly contractThePluginGot = _run.Result!.Plugins[0].Instance.GetType().BaseType!.Assembly;

        contractThePluginGot.Should().BeSameAs(contractTheHostCarries);
    }

    [Test]
    public void It_loads_the_plugin_into_a_non_collectible_context_named_for_it()
    {
        AssemblyLoadContext context = AssemblyLoadContext.GetLoadContext(
            _run.Result!.Plugins[0].Instance.GetType().Assembly
        )!;

        // Named for the plugin so a diagnostic taken from the runtime says which plugin an assembly
        // came from, and non-collectible because nothing unloads a plugin.
        context.Name.Should().Be(PluginFixtures.Good);
        context.IsCollectible.Should().BeFalse();
        context.Should().NotBeSameAs(AssemblyLoadContext.Default);
    }
}

/// <summary>
/// Records what the default context actually does, so the loader's simple-name approach rests on a
/// measurement rather than on an assumed exception type. If a future runtime makes the two
/// distinguishable, this fails and the approach is revisited deliberately rather than silently.
/// </summary>
[TestFixture]
public class Given_the_default_context_is_asked_for_an_assembly_it_cannot_serve
{
    [Test]
    public void It_throws_the_same_exception_for_an_absent_assembly_and_for_an_older_shared_framework_one()
    {
        Exception? absent = Capture("Acme.NoSuchAssembly.ThisHostHasNever.HeardOf");
        Exception? olderFramework = Capture("System.Runtime, Version=99.0.0.0");

        TestContext.Out.WriteLine($"absent: {Describe(absent)}");
        TestContext.Out.WriteLine($"older, shared framework: {Describe(olderFramework)}");

        // The measurement the loader's approach rests on, pinned exactly rather than loosely: the two
        // cases that need opposite treatment produce the identical exception, so a catch cannot tell
        // "the host does not have it" from "the host has an older one". If a future runtime makes them
        // distinguishable, this fails and the simple-name approach is revisited deliberately rather
        // than silently.
        absent.Should().BeOfType<FileNotFoundException>();
        olderFramework.Should().BeOfType<FileNotFoundException>();
    }

    [Test]
    public void It_quietly_serves_an_older_copy_of_an_application_dependency()
    {
        // A separate measurement, and a different shape from the one above. For an assembly the
        // application's own manifest declares, a higher-version request does not fail at all: the host
        // returns the copy it has. A loader that asked with a version and trusted the answer would be
        // handed an assembly older than the reference declared with nothing at all to catch, which is
        // the second reason the loader asks without a version and compares for itself.
        Exception? olderApplication = Capture("Acme.HostShared, Version=99.0.0.0");

        TestContext.Out.WriteLine($"older, application dependency: {Describe(olderApplication)}");

        olderApplication.Should().BeNull();

        AssemblyLoadContext
            .Default.LoadFromAssemblyName(new AssemblyName("Acme.HostShared, Version=99.0.0.0"))
            .GetName()
            .Version.Should()
            .Be(new Version(1, 0, 0, 0));
    }

    /// <summary>
    /// Asks the default context and reports what came back, including nothing at all. NUnit's Assert
    /// helpers all insist an exception was thrown, and "it did not throw" is one of the outcomes this
    /// measurement exists to record.
    /// </summary>
    private static Exception? Capture(string displayName)
    {
        try
        {
            AssemblyLoadContext.Default.LoadFromAssemblyName(new AssemblyName(displayName));
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static string Describe(Exception? exception) =>
        exception is null ? "no exception" : exception.GetType().Name;

    [Test]
    public void It_serves_the_host_copy_when_asked_by_simple_name_alone()
    {
        // Which is why the loader asks this way and compares versions itself: a request carrying a
        // version cannot fail on version grounds if it carries none.
        AssemblyLoadContext
            .Default.LoadFromAssemblyName(new AssemblyName("Acme.HostShared"))
            .GetName()
            .Version.Should()
            .Be(new Version(1, 0, 0, 0));
    }
}
