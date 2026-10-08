// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.SchemaTools.Provisioning;

namespace EdFi.DataManagementService.SchemaTools.Restamping;

public sealed record SchemaRestampFingerprint(
    short SingletonId,
    string ApiSchemaFormatVersion,
    string EffectiveSchemaHash,
    short ResourceKeyCount,
    byte[] ResourceKeySeedHash
);

public sealed record SchemaRestampComponent(string EffectiveSchemaHash, SchemaComponentRow Payload);

public sealed record SchemaRestampSnapshot(
    IReadOnlyList<SchemaRestampFingerprint> Fingerprints,
    IReadOnlyList<ResourceKeyRow> ResourceKeys,
    IReadOnlyList<SchemaRestampComponent> SchemaComponents
);

public sealed record SchemaRestampResult(
    bool Changed,
    string PreviousHash,
    string TargetHash,
    int ComponentCount
);

public enum SchemaRestampFailure
{
    Validation,
    ConfirmationRequired,
    Connection,
    PermissionDenied,
    Timeout,
    Cancelled,
    TransactionFailed,
    CommitOutcomeUnknown,
}

public sealed class SchemaRestampException(SchemaRestampFailure failure, string safeMessage)
    : Exception(safeMessage)
{
    public SchemaRestampFailure Failure { get; } = failure;
}

public interface ISchemaRestamper
{
    Task<SchemaRestampResult> RestampAsync(
        SqlDialect dialect,
        string connectionString,
        int commandTimeoutSeconds,
        EffectiveSchemaInfo target,
        bool migrationCompleted,
        CancellationToken cancellationToken
    );
}
