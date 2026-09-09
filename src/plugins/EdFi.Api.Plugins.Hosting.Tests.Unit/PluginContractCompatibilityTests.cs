// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.Api.Plugins.Hosting.Tests.Unit;

/// <summary>What one run of the 1.1 host reported, as the runner serialised it.</summary>
internal sealed record HostRunnerResult(
    string HostContractVersion,
    string PluginName,
    string PluginBaseContractVersion,
    bool AddedVirtualVisible,
    string? HookMarkerPluginName,
    HostRunnerSubstitution[] Substitutions
);

/// <summary>One host-first substitution the run recorded.</summary>
internal sealed record HostRunnerSubstitution(
    string AssemblyName,
    string? RequestedVersion,
    string HostVersion,
    string? DeclaredVersion
);

/// <summary>
/// Stages a private copy of the host runner whose default context carries contract 1.1.0, and runs it.
/// </summary>
/// <remarks>
/// The staged runner is published against the production contract at 1.0.0, like any host. Making a
/// 1.1 host out of it is done to a copy, in a temporary directory of its own, so no shared staged
/// output is ever the thing whose identity is rewritten and two cases cannot observe each other's
/// staging.
/// </remarks>
internal sealed class TemporaryHostRunner : IDisposable
{
    /// <summary>
    /// Generous, because the work is a process start, a restore-free load and a plugin load, and a
    /// build machine is slow at all three. It bounds a hang; it is not a performance assertion.
    /// </summary>
    private static readonly TimeSpan _deadline = TimeSpan.FromMinutes(2);

    private readonly string _base;

    private TemporaryHostRunner(string basePath, string runnerPath)
    {
        _base = basePath;
        RunnerPath = runnerPath;
    }

    /// <summary>The copied runner assembly to execute.</summary>
    internal string RunnerPath { get; }

    /// <summary>
    /// Copies the staged runner, and unless <paramref name="substituteContract"/> is false replaces
    /// that copy's contract assembly with the additive 1.1 build and rewrites its manifest rows.
    /// </summary>
    internal static TemporaryHostRunner Create(bool substituteContract = true)
    {
        string basePath = Path.Combine(Path.GetTempPath(), "edfi-plugin-hosts", Guid.NewGuid().ToString("N"));
        string runnerDirectory = Path.Combine(basePath, PluginFixtures.HostRunnerName);

        CopyDirectory(PluginFixtures.HostRunnerDirectory, runnerDirectory);

        if (substituteContract)
        {
            // The whole substitution: one file, and the two version rows in the manifest that describe
            // it. Rewritten with System.Text.Json against a real published manifest rather than by text
            // replacement, so every other byte the publish produced survives.
            File.Copy(
                PluginFixtures.Contract11Assembly,
                Path.Combine(runnerDirectory, "EdFi.Api.Plugins.dll"),
                overwrite: true
            );

            RewriteContractVersionRows(
                Path.Combine(runnerDirectory, $"{PluginFixtures.HostRunnerName}.deps.json"),
                "1.1.0.0"
            );
        }

        return new TemporaryHostRunner(
            basePath,
            Path.Combine(runnerDirectory, $"{PluginFixtures.HostRunnerName}.dll")
        );
    }

