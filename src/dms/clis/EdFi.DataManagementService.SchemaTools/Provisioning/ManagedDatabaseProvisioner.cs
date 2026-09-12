// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Cdc;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.DocumentCache;

namespace EdFi.DataManagementService.SchemaTools.Provisioning;

/// <summary>Connects the existing DDL provider and E18 source reader to managed creation journaling.</summary>
public sealed class ManagedDatabaseProvisioner(
    IDatabaseProvisioner provisioner,
    IDocumentCachePhysicalSourceFingerprintReader sourceReader,
    string connectionString,
    EffectiveSchemaInfo schema,
    string sql,
    int timeoutSeconds,
    CdcProjectionPrerequisiteMode projectionPrerequisites = CdcProjectionPrerequisiteMode.None
) : ICdcManagedDatabaseProvisioner
{
    public bool CreateDatabase() => provisioner.CreateDatabaseIfNotExists(connectionString);

    public void ProvisionSchema(bool databaseWasCreated)
    {
        provisioner.CheckOrConfigureMvcc(connectionString, databaseWasCreated);
        CheckProjectionPrerequisites(databaseWasCreated);
        provisioner.PreflightSeedValidation(connectionString, schema);
        provisioner.ExecuteInTransaction(connectionString, sql, timeoutSeconds);
    }

    public void ValidateSchema()
    {
        // The controller has reconciled the durable receipt and live source before entering here.
        // Even an initial retry is inspection-only; ownership never authorizes drift repair.
        CheckProjectionPrerequisites(databaseWasCreated: false);
        provisioner.PreflightSeedValidation(connectionString, schema);
    }

    private void CheckProjectionPrerequisites(bool databaseWasCreated)
    {
        if (projectionPrerequisites != CdcProjectionPrerequisiteMode.None)
        {
            provisioner.CheckCdcProjectionPrerequisites(
                connectionString,
                databaseWasCreated
                    && projectionPrerequisites == CdcProjectionPrerequisiteMode.OwnedLocalSqlServer
            );
        }
    }

    public async Task<string> ReadSourceFingerprintAsync(CancellationToken cancellationToken)
    {
        var result = await sourceReader.ReadFingerprintAsync(connectionString, cancellationToken);
        return result.Fingerprint is { } fingerprint
            ? fingerprint.Value
            : throw new CdcWorkflowStateException(CdcWorkflowStateFailure.Unavailable);
    }
}

/// <summary>Deployment configuration, not database ownership or reusable readiness evidence.</summary>
public enum CdcProjectionPrerequisiteMode
{
    None,
    Inspect,
    OwnedLocalSqlServer,
}
