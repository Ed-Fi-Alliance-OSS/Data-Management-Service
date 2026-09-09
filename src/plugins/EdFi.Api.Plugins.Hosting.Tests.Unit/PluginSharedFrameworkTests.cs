// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Reflection;
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
/// than left to be inferred. What this fixture proves is <em>provenance and resolution</em>: those
/// assemblies appear nowhere in the manifest and nowhere in the plugin directory, so the skew
/// preflight cannot see them, and when the plugin uses them it is the host's own assembly instances it
/// gets.
/// </para>
/// <para>
/// What it does not prove is a <em>refusal</em>. A genuine shared-framework version skew would need a
/// plugin compiled against a newer shared framework than the host runs, and with one installed runtime
/// there is no such compilation. The refusal half belongs to
/// <c>Given_a_plugin_whose_skewed_reference_the_manifest_does_not_declare</c> in
/// PluginLoaderVersionTests, over Acme.BackstopCtor, whose suppressed manifest row reproduces the one
/// property that matters - a reference the preflight cannot see - on an ordinary assembly. Neither
/// fixture is evidence for the other's claim.
/// </para>
/// </remarks>
[TestFixture]
public class Given_a_plugin_taking_the_hook_signature_from_the_shared_framework
{
    /// <summary>The two assemblies the hook signature names, which this plugin does not ship.</summary>
    private static readonly string[] SignatureAssemblies =
    [
        "Microsoft.Extensions.DependencyInjection.Abstractions",
        "Microsoft.Extensions.Configuration.Abstractions",
    ];

    private TemporaryPluginRoot _root = null!;
    private PluginLoaderRun _run = null!;
    private Type _pluginType = null!;

    [SetUp]
    public void Setup()
    {
        _root = TemporaryPluginRoot.Create();
        _root.Add(PluginFixtures.FrameworkOnly);

        _run = PluginLoaderProbe.Run(_root.RootPath, PluginFixtures.FrameworkOnly);
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

    [Test]
    public void It_declares_neither_of_them_in_its_manifest()
    {
        // Not "the manifest has one library": this fixture references the contract by project, so the
        // contract is a manifest entry and whatever else the publish emits is enumerated here rather
        // than assumed. The precise fact is that these two names are not among the declarations.
        string[] declared = DeclaredRuntimeSimpleNames(
                PluginFixtures.ManifestOf(PluginFixtures.FrameworkOnly)
            )
            .ToArray();

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
        Type serviceCollection = (Type)_pluginType.GetMethod("ServiceCollectionType")!.Invoke(null, null)!;
        Type configuration = (Type)_pluginType.GetMethod("ConfigurationType")!.Invoke(null, null)!;

        serviceCollection.Should().BeSameAs(typeof(IServiceCollection));
        configuration.Should().BeSameAs(typeof(IConfiguration));
    }

    [TestCaseSource(nameof(SignatureAssemblies))]
    public void It_takes_them_from_the_default_context_rather_than_its_own(string simpleName)
    {
        // The provenance claim stated exactly. Both the loader's Load override and, had it declined,
        // the runtime's own fallback would end at the default context for an assembly the plugin does
        // not ship, so this asserts where the assembly came from and not which of those two produced
        // it. That the override is genuinely consulted for an undeclared reference is Acme.BackstopCtor's
        // evidence, where declining would have produced a private copy instead of a refusal.
        Assembly resolved = _pluginType
            .Assembly.GetReferencedAssemblies()
            .Where(reference => reference.Name == simpleName)
            .Select(reference =>
                AssemblyLoadContext.GetLoadContext(_pluginType.Assembly)!.LoadFromAssemblyName(reference)
            )
            .Single();

        AssemblyLoadContext.GetLoadContext(resolved).Should().BeSameAs(AssemblyLoadContext.Default);
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
