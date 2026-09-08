// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.RegularExpressions;

namespace EdFi.DataManagementService.Backend.Ddl.Tests.Unit;

internal static class DescriptorTableColumnExtractor
{
    private static readonly Regex _pgColumnLine = new(
        "^\\s+\"(?<name>[A-Za-z][A-Za-z0-9]*)\"\\s+\\S",
        RegexOptions.Multiline | RegexOptions.Compiled
    );

    private static readonly Regex _mssqlColumnLine = new(
        "^\\s+\\[(?<name>[A-Za-z][A-Za-z0-9]*)\\]\\s+(?<type>\\S+)",
        RegexOptions.Multiline | RegexOptions.Compiled
    );

    public static IReadOnlyList<string> ExtractPgColumns(string ddl)
    {
        var block = ExtractDescriptorBlock(
            ddl,
            startMarker: "CREATE TABLE IF NOT EXISTS \"dms\".\"Descriptor\""
        );
        return _pgColumnLine.Matches(block).Select(m => m.Groups["name"].Value).ToList();
    }

    public static IReadOnlyList<(string Name, string Type)> ExtractMssqlColumns(string ddl)
    {
        var block = ExtractDescriptorBlock(ddl, startMarker: "CREATE TABLE [dms].[Descriptor]");
        return _mssqlColumnLine
            .Matches(block)
            .Select(m => (m.Groups["name"].Value, m.Groups["type"].Value))
            .ToList();
    }

    private static string ExtractDescriptorBlock(string ddl, string startMarker)
    {
        var start = ddl.IndexOf(startMarker, StringComparison.Ordinal);
        if (start < 0)
        {
            return string.Empty;
        }
        var end = ddl.IndexOf(");", start, StringComparison.Ordinal);
        return end < 0 ? string.Empty : ddl.Substring(start, end - start);
    }
}
