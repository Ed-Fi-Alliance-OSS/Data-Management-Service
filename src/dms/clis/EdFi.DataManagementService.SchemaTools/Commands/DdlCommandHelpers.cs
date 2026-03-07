// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Ddl;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.RelationalModel.Build;
using EdFi.DataManagementService.Backend.RelationalModel.Schema;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.Utilities;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.SchemaTools.Commands;

/// <summary>
/// Shared helpers used by both <see cref="DdlEmitCommand"/> and <see cref="DdlProvisionCommand"/>.
/// </summary>
internal static class DdlCommandHelpers
{
    internal record DdlBuildResult(
        EffectiveSchemaSet EffectiveSchemaSet,
        DerivedRelationalModelSet ModelSet,
        string CombinedSql
    );

    /// <summary>
    /// Builds the EffectiveSchemaSet from normalized schema nodes and logs summary info.
    /// This is dialect-independent and can be called once, then reused across dialects.
    /// </summary>
    internal static EffectiveSchemaSet BuildEffectiveSchemaSet(
        ILogger logger,
        EffectiveSchemaSetBuilder schemaSetBuilder,
        ApiSchemaDocumentNodes normalizedNodes
    )
    {
        var effectiveSchemaSet = schemaSetBuilder.Build(normalizedNodes);
        var effectiveSchemaInfo = effectiveSchemaSet.EffectiveSchema;

        logger.LogInformation(
            "Effective schema hash: {Hash}, resource keys: {ResourceKeyCount}",
            effectiveSchemaInfo.EffectiveSchemaHash,
            effectiveSchemaInfo.ResourceKeyCount
        );

        return effectiveSchemaSet;
    }

    /// <summary>
    /// Derives the relational model and generates combined DDL SQL for a single dialect
    /// from a pre-built EffectiveSchemaSet. Deep-clones the schema set to avoid mutating
    /// the original tree.
    /// </summary>
    internal static DdlBuildResult BuildDdlFromSchemaSet(
        ILogger logger,
        EffectiveSchemaSet effectiveSchemaSet,
        SqlDialect dialect
    )
    {
        var (modelSet, combinedSql) = DdlPipelineHelpers.BuildDdlForDialect(effectiveSchemaSet, dialect);

        LogModelDiagnostics(logger, modelSet);

        return new DdlBuildResult(effectiveSchemaSet, modelSet, combinedSql);
    }

    /// <summary>
    /// Convenience method that builds the EffectiveSchemaSet, derives the relational model,
    /// logs diagnostics, and generates the combined DDL SQL for a single dialect.
    /// </summary>
    internal static DdlBuildResult BuildDdl(
        ILogger logger,
        EffectiveSchemaSetBuilder schemaSetBuilder,
        ApiSchemaDocumentNodes normalizedNodes,
        SqlDialect dialect
    )
    {
        var effectiveSchemaSet = BuildEffectiveSchemaSet(logger, schemaSetBuilder, normalizedNodes);
        return BuildDdlFromSchemaSet(logger, effectiveSchemaSet, dialect);
    }

    /// <summary>
    /// Logs diagnostics for skipped key-unification constraints and decimal precision fallbacks.
    /// </summary>
    internal static void LogModelDiagnostics(ILogger logger, DerivedRelationalModelSet modelSet)
    {
        foreach (var resource in modelSet.ConcreteResourcesInNameOrder)
        {
            var resourceLabel = LoggingSanitizer.SanitizeForLogging(
                $"{resource.ResourceKey.Resource.ProjectName}:{resource.ResourceKey.Resource.ResourceName}"
            );

            var skipped = resource.RelationalModel.KeyUnificationEqualityConstraints.Skipped;
            if (skipped.Count > 0)
            {
                logger.LogDebug(
                    "Resource {Resource}: {Count} key-unification constraint(s) skipped due to unresolved binding paths",
                    resourceLabel,
                    skipped.Count
                );
                foreach (var entry in skipped)
                {
                    logger.LogDebug(
                        "  Skipped: source={Source}, target={Target}, unresolved={Endpoint}",
                        LoggingSanitizer.SanitizeForLogging(entry.SourcePath.Canonical),
                        LoggingSanitizer.SanitizeForLogging(entry.TargetPath.Canonical),
                        LoggingSanitizer.SanitizeForLogging(entry.UnresolvedEndpoint)
                    );
                }
            }

            var fallbacks = resource.RelationalModel.DecimalPrecisionFallbacks;
            if (fallbacks.Count > 0)
            {
                logger.LogDebug(
                    "Resource {Resource}: {Count} decimal property/ies fell back to default precision (18,4)",
                    resourceLabel,
                    fallbacks.Count
                );
                foreach (var entry in fallbacks)
                {
                    logger.LogDebug(
                        "  Fallback: path={Path}, reason={Reason}",
                        LoggingSanitizer.SanitizeForLogging(entry.SourcePath.Canonical),
                        LoggingSanitizer.SanitizeForLogging(entry.Reason)
                    );
                }
            }
        }
    }
}
