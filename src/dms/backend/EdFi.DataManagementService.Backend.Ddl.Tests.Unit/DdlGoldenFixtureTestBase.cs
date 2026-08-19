// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.RegularExpressions;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Ddl.Tests.Unit;

/// <summary>
/// Base class for golden-file fixture tests. Subclasses provide the fixture path;
/// this class runs the pipeline and asserts all standard artifacts are emitted and match expected/.
/// Assertions are driven by <see cref="FixtureConfig"/> so only declared artifacts are checked.
/// </summary>
public abstract class DdlGoldenFixtureTestBase
{
    private string _fixtureDirectory = default!;
    private string _actualDir = default!;
    private FixtureConfig _config = default!;
    private FixtureCompareResult _result = default!;

    /// <summary>
    /// Directory containing the freshly generated artifacts for the current fixture run.
    /// Exposed for subclasses that want to assert against generated output (not checked-in expected/).
    /// </summary>
    protected string ActualDirectory => _actualDir;

    /// <summary>
    /// Reads the contents of a file emitted into the fixture's actual/ directory.
    /// </summary>
    protected string ReadActual(string relativeFileName) =>
        File.ReadAllText(Path.Combine(_actualDir, relativeFileName));

    protected abstract string ResolveFixtureDirectory(string projectRoot);

    /// <summary>
    /// Whether the fixture runs through the strict pass set (production-equivalent).
    /// Defaults to <see langword="true"/>; synthetic fixtures that omit collection semantic
    /// identity or PrimaryAssociation literal columns override to <see langword="false"/>.
    /// </summary>
    protected virtual bool Strict => true;

    /// <summary>
    /// Hook for subclasses to massage the FixtureRunner's actual/ output before comparison
    /// (e.g. trim trailing whitespace from generated SQL). Default is a no-op.
    /// </summary>
    protected virtual void NormalizeActualOutput(string actualDir) { }

    [OneTimeSetUp]
    public void Setup()
    {
        var projectRoot = FixtureTestHelper.FindProjectRoot();
        _fixtureDirectory = Path.GetFullPath(ResolveFixtureDirectory(projectRoot));

        _config = FixtureConfigReader.Read(_fixtureDirectory);
        _actualDir = FixtureRunner.Run(_fixtureDirectory, Strict);
        NormalizeActualOutput(_actualDir);
        _result = FixtureComparer.Compare(_fixtureDirectory);
    }

    [Test]
    public void It_should_produce_actual_output_files()
    {
        Directory.Exists(_actualDir).Should().BeTrue("FixtureRunner should create actual/ directory");
        Directory.GetFiles(_actualDir).Should().NotBeEmpty("FixtureRunner should emit artifacts");
    }

    [Test]
    public void It_should_emit_dialect_sql_for_each_declared_dialect()
    {
        foreach (var dialect in _config.Dialects)
        {
            File.Exists(Path.Combine(_actualDir, $"{dialect}.sql"))
                .Should()
                .BeTrue($"dialect '{dialect}' is declared in fixture.json");
        }
    }

    [Test]
    public void It_should_emit_effective_schema_manifest()
    {
        File.Exists(Path.Combine(_actualDir, "effective-schema.manifest.json")).Should().BeTrue();
    }

    [Test]
    public void It_should_emit_ddl_manifest_when_configured()
    {
        var exists = File.Exists(Path.Combine(_actualDir, "ddl.manifest.json"));

        if (_config.EmitDdlManifest)
        {
            exists.Should().BeTrue("emitDdlManifest is true in fixture.json");
        }
        else
        {
            exists.Should().BeFalse("emitDdlManifest is false in fixture.json");
        }
    }

    [Test]
    public void It_should_emit_relational_model_manifest_for_each_declared_dialect()
    {
        foreach (var dialect in _config.Dialects)
        {
            File.Exists(Path.Combine(_actualDir, $"relational-model.{dialect}.manifest.json"))
                .Should()
                .BeTrue($"dialect '{dialect}' is declared in fixture.json");
        }
    }

    [Test]
    public void It_should_match_expected_golden_files()
    {
        _result
            .Passed.Should()
            .BeTrue(
                $"expected/ and actual/ should match. Set UPDATE_GOLDENS=1 to regenerate.\n\n{_result.Message}"
            );
    }

