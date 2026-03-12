// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.RelationalModel.Build;

namespace EdFi.DataManagementService.Backend.Ddl;

/// <summary>
/// Shared DDL pipeline helpers used by both the CLI and the fixture test runner.
/// Extracted here so that both consumers use the same artifact-emitter logic,
/// as required by the design doc (ddl-generator-testing.md).
/// </summary>
public static class DdlPipelineHelpers
{
    /// <summary>
    /// Creates the dialect implementation and rules for a given <see cref="SqlDialect"/>.
    /// </summary>
    public static (ISqlDialect Dialect, ISqlDialectRules Rules) CreateDialect(SqlDialect dialect)
    {
        ISqlDialectRules rules = dialect switch
        {
            SqlDialect.Pgsql => new PgsqlDialectRules(),
            SqlDialect.Mssql => new MssqlDialectRules(),
            _ => throw new ArgumentOutOfRangeException(nameof(dialect), dialect, "Unsupported dialect"),
        };
        return (SqlDialectFactory.Create(rules), rules);
    }

    /// <summary>
    /// Deep-clones an <see cref="EffectiveSchemaSet"/> so that
    /// <see cref="DerivedRelationalModelSetBuilder"/> can assign JsonNode.Parent
    /// without mutating the original tree.
    /// </summary>
    public static EffectiveSchemaSet CloneEffectiveSchemaSet(EffectiveSchemaSet original)
    {
        var clonedProjects = original
            .ProjectsInEndpointOrder.Select(p => new EffectiveProjectSchema(
                p.ProjectEndpointName,
                p.ProjectName,
                p.ProjectVersion,
                p.IsExtensionProject,
                (JsonObject)p.ProjectSchema.DeepClone()
            ))
            .ToList();

        return new EffectiveSchemaSet(original.EffectiveSchema, clonedProjects);
    }

    /// <summary>
    /// Derives the relational model and generates combined DDL SQL for a single dialect
    /// from a pre-built <see cref="EffectiveSchemaSet"/>. Deep-clones the schema set
    /// to avoid mutating the original tree.
    /// </summary>
    public static (DerivedRelationalModelSet ModelSet, string CombinedSql) BuildDdlForDialect(
        EffectiveSchemaSet effectiveSchemaSet,
        SqlDialect dialect
    )
    {
        var clonedSchemaSet = CloneEffectiveSchemaSet(effectiveSchemaSet);
        var (sqlDialect, dialectRules) = CreateDialect(dialect);
        var modelSetBuilder = new DerivedRelationalModelSetBuilder(RelationalModelSetPasses.CreateDefault());
        var modelSet = modelSetBuilder.Build(clonedSchemaSet, dialect, dialectRules);
        var combinedSql = FullDdlEmitter.Emit(sqlDialect, modelSet);

        return (modelSet, combinedSql);
    }
}
