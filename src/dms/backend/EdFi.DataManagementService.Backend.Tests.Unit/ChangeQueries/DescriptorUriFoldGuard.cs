// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using FluentAssertions;

namespace EdFi.DataManagementService.Backend.Tests.Unit.ChangeQueries;

/// <summary>
/// The descriptor probe SQL guard: every lowering in emitted Change Query SQL must fold its input under the
/// engine's pinned collation (<c>pg_c_utf8</c> on PostgreSQL, the identity collation inside <c>LOWER</c> on
/// SQL Server), never under the database default.
/// </summary>
internal static class DescriptorUriFoldGuard
{
    /// <summary>
    /// Asserts every lowering call in <paramref name="sql"/> is the dialect's function over an argument that
    /// ends with the pinned collation, and returns how many it checked.
    /// </summary>
    public static int AssertEveryLoweringIsCollated(string sql, SqlDialect dialect)
    {
        string collationSuffix =
            dialect is SqlDialect.Pgsql ? " COLLATE \"pg_c_utf8\"" : " COLLATE SQL_Latin1_General_CP1_CI_AS";
        string loweringFunction = dialect is SqlDialect.Pgsql ? "lower(" : "LOWER(";
        IReadOnlyList<(string Function, string Argument)> lowerings = LoweringCalls(sql);

        foreach ((string function, string argument) in lowerings)
        {
            function.Should().Be(loweringFunction);
            argument.Should().EndWith(collationSuffix);
        }

        return lowerings.Count;
    }

    /// <summary>
    /// Every lowering call in <paramref name="sql"/>, in either case, with its full parenthesized argument.
    /// </summary>
    private static IReadOnlyList<(string Function, string Argument)> LoweringCalls(string sql)
    {
        List<(string, string)> calls = [];
        int start = sql.IndexOf("lower(", StringComparison.OrdinalIgnoreCase);
        while (start >= 0)
        {
            int argumentStart = start + "lower(".Length;
            int depth = 1;
            int index = argumentStart;
            while (depth > 0)
            {
                depth += sql[index] switch
                {
                    '(' => 1,
                    ')' => -1,
                    _ => 0,
                };
                index++;
            }
            calls.Add((sql[start..argumentStart], sql[argumentStart..(index - 1)]));
            start = sql.IndexOf("lower(", argumentStart, StringComparison.OrdinalIgnoreCase);
        }
        return calls;
    }
}