    /// <summary>
    /// Every SQL Server mirror-stamp UPDATE is hinted <c>WITH (FORCESEEK)</c>, and SQL Server can
    /// only honor that hint while the hinted table exposes an index whose leading key column is the
    /// joined column. When it cannot, the engine does not fall back to a scan — it fails the
    /// statement with error 8622, so a model change that moves a mirror target's key off the joined
    /// column turns every write to that resource into a runtime failure that applies cleanly and
    /// regenerates goldens cleanly. The emitter's own tests pin the hint's text, not its
    /// satisfiability; this reads the emitted <c>CREATE TABLE</c> for each hinted target and checks
    /// the key it actually declares, for every fixture that emits SQL Server DDL.
    /// </summary>
    [Test]
    public void It_should_only_force_seek_mirror_targets_keyed_on_the_joined_column()
    {
        var mssqlPath = Path.Combine(_actualDir, "mssql.sql");
        if (!File.Exists(mssqlPath))
        {
            // PostgreSQL-only fixture: nothing emits the hint, so there is no invariant to check.
            return;
        }

        var generatedSql = File.ReadAllText(mssqlPath);
        var primaryKeyLeadColumns = ReadMssqlPrimaryKeyLeadColumns(generatedSql);

        foreach (Match hint in _mssqlForceSeekMirrorUpdate.Matches(generatedSql))
        {
            var qualifiedTable = $"[{hint.Groups["schema"].Value}].[{hint.Groups["table"].Value}]";
            var joinedColumn = hint.Groups["targetColumn"].Value;

            primaryKeyLeadColumns
                .TryGetValue(qualifiedTable, out var leadColumn)
                .Should()
                .BeTrue(
                    $"the FORCESEEK mirror target {qualifiedTable} must declare a primary key in the "
                        + "emitted DDL; without one the hint cannot be honored and the statement fails with error 8622"
                );

            leadColumn
                .Should()
                .Be(
                    joinedColumn,
                    $"the mirror stamp joins {qualifiedTable} on [{joinedColumn}] under FORCESEEK, so "
                        + $"[{joinedColumn}] must lead that table's primary key. Drop the hint in the "
                        + "emitter if the key ever moves off the joined column."
                );
        }
    }

    /// <summary>
    /// Matches an emitted SQL Server mirror-stamp UPDATE, capturing the hinted table and the column
    /// the <c>@stamped</c> table variable is joined on.
    /// </summary>
    private static readonly Regex _mssqlForceSeekMirrorUpdate = new(
        @"FROM \[(?<schema>[^\]]+)\]\.\[(?<table>[^\]]+)\] r WITH \(FORCESEEK\)\r?\n\s*INNER JOIN @stamped s ON s\.\[[^\]]+\] = r\.\[(?<targetColumn>[^\]]+)\]",
        RegexOptions.Compiled
    );

    private static readonly Regex _mssqlCreateTable = new(
        @"CREATE TABLE \[(?<schema>[^\]]+)\]\.\[(?<table>[^\]]+)\]\r?\n\((?<body>.*?)\r?\n\);",
        RegexOptions.Compiled | RegexOptions.Singleline
    );

    private static readonly Regex _mssqlPrimaryKeyColumns = new(
        @"PRIMARY KEY(?:\s+(?:NON)?CLUSTERED)?\s*\((?<columns>[^)]*)\)",
        RegexOptions.Compiled
    );

    /// <summary>
    /// Maps each emitted SQL Server table to the first column of its declared primary key, which is
    /// the only key position an equality seek on that column can use.
    /// </summary>
    private static Dictionary<string, string> ReadMssqlPrimaryKeyLeadColumns(string generatedSql)
    {
        Dictionary<string, string> leadColumns = new(StringComparer.Ordinal);

        foreach (Match table in _mssqlCreateTable.Matches(generatedSql))
        {
            var primaryKey = _mssqlPrimaryKeyColumns.Match(table.Groups["body"].Value);
            if (!primaryKey.Success)
            {
                continue;
            }

            var leadColumn = primaryKey
                .Groups["columns"]
                .Value.Split(',')[0]
                .Trim()
                .TrimStart('[')
                .Split(']')[0];

            leadColumns[$"[{table.Groups["schema"].Value}].[{table.Groups["table"].Value}]"] = leadColumn;
        }

        return leadColumns;
    }
}

/// <summary>
/// Base for golden-file fixture tests over synthetic small/focused fixtures that intentionally
/// omit collection semantic identity or PrimaryAssociation literal columns. Runs the permissive
/// (non-strict) pass set so the synthetic fixtures keep building.
/// </summary>
public abstract class SyntheticDdlGoldenFixtureTestBase : DdlGoldenFixtureTestBase
{
    protected sealed override bool Strict => false;
}
