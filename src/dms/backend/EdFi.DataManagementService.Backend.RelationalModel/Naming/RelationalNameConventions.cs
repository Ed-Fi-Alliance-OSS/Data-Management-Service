// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text;

namespace EdFi.DataManagementService.Backend.RelationalModel.Naming;

/// <summary>
/// Deterministic naming helpers used when deriving physical schemas, tables, columns, and constraints from
/// resource metadata.
///
/// These helpers intentionally apply a restricted, cross-database-safe identifier policy:
/// <list type="bullet">
/// <item><description>Schema names are normalized to lowercase ASCII letters/digits (with a leading letter).</description></item>
/// <item><description>Property and collection segments are transformed into PascalCase identifiers.</description></item>
/// <item><description>Collection tables and key parts follow the root+ordinals conventions in the redesign docs.</description></item>
/// </list>
/// </summary>
public static class RelationalNameConventions
{
    /// <summary>
    /// The standard <c>DocumentId</c> column name used by <c>dms.Document</c> and resource root tables.
    /// </summary>
    public static DbColumnName DocumentIdColumnName { get; } = new("DocumentId");

    /// <summary>
    /// Returns <c>true</c> when the column name represents a document ID — either the root
    /// <c>DocumentId</c> or a prefixed variant such as <c>School_DocumentId</c>.
    /// </summary>
    public static bool IsDocumentIdColumn(DbColumnName columnName)
    {
        return string.Equals(columnName.Value, DocumentIdColumnName.Value, StringComparison.Ordinal)
            || columnName.Value.EndsWith("_DocumentId", StringComparison.Ordinal);
    }

    /// <summary>
    /// The standard <c>Ordinal</c> column name used by collection tables to preserve array ordering.
    /// </summary>
    public static DbColumnName OrdinalColumnName { get; } = new("Ordinal");

    /// <summary>
    /// Normalizes a project endpoint name into a physical schema identifier.
    /// </summary>
    /// <param name="projectEndpointName">The endpoint name (e.g., <c>ed-fi</c>).</param>
    public static DbSchemaName NormalizeSchemaName(string projectEndpointName)
    {
        if (projectEndpointName is null)
        {
            throw new ArgumentNullException(nameof(projectEndpointName));
        }

        StringBuilder builder = new(projectEndpointName.Length);

        foreach (var character in projectEndpointName)
        {
            if (IsAsciiLetterOrDigit(character))
            {
                builder.Append(char.ToLowerInvariant(character));
            }
        }

        if (builder.Length == 0 || !IsAsciiLetter(builder[0]))
        {
            builder.Insert(0, 'p');
        }

        return new DbSchemaName(builder.ToString());
    }

    /// <summary>
    /// Converts an arbitrary identifier into PascalCase by removing separators and capitalizing segment starts.
    /// </summary>
    /// <param name="value">The raw identifier.</param>
    public static string ToPascalCase(string value)
    {
        if (value is null)
        {
            throw new ArgumentNullException(nameof(value));
        }

        if (value.Length == 0)
        {
            return string.Empty;
        }

        StringBuilder builder = new(value.Length);
        var nextUpper = true;

        foreach (var character in value)
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(nextUpper ? char.ToUpperInvariant(character) : character);
                nextUpper = false;
                continue;
            }

            nextUpper = true;
        }

        return builder.ToString();
    }

    /// <summary>
    /// Applies a simple singularization rule for collection property names (used for table and key naming).
    /// </summary>
    /// <param name="value">The collection property name.</param>
    public static string SingularizeCollectionSegment(string value)
    {
        if (value is null)
        {
            throw new ArgumentNullException(nameof(value));
        }

        if (value.Length == 0)
        {
            return string.Empty;
        }

        var lower = value.ToLowerInvariant();

        return lower switch
        {
            var s when s.EndsWith("ies", StringComparison.Ordinal) => $"{value[..^3]}y",
            var s
                when s.EndsWith("ches", StringComparison.Ordinal)
                    || s.EndsWith("shes", StringComparison.Ordinal)
                    || s.EndsWith("xes", StringComparison.Ordinal)
                    || s.EndsWith("zes", StringComparison.Ordinal)
                    || s.EndsWith("ses", StringComparison.Ordinal) => value[..^2],
            var s
                when s.EndsWith("s", StringComparison.Ordinal)
                    && !s.EndsWith("ss", StringComparison.Ordinal) => value[..^1],
            _ => value,
        };
    }

    /// <summary>
    /// Produces the base name for a collection table by singularizing and PascalCasing the collection property.
    /// </summary>
    /// <param name="collectionPropertyName">The JSON property name for the collection.</param>
    public static string ToCollectionBaseName(string collectionPropertyName)
    {
        if (collectionPropertyName is null)
        {
            throw new ArgumentNullException(nameof(collectionPropertyName));
        }

        var singular = SingularizeCollectionSegment(collectionPropertyName);

        return ToPascalCase(singular);
    }

    /// <summary>
    /// Returns the column name for the root document id key part on a collection table (e.g.,
    /// <c>School_DocumentId</c>).
    /// </summary>
    /// <param name="rootBaseName">The resource root table base name.</param>
    public static DbColumnName RootDocumentIdColumnName(string rootBaseName)
    {
        if (rootBaseName is null)
        {
            throw new ArgumentNullException(nameof(rootBaseName));
        }

        if (rootBaseName.Length == 0)
        {
            throw new ArgumentException("Root base name must not be empty.", nameof(rootBaseName));
        }

        return new DbColumnName($"{rootBaseName}_DocumentId");
    }

    /// <summary>
    /// Returns the column name for an ancestor collection ordinal key part (e.g., <c>AddressOrdinal</c>).
    /// </summary>
    /// <param name="parentCollectionBaseName">The PascalCase base name of the parent collection.</param>
    public static DbColumnName ParentCollectionOrdinalColumnName(string parentCollectionBaseName)
    {
        if (parentCollectionBaseName is null)
        {
            throw new ArgumentNullException(nameof(parentCollectionBaseName));
        }

        if (parentCollectionBaseName.Length == 0)
        {
            throw new ArgumentException(
                "Parent collection base name must not be empty.",
                nameof(parentCollectionBaseName)
            );
        }

        return new DbColumnName($"{parentCollectionBaseName}Ordinal");
    }

    /// <summary>
    /// Returns the column name for a descriptor FK column given the base name (e.g.,
    /// <c>SchoolTypeDescriptor_DescriptorId</c>).
    /// </summary>
    /// <param name="descriptorBaseName">The base name for the descriptor value path.</param>
    public static DbColumnName DescriptorIdColumnName(string descriptorBaseName)
    {
        if (descriptorBaseName is null)
        {
            throw new ArgumentNullException(nameof(descriptorBaseName));
        }

        if (descriptorBaseName.Length == 0)
        {
            throw new ArgumentException(
                "Descriptor base name must not be empty.",
                nameof(descriptorBaseName)
            );
        }

        return new DbColumnName($"{descriptorBaseName}_DescriptorId");
    }

    /// <summary>
    /// Checks whether a character is an ASCII letter or digit.
    /// </summary>
    private static bool IsAsciiLetterOrDigit(char value)
    {
        return value is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9');
    }

    /// <summary>
    /// Checks whether a character is an ASCII lowercase letter.
    /// </summary>
    private static bool IsAsciiLetter(char value)
    {
        return value is >= 'a' and <= 'z';
    }
}
