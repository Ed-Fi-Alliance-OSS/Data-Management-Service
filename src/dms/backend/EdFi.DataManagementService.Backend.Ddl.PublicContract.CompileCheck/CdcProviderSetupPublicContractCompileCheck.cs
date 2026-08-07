// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using EdFi.DataManagementService.Backend.Ddl;
using EdFi.DataManagementService.Backend.External;
using Microsoft.Extensions.DependencyInjection;

namespace EdFi.DataManagementService.Backend.Ddl.PublicContract.CompileCheck;

public static class CdcProviderSetupPublicContractCompileCheck
{
    public static IServiceCollection RegisterBuiltInCdcProviderSetup(IServiceCollection services) =>
        services.AddCdcProviderSetup();

    public static Task<CdcProviderSetupResult> InvokePostgresqlSetupAsync(
        ICdcProviderSetupService setupService,
        EffectiveSchemaSet effectiveSchemaSet,
        DbConnection connection,
        string sourceIdentity,
        CancellationToken cancellationToken = default
    )
    {
        var request = BuildPostgresqlRequest(effectiveSchemaSet, connection, sourceIdentity);

        return setupService.SetupAsync(request, cancellationToken);
    }

    public static CdcProviderSetupRequest BuildPostgresqlRequest(
        EffectiveSchemaSet effectiveSchemaSet,
        DbConnection connection,
        string sourceIdentity
    )
    {
        var emission = DdlPipelineHelpers.BuildDdlEmissionForDialect(effectiveSchemaSet, SqlDialect.Pgsql);

        return new CdcProviderSetupRequest(
            provider: CdcProvider.Postgresql,
            mode: CdcProviderSetupMode.InitialCreateOrExactMatch,
            boundPhysicalSourceFingerprint: CdcSourceFingerprintMetadata.Compute(
                CdcProvider.Postgresql,
                sourceIdentity
            ),
            setupPrincipal: new CdcSetupPrincipalContext(new CdcSafeName("setup_principal")),
            connectorPrincipal: new CdcConnectorPrincipal(new CdcSafeName("connector_principal")),
            artifactNames: CdcProviderArtifactNames.ForPostgresql(
                new CdcSafeName("dms_binding_publication"),
                new CdcSafeName("dms_binding_slot")
            ),
            artifactOutput: new CdcProviderArtifactOutputRequest(IncludeManifestPayload: true),
            expectedSourceInventory: emission.CdcSourceInventory,
            dmsManagedTableInventory: emission.CdcDmsManagedTableInventory,
            databaseExecutor: new DbConnectionCdcProviderDatabaseExecutor(connection)
        );
    }

    public static CdcProviderSetupRequest BuildSqlServerRequest(
        EffectiveSchemaSet effectiveSchemaSet,
        DbConnection connection,
        string sourceIdentity
    )
    {
        var emission = DdlPipelineHelpers.BuildDdlEmissionForDialect(effectiveSchemaSet, SqlDialect.Mssql);

        return new CdcProviderSetupRequest(
            provider: CdcProvider.SqlServer,
            mode: CdcProviderSetupMode.ValidateOnly,
            boundPhysicalSourceFingerprint: CdcSourceFingerprintMetadata.Compute(
                CdcProvider.SqlServer,
                sourceIdentity
            ),
            setupPrincipal: new CdcSetupPrincipalContext(new CdcSafeName("setup_principal")),
            connectorPrincipal: new CdcConnectorPrincipal(new CdcSafeName("connector_principal")),
            artifactNames: CdcProviderArtifactNames.ForSqlServer(
                new CdcSafeName("dms_binding_gate"),
                new Dictionary<CdcSourceTableKind, CdcSafeName>
                {
                    [CdcSourceTableKind.Document] = new("dms_binding_document"),
                    [CdcSourceTableKind.DocumentCache] = new("dms_binding_document_cache"),
                    [CdcSourceTableKind.CdcHeartbeat] = new("dms_binding_cdc_heartbeat"),
                }
            ),
            artifactOutput: new CdcProviderArtifactOutputRequest(IncludeManifestPayload: true),
            expectedSourceInventory: emission.CdcSourceInventory,
            dmsManagedTableInventory: emission.CdcDmsManagedTableInventory,
            databaseExecutor: new DbConnectionCdcProviderDatabaseExecutor(connection)
        );
    }
}
