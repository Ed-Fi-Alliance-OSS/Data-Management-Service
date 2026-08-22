// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text;
using EdFi.DataManagementService.Backend.Ddl;

namespace EdFi.DataManagementService.Backend.Cdc;

internal static class CdcConnectorTemplateDebeziumSelectorFormatter
{
    internal static string TableSelector(CdcSourceTableInventory table)
    {
        ArgumentNullException.ThrowIfNull(table);

        return $"{EscapeRegexIdentifier(table.TableName.Schema.Value)}\\.{EscapeRegexIdentifier(table.TableName.Name)}";
    }

    internal static string KeyColumnList(
        CdcSourceTableInventory table,
        CdcExpectedMessageKeyColumns messageKeyColumns
    )
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(messageKeyColumns);

        var emittedColumnsByName = table.Columns.ToDictionary(
            column => column.ColumnName.Value,
            StringComparer.Ordinal
        );

        return string.Join(
            ",",
            messageKeyColumns.KeyColumns.Select(column =>
                EscapeRegexIdentifier(emittedColumnsByName[column.Value].ColumnName.Value)
            )
        );
    }

    internal static string EscapeRegexIdentifier(string identifier)
    {
        ArgumentNullException.ThrowIfNull(identifier);

        var escapedIdentifier = new StringBuilder(identifier.Length);

        foreach (char character in identifier)
        {
            if (IsJavaRegexMetacharacter(character))
            {
                escapedIdentifier.Append('\\');
            }

            escapedIdentifier.Append(character);
        }

        return escapedIdentifier.ToString();
    }

    private static bool IsJavaRegexMetacharacter(char character) =>
        character
            is '\\'
                or '.'
                or '^'
                or '$'
                or '|'
                or '?'
                or '*'
                or '+'
                or '('
                or ')'
                or '['
                or ']'
                or '{'
                or '}';
}
