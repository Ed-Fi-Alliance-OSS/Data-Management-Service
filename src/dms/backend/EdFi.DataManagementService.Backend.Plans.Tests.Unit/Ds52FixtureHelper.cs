// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;

namespace EdFi.DataManagementService.Backend.Plans.Tests.Unit;

/// <summary>
/// Shared helper for tests that compile the authoritative DS 5.2 schema
/// into a DerivedRelationalModelSet and MappingSet.
/// </summary>
internal static class Ds52FixtureHelper
{
    public const string FixturePath =
        "../Fixtures/authoritative/ds-5.2/inputs/ds-5.2-api-schema-authoritative.json";

    /// <summary>
    /// Builds the derived relational model and compiles the mapping set for the
    /// authoritative DS 5.2 schema. Defaults to PostgreSQL but supports MSSQL
    /// for dialect-specific tests (e.g. column name truncation, quoting rules).
    /// </summary>
    public static (DerivedRelationalModelSet ModelSet, MappingSet MappingSet) BuildAndCompile(
        SqlDialect dialect = SqlDialect.Pgsql
    )
    {
        var modelSet = RuntimePlanFixtureModelSetBuilder.Build(FixturePath, dialect);
        var compiler = new MappingSetCompiler();
        var mappingSet = compiler.Compile(modelSet);
        return (modelSet, mappingSet);
    }
}
