// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text;

namespace EdFi.DataManagementService.Backend.RelationalModel;

public static class RelationalNameConventions
{
    public static DbColumnName DocumentIdColumnName { get; } = new("DocumentId");
    public static DbColumnName OrdinalColumnName { get; } = new("Ordinal");

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

    public static string ToCollectionBaseName(string collectionPropertyName)
    {
        if (collectionPropertyName is null)
        {
            throw new ArgumentNullException(nameof(collectionPropertyName));
        }

        var singular = SingularizeCollectionSegment(collectionPropertyName);

        return ToPascalCase(singular);
    }

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

    private static bool IsAsciiLetterOrDigit(char value)
    {
        return value is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9');
    }

    private static bool IsAsciiLetter(char value)
    {
        return value is >= 'a' and <= 'z';
    }
}
