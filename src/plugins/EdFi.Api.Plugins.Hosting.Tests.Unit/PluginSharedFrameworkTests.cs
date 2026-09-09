// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Runtime.Loader;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace EdFi.Api.Plugins.Hosting.Tests.Unit;

/// <summary>
/// A plugin taking the hook signature's assemblies from the shared framework rather than packages.
/// </summary>
/// <remarks>
/// <para>
/// The evidence boundary matters more here than anywhere else in this suite, so it is stated rather
/// than left to be inferred. What this fixture proves is that those assemblies appear nowhere in the
/// manifest and nowhere in the plugin directory, so the skew preflight cannot see them; that the
/// loader's own <c>Load</c> override is what answers for each of them rather than the runtime's
/// fallback; and that what it serves is the host's own assembly instance out of the default context.
/// </para>
/// <para>
/// What it does not prove is a <em>refusal</em>, and it is not evidence of a directly measured
/// shared-framework version skew. That would need a plugin compiled against a newer shared framework
/// than the host runs, and with one installed runtime there is no such compilation. The refusal half
/// belongs to <c>Given_a_plugin_whose_skewed_reference_the_manifest_does_not_declare</c> in
/// PluginLoaderVersionTests, over Acme.BackstopCtor, whose suppressed manifest row reproduces the one
/// property that matters - a reference the preflight cannot see - on an ordinary assembly, and to the
/// hook case below. Neither fixture is evidence for the other's claim.
/// </para>
/// </remarks>
[TestFixture]
public class Given_a_plugin_taking_the_hook_signature_from_the_shared_framework
{
    private const string ServiceCollectionAssembly = "Microsoft.Extensions.DependencyInjection.Abstractions";

    private const string ConfigurationAssembly = "Microsoft.Extensions.Configuration.Abstractions";

    /// <summary>The two assemblies the hook signature names, which this plugin does not ship.</summary>
    private static readonly string[] SignatureAssemblies = [ServiceCollectionAssembly, ConfigurationAssembly];

    /// <summary>
    /// The same two, each paired with the plugin method whose body resolves a type out of it.
    /// </summary>
    /// <remarks>
    /// The method matters as much as the name. Resolution has to be driven by the plugin's own code,
    /// because asking the context to load the assembly directly would be the test resolving it rather
    /// than the plugin, and the claim is about the plugin's first use.
    /// </remarks>
    private static readonly object[] SignatureAssemblyUses =
    [
        new object[] { ServiceCollectionAssembly, "ServiceCollectionType" },
        new object[] { ConfigurationAssembly, "ConfigurationType" },
    ];

    /// <summary>
    /// A name this plugin does not reference, watched alongside the two that matter.
    /// </summary>
    /// <remarks>
    /// The control. An observer that reported every watched name as served would satisfy the two
    /// assertions below without observing anything, so one watched name has to come back unserved.
    /// </remarks>
    private const string UnusedAssembly = "Acme.HostShared";

    private TemporaryPluginRoot _root = null!;
    private PluginLoaderRun _run = null!;
    private Type _pluginType = null!;
    private HostResolutionObserver _observer = null!;

    [SetUp]
    public void Setup()
    {
        _root = TemporaryPluginRoot.Create();
        _root.Add(PluginFixtures.FrameworkOnly);

        // Named by this test and nothing wider: the two assemblies the hook signature carries, plus one
        // the plugin never asks for. The production loader creates its contexts with no observer at
        // all, so nothing outside this run records anything.
        _observer = new HostResolutionObserver([.. SignatureAssemblies, UnusedAssembly]);

        _run = PluginLoaderProbe.Run(_root.RootPath, PluginFixtures.FrameworkOnly, observer: _observer);
        _run.Failure.Should().BeNull();

        _pluginType = _run.Result!.Plugins.Single().Instance.GetType();
    }

    [TearDown]
    public void TearDown() => _root.Dispose();