    /// <summary>Runs the copied runner against a plugin root, under a deadline.</summary>
    internal ChildProcess.Result Run(string pluginRoot, string pluginName)
    {
        ProcessStartInfo start = new()
        {
            FileName = Environment.ProcessPath ?? "dotnet",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        // The test host is itself dotnet, so its own path runs the runner. Falling back to the name on
        // PATH covers a host that is something else.
        if (
            !Path.GetFileNameWithoutExtension(start.FileName)
                .Equals("dotnet", StringComparison.OrdinalIgnoreCase)
        )
        {
            start.FileName = "dotnet";
        }

        start.ArgumentList.Add(RunnerPath);
        start.ArgumentList.Add(pluginRoot);
        start.ArgumentList.Add(pluginName);

        return ChildProcess.Run(start, _deadline, "The 1.1 contract host");
    }

    /// <summary>Reads the runner's serialised result out of everything it wrote.</summary>
    internal static HostRunnerResult ResultOf(ChildProcess.Result run)
    {
        const string prefix = "RESULT ";

        string? line = Array.Find(
            run.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            candidate => candidate.StartsWith(prefix, StringComparison.Ordinal)
        );

        if (line is null)
        {
            throw new AssertionException(
                $"The host wrote no result line. Exit code {run.ExitCode}. Output: {run.Output}"
            );
        }

        return JsonSerializer.Deserialize<HostRunnerResult>(
            line[prefix.Length..],
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
        )!;
    }

    private static void RewriteContractVersionRows(string manifestPath, string version)
    {
        JsonNode manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!;
        string targetName = manifest["runtimeTarget"]!["name"]!.GetValue<string>();
        bool rewritten = false;

        foreach (KeyValuePair<string, JsonNode?> library in manifest["targets"]![targetName]!.AsObject())
        {
            if (library.Value?["runtime"] is not JsonObject runtime)
            {
                continue;
            }

            foreach (KeyValuePair<string, JsonNode?> asset in runtime)
            {
                if (
                    !Path.GetFileNameWithoutExtension(asset.Key)
                        .Equals("EdFi.Api.Plugins", StringComparison.Ordinal)
                    || asset.Value is not JsonObject declaration
                )
                {
                    continue;
                }

                declaration["assemblyVersion"] = version;
                declaration["fileVersion"] = version;
                rewritten = true;
            }
        }

        if (!rewritten)
        {
            throw new AssertionException(
                $"'{manifestPath}' declares no runtime asset for EdFi.Api.Plugins, so the staged host "
                    + "cannot be given a different contract identity."
            );
        }

        File.WriteAllText(manifestPath, manifest.ToJsonString());
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_base, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The run is over and the process is gone, but a file can still be held briefly on
            // Windows. Leaving a temporary directory behind is not worth failing a passing test over.
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);

        foreach (string file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        }

        foreach (string directory in Directory.EnumerateDirectories(source))
        {
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
        }
    }
}

/// <summary>
/// Loads one assembly by path while everything it depends on comes from the host.
/// </summary>
/// <remarks>
/// Collectible, because it exists only to be reflected over and then discarded. It is not the
/// production load context and makes no claim about resolution policy: it is the smallest way to have
/// two assembly versions of one name in this process at once.
/// </remarks>
internal sealed class HostFirstProbeContext : AssemblyLoadContext, IDisposable
{
    internal HostFirstProbeContext()
        : base("contract-surface-probe", isCollectible: true) { }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (assemblyName.Name is not { } simpleName)
        {
            return null;
        }

        try
        {
            return Default.LoadFromAssemblyName(new AssemblyName(simpleName));
        }
        catch (FileNotFoundException)
        {
            // Nothing here ships a private copy of anything, so an assembly the host does not carry is
            // left to the runtime's own probing rather than answered wrongly.
            return null;
        }
    }

    public void Dispose() => Unload();
}

/// <summary>
/// The additive 1.1 contract fixture, against the real production contract it claims to extend.
/// </summary>
/// <remarks>
/// The 1.0-on-1.1 proof is only worth anything if the 1.1 assembly really is the production contract
/// plus an addition. A fixture that had quietly drifted into a different surface would still load a
/// 1.0 plugin and still report a substitution, and the proof would say nothing about compatibility. So
/// the superset relation is asserted rather than assumed, member by member and signature by signature.
/// </remarks>
[TestFixture]
public class Given_the_additive_contract_fixture
{
    private Type _production = null!;
    private Type _fixture = null!;
    private HostFirstProbeContext _fixtureContext = null!;

    [SetUp]
    public void Setup()
    {
        _production = typeof(EdFiApiPlugin);

        // Its own context, because the default one already holds the production contract under the same
        // assembly name and the two versions cannot both be there.
        _fixtureContext = new HostFirstProbeContext();

        _fixture = _fixtureContext
            .LoadFromAssemblyPath(PluginFixtures.Contract11Assembly)
            .GetType("EdFi.Api.Plugins.EdFiApiPlugin")!;
    }

