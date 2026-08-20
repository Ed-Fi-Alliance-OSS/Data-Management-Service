// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.RegularExpressions;
using FluentAssertions;

namespace EdFi.DataManagementService.Backend.Ddl.Tests.Unit;

/// <summary>
/// Checks change-queries.md invariant 7 against emitted SQL Server DDL, in both directions: every
/// mirror-stamp UPDATE carries <c>WITH (FORCESEEK)</c>, and every hint that is carried is one SQL
/// Server can honor. It can only honor one while the hinted table exposes an index whose leading key
/// column is the joined column. When it cannot, the engine does not fall back to a scan — it fails
/// the statement with error 8622, so a model change that moves a mirror target's key off the joined
/// column turns every write to that resource into a runtime failure that applies cleanly and
/// regenerates goldens cleanly. The emitter's own tests pin the hint's text, not its satisfiability;
/// this reads the emitted <c>CREATE TABLE</c> for each hinted target and checks the key it actually
/// declares.
///
/// <para>The presence half is checked here rather than left to the emitters' own tests because those
/// pin the hint on a resource root (<c>edfi.School</c>) and on <c>dms.Descriptor</c> only. The child
/// and <c>_ext</c> shapes emit the same statement against the resource's root table, and that is the
/// statement whose scanning plan was measured as the sole cycle in every deadlock graph a concurrent
/// child-collection load produced — so dropping the hint from that shape alone has to fail
/// something. Absent this check it fails nothing: the emitters' tests still pass, the goldens
/// regenerate cleanly, and the seekability walk below simply has fewer hints to look at.</para>
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
    /// Matches an emitted SQL Server mirror-stamp UPDATE, capturing the target table, whatever table
    /// hint it carries, and the column the <c>@stamped</c> table variable is joined on.
    ///
    /// <para>One pattern rather than a hinted/unhinted pair. A pair only classifies the two spellings
    /// it anticipates: a mirror stamp carrying any other hint — <c>WITH (INDEX(…))</c>, or
    /// <c>WITH(FORCESEEK)</c> without the space — matches neither, so it is reported as neither
    /// unhinted nor checked for seekability and the invariant passes with nothing to look at.
    /// Capturing the hint instead makes every spelling land somewhere.</para>
    /// </summary>
    private static readonly Regex _mssqlMirrorUpdate = new(
        @"FROM \[(?<schema>[^\]]+)\]\.\[(?<table>[^\]]+)\] r(?<hint>[^\r\n]*)\r?\n\s*INNER JOIN @stamped s ON s\.\[[^\]]+\] = r\.\[(?<targetColumn>[^\]]+)\]",
        RegexOptions.Compiled
    );

    /// <summary>
    /// The exact hint invariant 7 requires, as it is emitted — leading space included, because the
    /// capture above starts immediately after the <c>r</c> alias.
    /// </summary>
    private const string RequiredHint = " WITH (FORCESEEK)";

    /// <summary>
    /// The join every mirror stamp is built around. Counted, so that a mirror stamp
    /// <see cref="_mssqlMirrorUpdate"/> stops matching fails instead of silently leaving scope.
    /// </summary>
    private const string MirrorStampJoin = "INNER JOIN @stamped s ON";

    private static readonly Regex _mssqlCreateTable = new(
        @"CREATE TABLE \[(?<schema>[^\]]+)\]\.\[(?<table>[^\]]+)\]\r?\n\((?<body>.*?)\r?\n\);",
        RegexOptions.Compiled | RegexOptions.Singleline
    );

    private static readonly Regex _mssqlPrimaryKeyColumns = new(
        @"PRIMARY KEY(?:\s+(?:NON)?CLUSTERED)?\s*\((?<columns>[^)]*)\)",
        RegexOptions.Compiled
    );

    /// <summary>
    /// Asserts that no mirror-stamp UPDATE in <paramref name="generatedSql"/> is missing
    /// <c>WITH (FORCESEEK)</c>, and that every hinted target declares a primary key led by the column
    /// the mirror joins it on.
    /// </summary>
    /// <param name="generatedSql">
    /// Emitted SQL Server DDL. A dialect or fixture that emits no mirror stamp at all passes
    /// trivially — there is no statement for the invariant to be about.
    /// </param>
    /// <param name="source">
    /// Identifies which emission produced the DDL, so a failure names the fixture rather than only
    /// the table. Golden runs cover many model sets and the regex alone cannot say which one.
    /// </param>
    public static void AssertMirrorStampsAreHintedAndSeekable(string generatedSql, string source)
    {
        var mirrorStamps = _mssqlMirrorUpdate.Matches(generatedSql);

        // This pattern is the only thing that decides what gets checked below, so a mirror stamp it
        // stops matching takes its own hint and seekability out of scope without failing anything.
        // Counting the join every mirror stamp is built around turns that into a failure.
        mirrorStamps
            .Count.Should()
            .Be(
                generatedSql.AsSpan().Count(MirrorStampJoin),
                $"every mirror stamp in the DDL emitted for {source} must be recognized by the "
                    + "mirror-stamp pattern; one it does not recognize is one whose hint and "
                    + "seekability go unchecked"
            );

        var primaryKeyLeadColumns = ReadMssqlPrimaryKeyLeadColumns(generatedSql);

        foreach (Match mirrorStamp in mirrorStamps)
        {
            var qualifiedTable =
                $"[{mirrorStamp.Groups["schema"].Value}].[{mirrorStamp.Groups["table"].Value}]";
            var joinedColumn = mirrorStamp.Groups["targetColumn"].Value;

            mirrorStamp
                .Groups["hint"]
                .Value.Should()
                .Be(
                    RequiredHint,
                    $"change-queries.md invariant 7 requires exactly WITH (FORCESEEK) on every mirror "
                        + $"stamp, and the one emitted for {source} against {qualifiedTable} carries "
                        + "something else. The join is on a table variable, so any plan free to scan "
                        + "the mirror table takes update locks across rows the transaction never "
                        + "touched."
                );

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