    private static IEnumerable<string> DeclaredRuntimeSimpleNames(string manifestPath)
    {
        using JsonDocument manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));

        string targetName = manifest
            .RootElement.GetProperty("runtimeTarget")
            .GetProperty("name")
            .GetString()!;

        foreach (
            JsonProperty library in manifest
                .RootElement.GetProperty("targets")
                .GetProperty(targetName)
                .EnumerateObject()
        )
        {
            if (!library.Value.TryGetProperty("runtime", out JsonElement runtime))
            {
                continue;
            }

            foreach (JsonProperty asset in runtime.EnumerateObject())
            {
                yield return Path.GetFileNameWithoutExtension(asset.Name);
            }
        }
    }

    /// <summary>Runs the plugin's own accessor, which is what forces the resolution.</summary>
    private Type ResolvedByThePlugin(string accessor) =>
        (Type)_pluginType.GetMethod(accessor)!.Invoke(null, null)!;

    [Test]
    public void It_declares_neither_of_them_in_its_manifest()
    {
        // Not "the manifest has one library": this fixture references the contract by project, so the
        // contract is a manifest entry and whatever else the publish emits is enumerated here rather
        // than assumed. The precise fact is that these two names are not among the declarations.
        string[] declared =
        [
            .. DeclaredRuntimeSimpleNames(PluginFixtures.ManifestOf(PluginFixtures.FrameworkOnly)),
        ];

        TestContext.Out.WriteLine($"declared runtime assets: {string.Join(", ", declared)}");

        declared.Should().NotBeEmpty();
        declared.Should().NotContain(SignatureAssemblies);
    }

    [TestCaseSource(nameof(SignatureAssemblies))]
    public void It_ships_no_file_for_them_either(string simpleName)
    {
        // A declaration the preflight cannot see and a file the resolver could still find would be a
        // different situation, so both halves are asserted.
        File.Exists(Path.Combine(_root.RootPath, PluginFixtures.FrameworkOnly, $"{simpleName}.dll"))
            .Should()
            .BeFalse();
    }

    [Test]
    public void It_is_served_the_hosts_own_instances_when_it_uses_them()
    {
        // Resolved from inside the plugin: each method body carries a type reference in the plugin
        // assembly's own metadata, so the answer is what the plugin's context produced rather than
        // what this test project happens to hold.
        ResolvedByThePlugin("ServiceCollectionType").Should().BeSameAs(typeof(IServiceCollection));
        ResolvedByThePlugin("ConfigurationType").Should().BeSameAs(typeof(IConfiguration));
    }

    [TestCaseSource(nameof(SignatureAssemblyUses))]
    public void It_reaches_the_loaders_own_override(string simpleName, string accessor)
    {
        // The half a comment cannot stand in for. An assembly the plugin does not ship would arrive
        // from the default context whether the override answered for it or declined and let the runtime
        // fall back, and it leaves no substitution row either, because there is no declared version for
        // the host's to have differed from. Only the override itself can say, so this run asks it.
        //
        // Measured, and not what I first assumed: both names are already served by the time Load
        // returns, so this is not a false-then-true transition and no such condition is manufactured.
        // The cause is that this plugin overrides ContributeServices, and matching an override to its
        // base virtual slot resolves the override's parameter types when the type is loaded, which the
        // loader's own candidate scan does before the plugin is constructed. The fact required is the
        // same either way, and it holds across the plugin's own use as well.
        _observer.HasServed(simpleName).Should().BeTrue();

        ResolvedByThePlugin(accessor);

        _observer.HasServed(simpleName).Should().BeTrue();
    }

    [Test]
    public void It_reports_nothing_for_a_watched_name_the_plugin_never_asked_for()
    {
        // Same observer, same run, a name the plugin does not reference: unserved. Without this an
        // observer that said yes to everything would pass the cases above.
        _observer.HasServed(UnusedAssembly).Should().BeFalse();
    }

    [Test]
    public void It_refuses_to_answer_about_a_name_it_was_not_watching()
    {
        // A quiet false for an unwatched name would let a caller read "never observed" as "never
        // resolved", which is the one wrong answer this observation must not give.
        Assert.Throws<InvalidOperationException>(() =>
            _observer.HasServed("Microsoft.Extensions.Primitives")
        );
    }

    [TestCaseSource(nameof(SignatureAssemblyUses))]
    public void It_still_declares_nothing_for_them_after_serving_them(string simpleName, string accessor)
    {
        // The two facts have to hold together, or the fixture would prove the override was consulted
        // for something the preflight could have caught instead. Nothing the resolution does adds a
        // declaration or a substitution row: there is no manifest version to have differed from.
        ResolvedByThePlugin(accessor);

        _run.Result!.Plugins.Single()
            .DeclaredFiles.Should()
            .NotContain(file => Path.GetFileNameWithoutExtension(file.FileName) == simpleName);

        _run.Result!.Plugins.Single()
            .MaterializeSubstitutions()
            .Should()
            .NotContain(substitution => substitution.AssemblyName == simpleName);
    }

    [TestCaseSource(nameof(SignatureAssemblyUses))]
    public void It_takes_them_from_the_default_context_rather_than_its_own(string simpleName, string accessor)
    {
        // Provenance, read off the assembly the plugin's own resolution produced rather than off one
        // this test asked for. simpleName is asserted too, so a fixture whose accessor stopped naming
        // the assembly it is paired with fails here instead of passing on the other one.
        Type resolved = ResolvedByThePlugin(accessor);

        resolved.Assembly.GetName().Name.Should().Be(simpleName);
        AssemblyLoadContext.GetLoadContext(resolved.Assembly).Should().BeSameAs(AssemblyLoadContext.Default);
    }

    [Test]
    public void It_runs_its_hook_against_the_shared_types()
    {
        ServiceCollection services = [];

        _run.Result!.Plugins.Single()
            .Instance.ContributeServices(services, new ConfigurationBuilder().Build());

        // One registration, of a plugin type, added through the host's own IServiceCollection. A split
        // identity for either signature assembly would have failed before this point.
        services.Should().ContainSingle();
    }
}

