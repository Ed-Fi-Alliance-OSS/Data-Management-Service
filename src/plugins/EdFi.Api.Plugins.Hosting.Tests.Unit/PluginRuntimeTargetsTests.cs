// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.Api.Plugins.Hosting.Tests.Unit;

/// <summary>One managed asset declaration read out of a real published manifest.</summary>
internal sealed record ManagedDeclaration(string Library, string SimpleName, string? AssemblyVersion);

/// <summary>
/// The managed declarations of one published manifest, separated by where they are declared.
/// </summary>
/// <remarks>
/// Read here rather than in each test because the two collections answer the same question from
/// opposite sides: what a per-identifier row says, and what the top-level entry the skew preflight
/// reads says about the same simple name.
/// </remarks>
internal sealed record ManagedDeclarations(
    string TargetName,
    IReadOnlyList<ManagedDeclaration> RuntimeTargetsRows,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, string?>> TopLevelRowsByLibrary
)
{
    internal static ManagedDeclarations Read(string manifestPath)
    {
        using JsonDocument manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));

        string targetName = manifest
            .RootElement.GetProperty("runtimeTarget")
            .GetProperty("name")
            .GetString()!;

        JsonElement target = manifest.RootElement.GetProperty("targets").GetProperty(targetName);

        List<ManagedDeclaration> runtimeTargetsRows = [];
        Dictionary<string, IReadOnlyDictionary<string, string?>> topLevel = new(StringComparer.Ordinal);

        foreach (JsonProperty library in target.EnumerateObject())
        {
            topLevel[library.Name] = TopLevelRowsOf(library.Value);
            runtimeTargetsRows.AddRange(RuntimeTargetsRowsOf(library));
        }

        return new ManagedDeclarations(targetName, runtimeTargetsRows, topLevel);
    }

    /// <summary>The top-level <c>runtime</c> declarations of one library, by simple name.</summary>
    internal IReadOnlyDictionary<string, string?> TopLevelRowsIn(string library) =>
        TopLevelRowsByLibrary[library];

    private static IReadOnlyDictionary<string, string?> TopLevelRowsOf(JsonElement library)
    {
        Dictionary<string, string?> rows = new(StringComparer.Ordinal);

        if (!library.TryGetProperty("runtime", out JsonElement runtime))
        {
            return rows;
        }

        foreach (JsonProperty asset in runtime.EnumerateObject())
        {
            rows[Path.GetFileNameWithoutExtension(asset.Name)] = DeclaredVersionOf(asset.Value);
        }

        return rows;
    }

    private static IEnumerable<ManagedDeclaration> RuntimeTargetsRowsOf(JsonProperty library)
    {
        if (!library.Value.TryGetProperty("runtimeTargets", out JsonElement runtimeTargets))
        {
            yield break;
        }

        foreach (JsonProperty row in runtimeTargets.EnumerateObject())
        {
            // assetType is the discriminator, not the path: a native asset also lives under runtimes/
            // and carries no assembly version, and treating the two alike would compare a managed
            // declaration against something that has none.
            if (
                !row.Value.TryGetProperty("assetType", out JsonElement assetType)
                || assetType.GetString() != "runtime"
            )
            {
                continue;
            }

            yield return new ManagedDeclaration(
                library.Name,
                Path.GetFileNameWithoutExtension(row.Name),
                DeclaredVersionOf(row.Value)
            );
        }
    }

    private static string? DeclaredVersionOf(JsonElement declaration) =>
        declaration.TryGetProperty("assemblyVersion", out JsonElement version) ? version.GetString() : null;
}

