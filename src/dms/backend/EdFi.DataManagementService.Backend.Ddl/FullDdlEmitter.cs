// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

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
    /// Emits the complete DDL SQL by combining core schema DDL, relational model DDL,
    /// and seed DML for the given dialect and derived model set.
    /// </summary>
    public static string Emit(ISqlDialect dialect, DerivedRelationalModelSet modelSet)
    {
        var coreDdl = new CoreDdlEmitter(dialect).Emit();
        var relationalDdl = new RelationalModelDdlEmitter(dialect).Emit(modelSet);
        var seedDml = new SeedDmlEmitter(dialect).Emit(modelSet.EffectiveSchema);
        return coreDdl + relationalDdl + seedDml;
    }
}
