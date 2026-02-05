// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System;
using System.Collections.Generic;
using System.Linq;

namespace EdFi.DataManagementService.Backend.RelationalModel;

/// <summary>
/// Classifies the identifier namespace for collision diagnostics.
/// </summary>
internal enum IdentifierCollisionKind
{
    Schema,
    Table,
    Column,
    Constraint,
    Index,
    Trigger,
}

/// <summary>
/// Describes the stage that produced a naming collision.
/// </summary>
internal readonly record struct IdentifierCollisionStage(string Name, string? Details)
{
    /// <summary>
    /// Collision stage after schema normalization.
    /// </summary>
    public static IdentifierCollisionStage AfterSchemaNormalization => new("AfterSchemaNormalization", null);

    /// <summary>
    /// Collision stage after override normalization.
    /// </summary>
    public static IdentifierCollisionStage AfterOverrideNormalization =>
        new("AfterOverrideNormalization", null);

    /// <summary>
    /// Collision stage after dialect shortening.
    /// </summary>
    public static IdentifierCollisionStage AfterDialectShortening(ISqlDialectRules dialectRules)
    {
        ArgumentNullException.ThrowIfNull(dialectRules);

        var units = dialectRules.Dialect switch
        {
            SqlDialect.Pgsql => "bytes",
            SqlDialect.Mssql => "chars",
            _ => "chars",
        };

        var dialectLabel = dialectRules.Dialect switch
        {
            SqlDialect.Pgsql => "Pgsql",
            SqlDialect.Mssql => "Mssql",
            _ => dialectRules.Dialect.ToString(),
        };

        return new IdentifierCollisionStage(
            "AfterDialectShortening",
            $"{dialectLabel}:{dialectRules.MaxIdentifierLength}-{units}"
        );
    }

    /// <summary>
    /// Formats the stage for diagnostics.
    /// </summary>
    /// <returns>A formatted stage label.</returns>
    public string Format()
    {
        return string.IsNullOrWhiteSpace(Details) ? Name : $"{Name}({Details})";
    }
}

/// <summary>
/// Identifies the namespace scope for collision detection.
/// </summary>
internal readonly record struct IdentifierCollisionScope(
    IdentifierCollisionKind Kind,
    string Schema,
    string Table
)
{
    /// <summary>
    /// Formats the scope for diagnostics.
    /// </summary>
    /// <returns>A formatted scope label.</returns>
    public string FormatLocation()
    {
        if (!string.IsNullOrWhiteSpace(Table))
        {
            return $"table '{Schema}.{Table}'";
        }

        return string.IsNullOrWhiteSpace(Schema) ? "database" : $"schema '{Schema}'";
    }
}

/// <summary>
/// Captures the origin details for an identifier collision source.
/// </summary>
internal readonly record struct IdentifierCollisionOrigin(
    string Description,
    string? ResourceLabel,
    string? JsonPath
)
{
    /// <summary>
    /// Formats origin details for diagnostics.
    /// </summary>
    /// <returns>A formatted origin label.</returns>
    public string Format()
    {
        List<string> parts = [];

        if (!string.IsNullOrWhiteSpace(Description))
        {
            parts.Add(Description);
        }

        if (!string.IsNullOrWhiteSpace(ResourceLabel))
        {
            parts.Add($"resource '{ResourceLabel}'");
        }

        if (!string.IsNullOrWhiteSpace(JsonPath))
        {
            parts.Add($"path '{JsonPath}'");
        }

        return parts.Count == 0 ? string.Empty : string.Join(", ", parts);
    }
}

/// <summary>
/// Represents a collision source with pre- and post-normalization identifiers.
/// </summary>
internal readonly record struct IdentifierCollisionSource(
    string OriginalIdentifier,
    string FinalIdentifier,
    IdentifierCollisionOrigin Origin
)
{
    /// <summary>
    /// Formats the source for diagnostics.
    /// </summary>
    /// <returns>A formatted source label.</returns>
    public string Format()
    {
        var origin = Origin.Format();

        return string.IsNullOrWhiteSpace(origin)
            ? $"{OriginalIdentifier} -> {FinalIdentifier}"
            : $"{OriginalIdentifier} -> {FinalIdentifier} ({origin})";
    }
}

/// <summary>
/// Represents a formatted collision record.
/// </summary>
internal sealed record IdentifierCollisionRecord(
    IdentifierCollisionStage Stage,
    IdentifierCollisionScope Scope,
    IReadOnlyList<IdentifierCollisionSource> Sources
)
{
    /// <summary>
    /// Formats the collision record for diagnostics.
    /// </summary>
    /// <returns>A formatted collision label.</returns>
    public string Format()
    {
        var kindLabel = Scope.Kind switch
        {
            IdentifierCollisionKind.Schema => "schema name",
            IdentifierCollisionKind.Table => "table name",
            IdentifierCollisionKind.Column => "column name",
            IdentifierCollisionKind.Constraint => "constraint name",
            IdentifierCollisionKind.Index => "index name",
            IdentifierCollisionKind.Trigger => "trigger name",
            _ => "identifier",
        };

        var header = $"{kindLabel} collision {Stage.Format()} in {Scope.FormatLocation()}";
        var sources = string.Join(", ", Sources.Select(source => source.Format()));

        return $"{header}: {sources}";
    }
}