/// <summary>
/// The publish shape the skew preflight's sufficiency rests on, measured rather than assumed.
/// </summary>
/// <remarks>
/// <para>
/// The preflight reads top-level <c>runtime</c> entries only. That is safe to the extent that a
/// managed <c>runtimeTargets</c> row repeats a top-level declaration of the same simple name at the
/// same <c>assemblyVersion</c>, because then nothing a per-identifier row could say is new. This
/// fixture is the measurement of that claim against a real package with runtime-identifier-specific
/// managed assets, published portable so the rows exist at all.
/// </para>
/// <para>
/// It is also where the claim's limit is recorded. Measured on Microsoft.Data.SqlClient 6.1.4, the
/// repetition holds for every managed row whose simple name appears at the top level, and one managed
/// row appears nowhere at the top level at all: <c>System.Diagnostics.EventLog.Messages</c>, from the
/// transitive <c>System.Diagnostics.EventLog/9.0.11</c>, declared at 9.0.0.0 in a <c>win</c> row and
/// in no top-level entry, beside a <c>System.Diagnostics.EventLog</c> row that does have one. The
/// preflight's top-level read is therefore incomplete rather than equivalent. That is a documented
/// policy about what to refuse, it is asserted below rather than hidden by a sweep written only over
/// the rows that happen to match, and it is not a reason to widen the production preflight past the
/// approved design.
/// </para>
/// </remarks>
[TestFixture]
public class Given_a_plugin_shipping_runtime_identifier_specific_managed_assets
{
    /// <summary>The package whose publish shape this fixture exists to measure.</summary>
    private const string SubjectPackage = "Microsoft.Data.SqlClient";

    private ManagedDeclarations _declarations = null!;

    [SetUp]
    public void Setup()
    {
        _declarations = ManagedDeclarations.Read(PluginFixtures.ManifestOf(PluginFixtures.RuntimeTargets));

        foreach (ManagedDeclaration row in _declarations.RuntimeTargetsRows)
        {
            TestContext.Out.WriteLine($"{row.Library}: {row.SimpleName} {row.AssemblyVersion}");
        }
    }

    [Test]
    public void It_is_published_portable_rather_than_for_one_identifier()
    {
        // The shape the criterion measures. A publish carrying a runtime identifier resolves the
        // per-identifier assets and emits no managed runtimeTargets rows at all, so a fixture built
        // that way would satisfy every assertion below over an empty collection.
        _declarations.TargetName.Should().Be(".NETCoreApp,Version=v10.0");
    }

    [Test]
    public void It_really_does_declare_managed_runtime_targets_rows()
    {
        _declarations.RuntimeTargetsRows.Should().NotBeEmpty();
    }

    [Test]
    public void It_repeats_the_top_level_version_for_the_package_this_fixture_is_built_on()
    {
        // The acceptance case, pinned to the package rather than left to the sweep below. That sweep
        // skips a row with no top-level counterpart, so on its own it would still pass if this package
        // lost its counterpart while some other library kept one, and the fixture would then measure
        // an invariant over somebody else's declarations.
        ManagedDeclaration[] rows =
        [
            .. _declarations.RuntimeTargetsRows.Where(row => row.SimpleName == SubjectPackage),
        ];

        rows.Should().NotBeEmpty();

        foreach (ManagedDeclaration row in rows)
        {
            IReadOnlyDictionary<string, string?> topLevel = _declarations.TopLevelRowsIn(row.Library);

            row.AssemblyVersion.Should().NotBeNull();
            topLevel.Should().ContainKey(SubjectPackage);
            row.AssemblyVersion.Should().Be(topLevel[SubjectPackage]);
        }
    }

    [Test]
    public void It_repeats_the_top_level_version_for_every_row_the_top_level_also_declares()
    {
        // The invariant the preflight's sufficiency actually rests on. Asserted per row rather than in
        // aggregate, so a single divergent declaration is named instead of being averaged away.
        List<ManagedDeclaration> matched = [];

        foreach (ManagedDeclaration row in _declarations.RuntimeTargetsRows)
        {
            if (!_declarations.TopLevelRowsIn(row.Library).TryGetValue(row.SimpleName, out string? topLevel))
            {
                continue;
            }

            row.AssemblyVersion.Should()
                .Be(
                    topLevel,
                    $"the runtimeTargets row for {row.SimpleName} in {row.Library} must not declare a "
                        + "version the top-level runtime entry does not, or the preflight's top-level "
                        + "read would miss a skew the manifest states"
                );

            matched.Add(row);
        }

        // Otherwise the loop above proves nothing: no matched row means no comparison happened.
        matched.Should().NotBeEmpty();
    }

