// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.RegularExpressions;

namespace EdFi.DataManagementService.Backend.Ddl.Tests.Unit;

internal static class DescriptorTableColumnExtractor
{
    /// <summary>
    /// <c>dms.Descriptor</c> columns that carry no client content: the stamp pair and the
    /// <c>dms.Document</c> metadata the descriptor stamping trigger mirrors, plus
    /// <c>CreatedByOwnershipTokenId</c>, which has no <c>dms.Document</c> counterpart and is never
    /// written at all. None of them may appear in the trigger's no-op change detection.
    /// </summary>
    public static readonly string[] TriggerMaintainedColumns =
    [
        "ContentVersion",
        "ContentLastModifiedAt",
        "DocumentUuid",
        "IdentityVersion",
        "IdentityLastModifiedAt",
        "CreatedAt",
        "CreatedByOwnershipTokenId",
    ];

    private static readonly Regex _pgColumnLine = new(
        "^\\s+\"(?<name>[A-Za-z][A-Za-z0-9]*)\"\\s+\\S",
        RegexOptions.Multiline | RegexOptions.Compiled
    );

    private static readonly Regex _mssqlColumnLine = new(
        "^\\s+\\[(?<name>[A-Za-z][A-Za-z0-9]*)\\]\\s+(?<type>\\S+)",
        RegexOptions.Multiline | RegexOptions.Compiled
    );

    /// <summary>
    /// Returns the <c>dms.Descriptor</c> <c>CREATE TABLE</c> body so assertions about column
    /// definitions cannot be satisfied by an identically-shaped column on another <c>dms</c> table
    /// (PostgreSQL drops default-constraint names, so several definitions are not self-identifying).
    /// </summary>
    public static string ExtractPgBlock(string ddl) =>
        ExtractDescriptorBlock(ddl, startMarker: "CREATE TABLE IF NOT EXISTS \"dms\".\"Descriptor\"");

    /// <inheritdoc cref="ExtractPgBlock" />
    public static string ExtractMssqlBlock(string ddl) =>
        ExtractDescriptorBlock(ddl, startMarker: "CREATE TABLE [dms].[Descriptor]");

    public static IReadOnlyList<string> ExtractPgColumns(string ddl)
    {
        var block = ExtractPgBlock(ddl);
        return _pgColumnLine.Matches(block).Select(m => m.Groups["name"].Value).ToList();
    }

    public static IReadOnlyList<(string Name, string Type)> ExtractMssqlColumns(string ddl)
    {
        var block = ExtractMssqlBlock(ddl);
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
