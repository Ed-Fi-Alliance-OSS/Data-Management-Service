// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Backend.RelationalModel.Build;
using EdFi.DataManagementService.Core.Startup;

namespace EdFi.DataManagementService.Old.Postgresql.Startup;

internal sealed class PostgresqlRuntimeMappingSetCompiler(
    IEffectiveSchemaSetProvider effectiveSchemaSetProvider,
    MappingSetCompiler mappingSetCompiler
)
{
    public MappingSetKey GetCurrentKey()
    {
        return CreateKey(GetCurrentEffectiveSchemaSet().EffectiveSchema);
    }

    public Task<MappingSet> CompileAsync(MappingSetKey expectedKey)
    {
        var effectiveSchemaSet = GetCurrentEffectiveSchemaSet();
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

    private EffectiveSchemaSet GetCurrentEffectiveSchemaSet()
    {
        try
        {
            return effectiveSchemaSetProvider.EffectiveSchemaSet;
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException(
                "PostgreSQL runtime mapping initialization failed: authoritative effective schema startup state is unavailable. "
                    + "Run API schema initialization before backend mapping initialization.",
                ex
            );
        }
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
