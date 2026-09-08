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
    int timeoutSeconds
) : ICdcManagedDatabaseProvisioner
{
    public bool CreateDatabase() => provisioner.CreateDatabaseIfNotExists(connectionString);

    public void ProvisionSchema(bool databaseWasCreated)
    {
        provisioner.CheckOrConfigureMvcc(connectionString, databaseWasCreated);
        provisioner.PreflightSeedValidation(connectionString, schema);
        provisioner.ExecuteInTransaction(connectionString, sql, timeoutSeconds);
    }

    public void ValidateSchema() => provisioner.PreflightSeedValidation(connectionString, schema);

    public async Task<string> ReadSourceFingerprintAsync(CancellationToken cancellationToken)
    {
        var result = await sourceReader.ReadFingerprintAsync(connectionString, cancellationToken);
        return result.Fingerprint is { } fingerprint
            ? fingerprint.Value
            : throw new CdcWorkflowStateException(CdcWorkflowStateFailure.Unavailable);
    }
}