/// <summary>
/// The same suppressed manifest row as Acme.BackstopCtor, touched after the loader has returned.
/// </summary>
/// <remarks>
/// The characterisation this fixture exists for is a negative one: there is no loader frame above a
/// contribution hook, so nothing unwraps the runtime's <c>FileLoadException</c> and no friendly
/// message naming both versions is produced. Claiming otherwise would be false, and the loader is not
/// changed to reach into a call it does not make. Draft 03 owns invocation and is where a diagnostic
/// for this case would belong.
/// </remarks>
[TestFixture]
public class Given_a_skewed_reference_first_touched_after_the_loader_returned
{
    private TemporaryPluginRoot _root = null!;
    private PluginLoaderRun _run = null!;
    private Exception _failure = null!;

    [SetUp]
    public void Setup()
    {
        _root = TemporaryPluginRoot.Create();
        _root.Add(PluginFixtures.BackstopHook);

        _run = PluginLoaderProbe.Run(_root.RootPath, PluginFixtures.BackstopHook);

        // Called directly rather than through reflection, so what the assertions see is the exception
        // the runtime raised and not a TargetInvocationException wrapped around it. Captured rather
        // than asserted here, because the exception's own type is one of the facts under test.
        try
        {
            _run.Result!.Plugins.Single()
                .Instance.ContributeServices(new ServiceCollection(), new ConfigurationBuilder().Build());

            throw new AssertionException(
                "Expected the hook to fail on the skewed reference, and it returned normally."
            );
        }
        catch (Exception exception) when (exception is not AssertionException)
        {
            _failure = exception;
        }

        TestContext.Out.WriteLine($"after the loader returned: {_failure}");
    }

    [TearDown]
    public void TearDown() => _root.Dispose();

    [Test]
    public void It_loads_without_complaint()
    {
        // The preflight reads the manifest and the manifest says nothing about this reference, so there
        // is nothing to refuse at load. That is the same blind spot Acme.BackstopCtor has; the only
        // difference is when the reference is first needed.
        _run.Failure.Should().BeNull();
        _run.DiagnosticLines.Should()
            .Contain(line => line.Contains($"plugin '{PluginFixtures.BackstopHook}' loaded"));
    }

    [Test]
    public void It_fails_with_the_runtimes_own_wrapper_rather_than_a_loader_diagnostic()
    {
        // The honest characterisation. The refusal still happens - host-first has not been bypassed -
        // but the caller is the hook, not the loader, so what reaches it is the runtime's wrapper.
        _failure.Should().BeOfType<FileLoadException>();
        _failure.Message.Should().Contain("Acme.HostShared");
    }

    [Test]
    public void It_carries_the_named_skew_only_as_an_inner_cause()
    {
        // The information an operator wants is present but unformatted, which is exactly the gap. No
        // loader-time friendly report exists for this case and none is claimed.
        _failure.InnerException.Should().BeOfType<PluginVersionSkewException>();
        _failure.Message.Should().NotContain("requires Acme.HostShared >= 2.0.0");
    }
}
