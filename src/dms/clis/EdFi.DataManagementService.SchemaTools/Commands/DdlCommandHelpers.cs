// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.Ddl;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.Utilities;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.SchemaTools.Commands;

/// <summary>
/// Shared helpers used by both <see cref="DdlEmitCommand"/> and <see cref="DdlProvisionCommand"/>.
/// </summary>
internal static class DdlCommandHelpers
{
    internal static (ISqlDialect Dialect, ISqlDialectRules Rules) CreateDialect(SqlDialect dialect)
    {
        return dialect switch
        {
            SqlDialect.Pgsql => CreatePgsqlDialect(),
            SqlDialect.Mssql => CreateMssqlDialect(),
            _ => throw new ArgumentOutOfRangeException(nameof(dialect), dialect, "Unsupported dialect"),
        };

        static (ISqlDialect, ISqlDialectRules) CreatePgsqlDialect()
        {
            var rules = new PgsqlDialectRules();
            return (new PgsqlDialect(rules), rules);
        }

        static (ISqlDialect, ISqlDialectRules) CreateMssqlDialect()
        {
            var rules = new MssqlDialectRules();
            return (new MssqlDialect(rules), rules);
        }
    }

    internal static EffectiveSchemaSet CloneEffectiveSchemaSet(EffectiveSchemaSet original)
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