    /// <summary>
    /// Signature as a string, so members from two assembly versions can be compared at all.
    /// </summary>
    /// <remarks>
    /// The two types come from different load contexts, so their parameter types are different
    /// <see cref="Type"/> instances however identical the signature is; comparing the instances would
    /// report every member as different. Names are what the comparison can be made on, and they are
    /// what a compiler binds against.
    /// </remarks>
    private static IEnumerable<string> PublicSurfaceOf(Type type) =>
        type.GetMembers(
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly
            )
            .Select(Describe)
            .Order(StringComparer.Ordinal);

    private static string Describe(MemberInfo member) =>
        member switch
        {
            MethodInfo method => $"method {method.ReturnType.FullName} {method.Name}("
                + string.Join(", ", method.GetParameters().Select(p => p.ParameterType.FullName))
                + $") abstract={method.IsAbstract} virtual={method.IsVirtual} static={method.IsStatic}",
            PropertyInfo property => $"property {property.PropertyType.FullName} {property.Name} "
                + $"get={property.CanRead} set={property.CanWrite}",
            ConstructorInfo constructor => $"ctor("
                + string.Join(", ", constructor.GetParameters().Select(p => p.ParameterType.FullName))
                + ")",
            _ => $"{member.MemberType} {member.Name}",
        };

    [TearDown]
    public void TearDown() => _fixtureContext.Dispose();

    [Test]
    public void It_is_a_different_assembly_version_of_the_same_assembly_name()
    {
        // The premise of the whole proof. One assembly name, two versions; if the fixture were a
        // differently named assembly nothing would ever be substituted for anything.
        _fixture.Assembly.GetName().Name.Should().Be(_production.Assembly.GetName().Name);
        _production.Assembly.GetName().Version.Should().Be(new Version(1, 0, 0, 0));
        _fixture.Assembly.GetName().Version.Should().Be(new Version(1, 1, 0, 0));
    }

    [Test]
    public void It_keeps_every_public_member_the_production_contract_declares()
    {
        string[] production = [.. PublicSurfaceOf(_production)];
        string[] fixture = [.. PublicSurfaceOf(_fixture)];

        TestContext.Out.WriteLine($"production 1.0.0: {string.Join(" | ", production)}");
        TestContext.Out.WriteLine($"fixture 1.1.0:    {string.Join(" | ", fixture)}");

        // Superset, member for member and signature for signature. An added member is allowed; a
        // removed or altered one is what the additive-only policy forbids and what this catches.
        production.Should().NotBeEmpty();
        fixture.Should().Contain(production);
    }

    [Test]
    public void It_adds_exactly_one_no_op_virtual()
    {
        string[] added =
        [
            .. PublicSurfaceOf(_fixture).Except(PublicSurfaceOf(_production), StringComparer.Ordinal),
        ];

        TestContext.Out.WriteLine($"added: {string.Join(" | ", added)}");

        // One addition, and a virtual rather than an abstract: a new abstract member would break every
        // plugin already published, which is exactly what the policy rules out.
        added.Should().HaveCount(1);
        added[0]
            .Should()
            .Contain("DescribeCapabilities")
            .And.Contain("abstract=False")
            .And.Contain("virtual=True");
    }
}

/// <summary>
/// A plugin compiled against contract 1.0.0, loaded by the real loader in a host carrying 1.1.0.
/// </summary>
/// <remarks>
/// Draft 02's criterion taken literally. A unit-test process cannot be the host, because this
/// assembly's own default context already holds the production contract at 1.0.0 through a project
/// reference, so the proof runs in a process of its own whose contract assembly has been replaced in a
/// private copy of its publish output.
/// </remarks>
[TestFixture]
public class Given_a_plugin_built_against_the_older_contract_on_a_newer_host
{
    private TemporaryPluginRoot _root = null!;
    private TemporaryHostRunner _host = null!;
    private ChildProcess.Result _run = null!;
    private HostRunnerResult _result = null!;

    [SetUp]
    public void Setup()
    {
        _root = TemporaryPluginRoot.Create();
        _root.Add(PluginFixtures.OldContract);

        _host = TemporaryHostRunner.Create();
        _run = _host.Run(_root.RootPath, PluginFixtures.OldContract);

        TestContext.Out.WriteLine($"exit {_run.ExitCode}: {_run.Output}");

        // Asserted here because every case below reads the result, and a non-zero exit with the child's
        // whole output attached is the diagnosable failure rather than a deserialisation error.
        _run.ExitCode.Should().Be(0, $"the host wrote: {_run.Output}");

        _result = TemporaryHostRunner.ResultOf(_run);
    }

