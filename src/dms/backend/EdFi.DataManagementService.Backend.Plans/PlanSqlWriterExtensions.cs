// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.RegularExpressions;
using EdFi.DataManagementService.Backend.Ddl;

namespace EdFi.DataManagementService.Backend.Plans;

/// <summary>
/// Shared SQL writer helpers for plan/query SQL generation.
/// </summary>
public static partial class PlanSqlWriterExtensions
{
    private const string BareParameterNamePattern = "^[a-zA-Z_][a-zA-Z0-9_]*$";

    /// <summary>
    /// Appends a parameter placeholder (<c>@name</c>) from a bare parameter name.
    /// </summary>
    /// <param name="writer">The SQL writer.</param>
    /// <param name="bareName">Bare parameter name without prefix (for example, <c>offset</c>).</param>
    /// <returns>The same writer for fluent chaining.</returns>
    public static SqlWriter AppendParameter(this SqlWriter writer, string bareName)
    {
        ArgumentNullException.ThrowIfNull(writer);

        ValidateBareParameterName(bareName, nameof(bareName));
        return writer.Append($"@{bareName}");
    }

    /// <summary>
    /// Appends a multi-line <c>WHERE</c> clause using canonical uppercase keywords and indentation.
    /// </summary>
    /// <param name="writer">The SQL writer.</param>
    /// <param name="predicates">Predicate SQL fragments without surrounding parentheses.</param>
    /// <returns>The same writer for fluent chaining.</returns>
    public static SqlWriter AppendWhereClause(this SqlWriter writer, IReadOnlyList<string> predicates)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(predicates);

        if (predicates.Count == 0)
        {
            return writer;
        }

        writer.AppendLine("WHERE");

        using (writer.Indent())
        {
            for (var index = 0; index < predicates.Count; index++)
            {
                var predicate = predicates[index];

                if (string.IsNullOrWhiteSpace(predicate))
                {
                    throw new ArgumentException(
                        $"Predicate at index {index} cannot be null or whitespace.",
                        nameof(predicates)
                    );
                }

                var normalizedPredicate = predicate.Trim();
                var line = index == 0 ? $"({normalizedPredicate})" : $"AND ({normalizedPredicate})";
                writer.AppendLine(line);
            }
        }

        return writer;
    }

    /// <summary>
    /// Validates a bare parameter name.
    /// </summary>
    /// <param name="bareName">Bare parameter name to validate.</param>
    /// <param name="argumentName">Argument name used for thrown exceptions.</param>
    /// <exception cref="ArgumentException">Thrown when the value is null/whitespace or violates the allowed pattern.</exception>
    public static void ValidateBareParameterName(string bareName, string argumentName)
    {
        if (string.IsNullOrWhiteSpace(bareName))
        {
            throw new ArgumentException("Parameter name cannot be null or whitespace.", argumentName);
        }

        if (!BareParameterNameRegex().IsMatch(bareName))
        {
            throw new ArgumentException(
                $"Parameter name must match pattern '{BareParameterNamePattern}'.",
                argumentName
            );
        }
    }

    [GeneratedRegex(BareParameterNamePattern, RegexOptions.CultureInvariant)]
    private static partial Regex BareParameterNameRegex();
}
