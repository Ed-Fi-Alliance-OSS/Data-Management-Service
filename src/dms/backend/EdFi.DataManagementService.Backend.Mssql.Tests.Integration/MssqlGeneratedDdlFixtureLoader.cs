// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Ddl;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Tests.Common;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Mssql.Tests.Integration;

internal sealed record MssqlGeneratedDdlFixture(
    string FixtureDirectory,
    EffectiveSchemaSet EffectiveSchemaSet,
    DerivedRelationalModelSet ModelSet,
    string GeneratedDdl
);

internal static class MssqlGeneratedDdlFixtureLoader
{
    public static MssqlGeneratedDdlFixture LoadFromRepositoryRelativePath(string relativePath)
    {
        return LoadFromFixtureDirectory(
            FixturePathResolver.ResolveRepositoryRelativePath(
                TestContext.CurrentContext.TestDirectory,
                relativePath
            )
        );
    }

    public static MssqlGeneratedDdlFixture LoadFromFixtureDirectory(string fixtureDirectory)
    {
        var resolvedFixtureDirectory = Path.GetFullPath(fixtureDirectory);
        var effectiveSchemaSet = EffectiveSchemaFixtureLoader.LoadFromFixtureDirectory(
            resolvedFixtureDirectory
        );
        var (modelSet, generatedDdl) = DdlPipelineHelpers.BuildDdlForDialect(
            effectiveSchemaSet,
            SqlDialect.Mssql
        );

        return new(resolvedFixtureDirectory, effectiveSchemaSet, modelSet, generatedDdl);
    }
}