    [TearDown]
    public void TearDown()
    {
        _host.Dispose();
        _root.Dispose();
    }

    [Test]
    public void It_really_is_a_1_1_host()
    {
        // Read out of the running process's own default context before it loaded anything, which is
        // what makes the staged identity a fact of the run instead of an assumption about staging.
        _result.HostContractVersion.Should().Be("1.1.0.0");
    }

    [Test]
    public void It_loads_the_plugin_and_names_it()
    {
        _result.PluginName.Should().Be(PluginFixtures.OldContract);
    }

    [Test]
    public void It_serves_the_plugin_the_hosts_newer_contract()
    {
        // The plugin ships its own 1.0.0 copy and declares 1.0.0, and its base type came from the
        // host's 1.1.0 assembly instead. That is host-first resolving the contract, and it is the
        // direction the upgrade story needs.
        _result.PluginBaseContractVersion.Should().Be("1.1.0.0");
    }

    [Test]
    public void It_exposes_the_member_only_the_newer_contract_declares()
    {
        // Found and invoked by the runner, so this is "present and does nothing" rather than a claim
        // about a member nobody called. A plugin served its own 1.0.0 copy could not have it at all.
        _result.AddedVirtualVisible.Should().BeTrue();
    }

    [Test]
    public void It_runs_the_hook_the_older_contract_declared()
    {
        // Resolved out of a real provider in that process. A plugin that loaded but whose hook no
        // longer ran would be a compatibility break that every other assertion here would miss.
        _result.HookMarkerPluginName.Should().Be(PluginFixtures.OldContract);
    }

    [Test]
    public void It_records_the_contract_substitution_it_made()
    {
        HostRunnerSubstitution substitution = _result
            .Substitutions.Should()
            .ContainSingle(row => row.AssemblyName == "EdFi.Api.Plugins")
            .Subject;

        substitution.RequestedVersion.Should().Be("1.0.0.0");
        substitution.HostVersion.Should().Be("1.1.0.0");
        substitution.DeclaredVersion.Should().Be("1.0.0.0");
    }
}

/// <summary>
/// The same runner without the contract substitution, which is the control on its self-verification.
/// </summary>
/// <remarks>
/// The proof above rests entirely on the host really carrying 1.1.0, and the only thing that
/// establishes it is the runner refusing to proceed otherwise. If that refusal did not work, a staging
/// step that silently failed would turn the whole case into a 1.0-on-1.0 run reporting success. This
/// runs the unmodified staged output to show the refusal happens and says so by name.
/// </remarks>
[TestFixture]
public class Given_the_host_runner_was_not_given_the_newer_contract
{
    private TemporaryPluginRoot _root = null!;
    private TemporaryHostRunner _host = null!;
    private ChildProcess.Result _run = null!;

    [SetUp]
    public void Setup()
    {
        _root = TemporaryPluginRoot.Create();
        _root.Add(PluginFixtures.OldContract);

        _host = TemporaryHostRunner.Create(substituteContract: false);
        _run = _host.Run(_root.RootPath, PluginFixtures.OldContract);

        TestContext.Out.WriteLine($"exit {_run.ExitCode}: {_run.Output}");
    }

    [TearDown]
    public void TearDown()
    {
        _host.Dispose();
        _root.Dispose();
    }

    [Test]
    public void It_refuses_before_loading_anything()
    {
        _run.ExitCode.Should().Be(3, $"the host wrote: {_run.Output}");
    }

    [Test]
    public void It_names_the_identity_it_required_and_the_one_it_found()
    {
        _run.Output.Should().Contain("HOST CONTRACT MISMATCH");
        _run.Output.Should().Contain("1.1.0.0");
        _run.Output.Should().Contain("1.0.0.0");
    }

    [Test]
    public void It_reports_no_result_at_all()
    {
        // Nothing was loaded, so there is nothing to report. A run that both refused and reported would
        // mean the refusal came too late to protect anything.
        _run.Output.Should().NotContain("RESULT ");
    }
}
