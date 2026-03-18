// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.RelationalModel.Build;
using static EdFi.DataManagementService.Backend.External.LogSanitizer;

namespace EdFi.DataManagementService.Backend.Plans;

/// <summary>
/// Dialect-neutral runtime mapping set compiler. Derives a relational model from the
/// effective schema set and compiles plans for the configured dialect.
/// </summary>
/// <param name="effectiveSchemaSetAccessor">
/// Delegate that returns the current <see cref="EffectiveSchemaSet"/>. Typically wired
/// to <c>IEffectiveSchemaSetProvider.EffectiveSchemaSet</c> at DI registration time.
/// </param>
/// <param name="mappingSetCompiler">The plan compiler.</param>
/// <param name="dialect">The target SQL dialect.</param>
/// <param name="dialectRules">The dialect-specific rules for model derivation.</param>
public sealed class RuntimeMappingSetCompiler(
    Func<EffectiveSchemaSet> effectiveSchemaSetAccessor,
    MappingSetCompiler mappingSetCompiler,
    SqlDialect dialect,
    ISqlDialectRules dialectRules
) : IRuntimeMappingSetCompiler
{
    /// <inheritdoc />
    public SqlDialect Dialect => dialect;

    /// <inheritdoc />
    public MappingSetKey GetCurrentKey()
    {
        return CreateKey(GetCurrentEffectiveSchemaSet().EffectiveSchema);
    }

    /// <inheritdoc />
    public Task<MappingSet> CompileAsync(MappingSetKey expectedKey, CancellationToken cancellationToken)
    {
        var effectiveSchemaSet = GetCurrentEffectiveSchemaSet();
        var actualKey = CreateKey(effectiveSchemaSet.EffectiveSchema);

        if (actualKey != expectedKey)
        {
            throw new InvalidOperationException(
                $"Cannot compile {dialect} runtime mapping set for "
                    + $"'{FormatKey(expectedKey)}': current schema resolved to '{FormatKey(actualKey)}'."
            );
        }

        var derivedModelSet = new DerivedRelationalModelSetBuilder(
            RelationalModelSetPasses.CreateDefault()
        ).Build(CloneEffectiveSchemaSet(effectiveSchemaSet), dialect, dialectRules);

        return Task.FromResult(mappingSetCompiler.Compile(derivedModelSet));
    }

    private EffectiveSchemaSet GetCurrentEffectiveSchemaSet()
    {
        try
        {
            return effectiveSchemaSetAccessor();
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException(
                $"{dialect} runtime mapping initialization failed: authoritative effective schema "
                    + "startup state is unavailable. Run API schema initialization before backend "
                    + "mapping initialization.",
                ex
            );
        }
    }

    private MappingSetKey CreateKey(EffectiveSchemaInfo effectiveSchemaInfo)
    {
        return new MappingSetKey(
            EffectiveSchemaHash: effectiveSchemaInfo.EffectiveSchemaHash,
            Dialect: dialect,
            RelationalMappingVersion: effectiveSchemaInfo.RelationalMappingVersion
        );
    }

    private static string FormatKey(MappingSetKey key)
    {
        return $"{SanitizeForLog(key.EffectiveSchemaHash)}/{key.Dialect}/{SanitizeForLog(key.RelationalMappingVersion)}";
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