    [Test]
    public void It_also_declares_a_managed_row_the_top_level_does_not_mention_at_all()
    {
        // The limit of the claim, kept as a measurement. Such a row is declared per identifier and
        // nowhere else, so the preflight cannot see its version at all: the top-level read is
        // incomplete rather than equivalent, which is a documented policy about what to refuse and not
        // an oversight. The inventory, a different question, still carries these rows.
        ManagedDeclaration[] unmatched =
        [
            .. _declarations.RuntimeTargetsRows.Where(row =>
                !_declarations.TopLevelRowsIn(row.Library).ContainsKey(row.SimpleName)
            ),
        ];

        foreach (ManagedDeclaration row in unmatched)
        {
            TestContext.Out.WriteLine(
                $"declared only per identifier: {row.Library}: {row.SimpleName} {row.AssemblyVersion}"
            );
        }

        unmatched.Should().NotBeEmpty();
    }
}

/// <summary>
/// The same manifest, read by the loader rather than by the test.
/// </summary>
/// <remarks>
/// What the preflight declines to read, the inventory still reports. The two answer different
/// questions - what to refuse, and what the directory contains - and the second is not narrowed to the
/// first.
/// </remarks>
[TestFixture]
public class Given_the_loader_inventories_runtime_identifier_specific_managed_assets
{
    private TemporaryPluginRoot _root = null!;
    private ManagedDeclarations _declarations = null!;
    private IReadOnlyList<PluginInventoryRow> _inventory = null!;

    [SetUp]
    public void Setup()
    {
        _declarations = ManagedDeclarations.Read(PluginFixtures.ManifestOf(PluginFixtures.RuntimeTargets));

        _root = TemporaryPluginRoot.Create();
        _root.Add(PluginFixtures.RuntimeTargets);

        // The one case over this fixture that has to get the plugin loaded, and the only one that
        // touches the manifest. Measured, this process carries System.Configuration.ConfigurationManager
        // 4.0.0.0 from the test platform while the package's closure declares 9.0.0.0, so the preflight
        // refuses the plugin exactly as designed. Lowering that declaration to the version this host
        // really carries is what a host serving SqlClient's closure would present, and it leaves every
        // runtimeTargets row untouched.
        foreach (string change in _root.LowerDeclarationsAboveHostVersions(PluginFixtures.RuntimeTargets))
        {
            TestContext.Out.WriteLine($"lowered to this host's version: {change}");
        }

        PluginLoaderRun run = PluginLoaderProbe.Run(_root.RootPath, PluginFixtures.RuntimeTargets);
        run.Failure.Should().BeNull();

        _inventory = run.Result!.Plugins.Single().MaterializeInventory();
    }

    [TearDown]
    public void TearDown() => _root.Dispose();

    [Test]
    public void It_carries_every_managed_runtime_targets_declaration()
    {
        foreach (ManagedDeclaration row in _declarations.RuntimeTargetsRows)
        {
            _inventory
                .Should()
                .Contain(
                    entry =>
                        entry.Kind == PluginFileKind.Managed
                        && Path.GetFileNameWithoutExtension(entry.FileName) == row.SimpleName,
                    $"the manifest declares {row.SimpleName} as a managed asset"
                );
        }
    }

    [Test]
    public void It_carries_the_row_the_preflight_cannot_see()
    {
        // Named as its own case because it is the one the two questions actually diverge on: declared
        // per identifier and nowhere else, invisible to the preflight, and present in the inventory.
        ManagedDeclaration[] unmatched =
        [
            .. _declarations.RuntimeTargetsRows.Where(row =>
                !_declarations.TopLevelRowsIn(row.Library).ContainsKey(row.SimpleName)
            ),
        ];

        unmatched.Should().NotBeEmpty();

        foreach (ManagedDeclaration row in unmatched)
        {
            _inventory
                .Should()
                .Contain(entry => Path.GetFileNameWithoutExtension(entry.FileName) == row.SimpleName);
        }
    }
}
