// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Concurrent;
using System.Threading;
using EdFi.DataManagementService.Backend.Ddl;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Backend.Tests.Common;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Postgresql.Tests.Integration;

internal sealed record PostgresqlGeneratedDdlFixture(
    string FixtureDirectory,
    EffectiveSchemaSet EffectiveSchemaSet,
    DerivedRelationalModelSet ModelSet,
    MappingSet MappingSet,
    string GeneratedDdl
);

internal static class PostgresqlGeneratedDdlFixtureLoader
{
    private static readonly ConcurrentDictionary<string, Lazy<PostgresqlGeneratedDdlFixture>> _cache = new(
        StringComparer.Ordinal
    );

    public static PostgresqlGeneratedDdlFixture LoadFromRepositoryRelativePath(
        string relativePath,
        bool strict = false
    )
    {
        return LoadFromFixtureDirectory(
            FixturePathResolver.ResolveRepositoryRelativePath(
                TestContext.CurrentContext.TestDirectory,
                relativePath
            ),
            strict
        );
    }

    public static PostgresqlGeneratedDdlFixture LoadFromFixtureDirectory(
        string fixtureDirectory,
        bool strict = false
    )
    {
        var descriptor = EffectiveSchemaFixtureLoader.DescribeFixtureDirectory(fixtureDirectory);
        var cacheKey = $"{descriptor.CacheKey}|strict={strict}";
        var lazyFixture = _cache.GetOrAdd(
            cacheKey,
            _ => new(() => LoadFixture(descriptor, strict), LazyThreadSafetyMode.ExecutionAndPublication)
        );

        try
        {
            return lazyFixture.Value;
        }
        catch
        {
            _cache.TryRemove(new(cacheKey, lazyFixture));
            throw;
        }
    }

    private static PostgresqlGeneratedDdlFixture LoadFixture(
        EffectiveSchemaFixtureLoader.FixtureContentDescriptor descriptor,
        bool strict
    )
    {
        var effectiveSchemaSet = EffectiveSchemaFixtureLoader.LoadEffectiveSchemaSet(descriptor);
        var (modelSet, generatedDdl) = DdlPipelineHelpers.BuildDdlForDialect(
            effectiveSchemaSet,
            SqlDialect.Pgsql,
            strict
        );
        var mappingSet = new MappingSetCompiler().Compile(modelSet);

        return new(descriptor.FixtureDirectory, effectiveSchemaSet, modelSet, mappingSet, generatedDdl);
    }
}
