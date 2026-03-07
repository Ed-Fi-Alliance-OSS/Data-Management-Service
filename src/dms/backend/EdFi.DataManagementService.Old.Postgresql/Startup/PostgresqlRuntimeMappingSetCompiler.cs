// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Backend.RelationalModel.Build;
using EdFi.DataManagementService.Backend.RelationalModel.Schema;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.Startup;

namespace EdFi.DataManagementService.Old.Postgresql.Startup;

internal sealed class PostgresqlRuntimeMappingSetCompiler(
    IApiSchemaProvider apiSchemaProvider,
    IApiSchemaInputNormalizer apiSchemaInputNormalizer,
    EffectiveSchemaSetBuilder effectiveSchemaSetBuilder,
    MappingSetCompiler mappingSetCompiler
)
{
    public MappingSetKey GetCurrentKey()
    {
        return CreateKey(BuildCurrentEffectiveSchemaSet().EffectiveSchema);
    }

    public Task<MappingSet> CompileAsync(MappingSetKey expectedKey)
    {
        var effectiveSchemaSet = BuildCurrentEffectiveSchemaSet();
        var actualKey = CreateKey(effectiveSchemaSet.EffectiveSchema);

        if (actualKey != expectedKey)
        {
            throw new InvalidOperationException(
                "Cannot compile PostgreSQL runtime mapping set for "
                    + $"'{FormatKey(expectedKey)}': current schema resolved to '{FormatKey(actualKey)}'."
            );
        }

        var derivedModelSet = new DerivedRelationalModelSetBuilder(
            RelationalModelSetPasses.CreateDefault()
        ).Build(CloneEffectiveSchemaSet(effectiveSchemaSet), SqlDialect.Pgsql, new PgsqlDialectRules());

        return Task.FromResult(mappingSetCompiler.Compile(derivedModelSet));
    }

    private EffectiveSchemaSet BuildCurrentEffectiveSchemaSet()
    {
        var normalizationResult = apiSchemaInputNormalizer.Normalize(apiSchemaProvider.GetApiSchemaNodes());

        return normalizationResult switch
        {
            ApiSchemaNormalizationResult.SuccessResult success => effectiveSchemaSetBuilder.Build(
                success.NormalizedNodes
            ),
            ApiSchemaNormalizationResult.MissingOrMalformedProjectSchemaResult failure =>
                throw new InvalidOperationException(
                    $"PostgreSQL runtime mapping initialization failed for '{failure.SchemaSource}': {failure.Details}"
                ),
            ApiSchemaNormalizationResult.ApiSchemaVersionMismatchResult failure =>
                throw new InvalidOperationException(
                    "PostgreSQL runtime mapping initialization failed: "
                        + $"apiSchemaVersion mismatch in '{failure.SchemaSource}': expected '{failure.ExpectedVersion}', "
                        + $"got '{failure.ActualVersion}'."
                ),
            ApiSchemaNormalizationResult.ProjectEndpointNameCollisionResult failure =>
                throw new InvalidOperationException(
                    "PostgreSQL runtime mapping initialization failed: duplicate "
                        + $"projectEndpointName(s) found: {string.Join("; ", failure.Collisions.Select(c => $"'{c.ProjectEndpointName}' in [{string.Join(", ", c.ConflictingSources)}]"))}"
                ),
            _ => throw new InvalidOperationException(
                "PostgreSQL runtime mapping initialization failed: unknown schema normalization result."
            ),
        };
    }

    private static MappingSetKey CreateKey(EffectiveSchemaInfo effectiveSchemaInfo)
    {
        return new MappingSetKey(
            EffectiveSchemaHash: effectiveSchemaInfo.EffectiveSchemaHash,
            Dialect: SqlDialect.Pgsql,
            RelationalMappingVersion: effectiveSchemaInfo.RelationalMappingVersion
        );
    }

    private static string FormatKey(MappingSetKey key)
    {
        return $"{key.EffectiveSchemaHash}/{key.Dialect}/{key.RelationalMappingVersion}";
    }

    private static EffectiveSchemaSet CloneEffectiveSchemaSet(EffectiveSchemaSet original)
    {
        var clonedProjects = original
            .ProjectsInEndpointOrder.Select(project => new EffectiveProjectSchema(
                project.ProjectEndpointName,
                project.ProjectName,
                project.ProjectVersion,
                project.IsExtensionProject,
                (JsonObject)project.ProjectSchema.DeepClone()
            ))
            .ToArray();

        return new EffectiveSchemaSet(original.EffectiveSchema, clonedProjects);
    }
}
