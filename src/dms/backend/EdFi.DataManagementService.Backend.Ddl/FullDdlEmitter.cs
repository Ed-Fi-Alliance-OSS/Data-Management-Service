// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text;
using EdFi.DataManagementService.Backend.External;

namespace EdFi.DataManagementService.Backend.Ddl;

/// <summary>
/// Combines all DDL emission stages (core DDL, relational model DDL, and seed DML)
/// into a single SQL output. This is the shared artifact emitter required by
/// <c>ddl-generator-testing.md</c> to prevent drift between CLI and test pipelines.
/// </summary>
public static class FullDdlEmitter
{
    /// <summary>
    /// Emits the complete DDL SQL by combining a Phase 0 preflight check, core schema DDL,
    /// relational model DDL, and seed DML for the given dialect and derived model set.
    /// </summary>
    public static string Emit(ISqlDialect dialect, DerivedRelationalModelSet modelSet)
    {
        var seedEmitter = new SeedDmlEmitter(dialect);
        var preflightDdl = WrapPhase0(
            seedEmitter.EmitPreflightOnly(modelSet.EffectiveSchema.EffectiveSchemaHash)
        );
        var coreDdl = new CoreDdlEmitter(dialect).Emit();
        var relationalDdl = new RelationalModelDdlEmitter(dialect).Emit(modelSet);
        var authHierarchy = AuthEdOrgHierarchyCompiler.Compile(modelSet);
        var authDdl = new AuthDdlEmitter(dialect, authHierarchy).Emit();
        var seedDml = seedEmitter.Emit(modelSet.EffectiveSchema);
        return JoinSegments(preflightDdl, coreDdl, relationalDdl, authDdl, seedDml);
    }

    private static string WrapPhase0(string preflightSql)
    {
        var sb = new StringBuilder();
        sb.Append("-- ==========================================================\n");
        sb.Append("-- Phase 0: Preflight (fail fast on schema hash mismatch)\n");
        sb.Append("-- ==========================================================\n");
        sb.Append('\n');
        sb.Append(preflightSql);
        return sb.ToString();
    }

    /// <summary>
    /// Concatenates SQL segments, ensuring a newline boundary between each non-empty
    /// segment so that the last line of one segment never runs into the first line
    /// of the next.
    /// </summary>
    internal static string JoinSegments(params string[] segments)
    {
        var sb = new StringBuilder();
        foreach (var segment in segments)
        {
            if (segment.Length == 0)
                continue;
            if (sb.Length > 0 && sb[sb.Length - 1] != '\n')
                sb.Append('\n');
            sb.Append(segment);
        }
        return sb.ToString();
    }
}
