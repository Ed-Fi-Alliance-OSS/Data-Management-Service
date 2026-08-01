// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.RegularExpressions;

namespace EdFi.DataManagementService.Backend.Ddl.Tests.Unit;

internal static class DescriptorTableColumnExtractor
{
    /// <summary>
    /// The <c>dms.Descriptor</c> mirror columns the descriptor stamping trigger writes on every stamp:
    /// the content stamp pair plus the remaining <c>dms.Document</c> metadata. They must not appear in the
    /// trigger's no-op change detection, because that exclusion is what stops the mirror <c>UPDATE</c> from
    /// re-firing the trigger and recursing.
    /// </summary>
    public static readonly string[] TriggerMaintainedColumns =
    [
        "ContentVersion",
        "ContentLastModifiedAt",
        "DocumentUuid",
        "IdentityVersion",
        "IdentityLastModifiedAt",
        "CreatedAt",
        "ResourceKeyId",
    ];

    /// <summary>
    /// <c>dms.Descriptor</c> columns that carry no client content and are never written by anything: this
    /// v8.0.0 base has no <c>dms.Document.CreatedByOwnershipTokenId</c> to copy, so the column is a
    /// permanently-NULL forward-compatible placeholder. It must not appear in the trigger's no-op change
    /// detection either — not for recursion reasons, but because diffing a column no writer ever sets is
    /// dead weight that would also wrongly imply the trigger maintains it.
    /// </summary>
    public static readonly string[] PlaceholderColumns = ["CreatedByOwnershipTokenId"];

    /// <summary>
    /// <c>dms.Descriptor</c> columns the storage engine computes from another column in the same row.
    /// No writer can change one independently, and one cannot change unless its source column changed —
    /// which the diff already detects through that source column. Diffing it would therefore be pure
    /// redundancy on a value the trigger has no way to influence.
    /// </summary>
    public static readonly string[] EngineComputedColumns = ["UriLowered"];

    /// <summary>
    /// Every <c>dms.Descriptor</c> column excluded from the stamping trigger's no-op change detection.
    /// </summary>
    public static IEnumerable<string> NonDiffedColumns =>
        TriggerMaintainedColumns.Concat(PlaceholderColumns).Concat(EngineComputedColumns);

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
