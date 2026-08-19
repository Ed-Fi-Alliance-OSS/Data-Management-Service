// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.RegularExpressions;
using FluentAssertions;

namespace EdFi.DataManagementService.Backend.Ddl.Tests.Unit;

/// <summary>
/// Checks change-queries.md invariant 7 against emitted SQL Server DDL: every mirror-stamp UPDATE is
/// hinted <c>WITH (FORCESEEK)</c>, and SQL Server can only honor that hint while the hinted table
/// exposes an index whose leading key column is the joined column. When it cannot, the engine does
/// not fall back to a scan — it fails the statement with error 8622, so a model change that moves a
/// mirror target's key off the joined column turns every write to that resource into a runtime
/// failure that applies cleanly and regenerates goldens cleanly. The emitter's own tests pin the
/// hint's text, not its satisfiability; this reads the emitted <c>CREATE TABLE</c> for each hinted
/// target and checks the key it actually declares.
///
/// <para>Shared rather than owned by one golden base because the two SQL Server golden paths reach
/// the emitters differently and both need it. <see cref="DdlGoldenFixtureTestBase"/> drives fixtures
/// derived from an ApiSchema, where a root table keyed on <c>DocumentId</c> is structural.
/// <c>DdlEmissionGoldenTests</c> drives hand-authored <c>DerivedRelationalModelSet</c> builders that
/// construct <c>Key</c> directly and never pass through derivation, so it is the path that can
/// actually produce an unsatisfiable hint.</para>
/// </summary>
internal static class MssqlForceSeekInvariant
{
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
    /// Asserts that every <c>WITH (FORCESEEK)</c> mirror-stamp target in <paramref name="generatedSql"/>
    /// declares a primary key led by the column the mirror joins it on.
    /// </summary>
    /// <param name="generatedSql">Emitted SQL Server DDL. A dialect that emits no hint passes trivially.</param>
    /// <param name="source">
    /// Identifies which emission produced the DDL, so a failure names the fixture rather than only
    /// the table. Golden runs cover many model sets and the regex alone cannot say which one.
    /// </param>
    public static void AssertHintedMirrorTargetsAreSeekable(string generatedSql, string source)
    {
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
                        + $"DDL emitted for {source}; without one the hint cannot be honored and the "
                        + "statement fails with error 8622"
                );

            leadColumn
                .Should()
                .Be(
                    joinedColumn,
                    $"the mirror stamp emitted for {source} joins {qualifiedTable} on [{joinedColumn}] "
                        + $"under FORCESEEK, so [{joinedColumn}] must lead that table's primary key. "
                        + "Drop the hint in the emitter if the key ever moves off the joined column."
                );
        }
    }

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
