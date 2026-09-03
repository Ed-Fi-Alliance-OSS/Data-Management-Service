// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Performance.Harness.Measurement;
using FluentAssertions;

namespace EdFi.DataManagementService.Performance.Harness.Tests.Unit.Measurement;

[TestFixture]
public class Given_The_Cursor_Sql_Shape_Gate
{
    private const string CursorSql = """
        SELECT c."DocumentId"
        FROM "edfi"."Student" c
        WHERE c."DocumentId" >= @cursorMin AND c."DocumentId" <= @cursorMax
        ORDER BY c."DocumentId"
        FETCH FIRST @pageSize ROWS ONLY;
        """;

    private static PageSelectionQueryCapture Capture(
        string sql,
        params (string Name, object? Value)[] parameters
    ) =>
        new(
            sql,
            parameters.ToDictionary(parameter => parameter.Name, parameter => parameter.Value),
            PageSelectionCapture.Sha256Lowercase(sql)
        );

    private static PageSelectionQueryCapture WellShapedCapture() =>
        Capture(CursorSql, ("cursorMin", 100L), ("cursorMax", long.MaxValue), ("pageSize", 25L));

    [Test]
    public void It_accepts_a_cursor_shaped_page_selection()
    {
        Action act = () => PerfCursorSqlShape.EnsureCursorShaped(WellShapedCapture(), 100, 25, "cell");

        act.Should().NotThrow();
    }

    [Test]
    public void It_rejects_offset_row_number_and_count_text_case_insensitively()
    {
        foreach (
            string sql in (string[])
                [
                    CursorSql + " OFFSET @x ROWS",
                    CursorSql.Replace("FETCH FIRST", "ROW_NUMBER() OVER () FETCH FIRST"),
                    "SELECT Count(*) FROM x; " + CursorSql,
                    CursorSql + " offset 5",
                ]
        )
        {
            PageSelectionQueryCapture capture = Capture(
                sql,
                ("cursorMin", 100L),
                ("cursorMax", long.MaxValue),
                ("pageSize", 25L)
            );
            Action act = () => PerfCursorSqlShape.EnsureCursorShaped(capture, 100, 25, "cell");

            act.Should().Throw<PerfObservationException>();
        }
    }

    [Test]
    public void It_rejects_traditional_offset_and_limit_parameter_bindings()
    {
        foreach (string forbidden in (string[])["offset", "limit"])
        {
            PageSelectionQueryCapture capture = Capture(
                CursorSql,
                ("cursorMin", 100L),
                ("cursorMax", long.MaxValue),
                ("pageSize", 25L),
                (forbidden, 0L)
            );
            Action act = () => PerfCursorSqlShape.EnsureCursorShaped(capture, 100, 25, "cell");

            act.Should().Throw<PerfObservationException>().WithMessage($"*{forbidden}*");
        }
    }

    [Test]
    public void It_rejects_a_wrong_bound_minimum_or_page_size()
    {
        Action wrongMinimum = () =>
            PerfCursorSqlShape.EnsureCursorShaped(WellShapedCapture(), 101, 25, "cell");
        Action wrongPageSize = () =>
            PerfCursorSqlShape.EnsureCursorShaped(WellShapedCapture(), 100, 500, "cell");

        wrongMinimum.Should().Throw<PerfObservationException>();
        wrongPageSize.Should().Throw<PerfObservationException>();
    }

    [Test]
    public void It_rejects_missing_cursor_parameters()
    {
        PageSelectionQueryCapture withoutMinimum = Capture(
            CursorSql,
            ("cursorMax", long.MaxValue),
            ("pageSize", 25L)
        );
        PageSelectionQueryCapture withoutMaximum = Capture(CursorSql, ("cursorMin", 100L), ("pageSize", 25L));

        Action missingMinimum = () => PerfCursorSqlShape.EnsureCursorShaped(withoutMinimum, 100, 25, "cell");
        Action missingMaximum = () => PerfCursorSqlShape.EnsureCursorShaped(withoutMaximum, 100, 25, "cell");

        missingMinimum.Should().Throw<PerfObservationException>();
        missingMaximum.Should().Throw<PerfObservationException>();
    }
}
