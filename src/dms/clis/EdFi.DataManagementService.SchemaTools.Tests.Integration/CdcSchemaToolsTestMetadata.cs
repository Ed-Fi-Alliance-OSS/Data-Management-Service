// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Ddl;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.Startup;
using Microsoft.Extensions.Logging.Abstractions;

namespace EdFi.DataManagementService.SchemaTools.Tests.Integration;

internal static class CdcSchemaToolsTestMetadata
{
    private static readonly Lazy<DdlPipelineEmission> _minimalPostgresqlEmission = new(() =>
        LoadMinimalDdlEmission(SqlDialect.Pgsql)
    );

    private static readonly Lazy<DdlPipelineEmission> _minimalMssqlEmission = new(() =>
        LoadMinimalDdlEmission(SqlDialect.Mssql)
    );

    public static DdlPipelineEmission BuildMinimalDdlEmission(SqlDialect dialect) =>
        dialect switch
        {
            SqlDialect.Pgsql => _minimalPostgresqlEmission.Value,
            SqlDialect.Mssql => _minimalMssqlEmission.Value,
            _ => throw new ArgumentOutOfRangeException(nameof(dialect), dialect, "Unsupported dialect."),
        };

    private static DdlPipelineEmission LoadMinimalDdlEmission(SqlDialect dialect)
    {
        var loadResult = CreateApiSchemaFileLoader().Load(CliTestHelper.GetMinimalSchemaPath(), []);
        var normalizedNodes = loadResult is ApiSchemaFileLoadResult.SuccessResult success
            ? success.NormalizedNodes
            : throw new InvalidOperationException(
                $"Failed to load minimal ApiSchema for CDC metadata: {loadResult}"
            );
        var effectiveSchemaSet = CreateSchemaSetBuilder().Build(normalizedNodes);

        return DdlPipelineHelpers.BuildDdlEmissionForDialect(effectiveSchemaSet, dialect);
    }

    private static ApiSchemaFileLoader CreateApiSchemaFileLoader() =>
        new(
            new ApiSchemaInputNormalizer(NullLogger<ApiSchemaInputNormalizer>.Instance),
            NullLogger<ApiSchemaFileLoader>.Instance
        );

    private static EffectiveSchemaSetBuilder CreateSchemaSetBuilder() =>
        new(
            new EffectiveSchemaHashProvider(NullLogger<EffectiveSchemaHashProvider>.Instance),
            new ResourceKeySeedProvider(NullLogger<ResourceKeySeedProvider>.Instance)
        );
}
