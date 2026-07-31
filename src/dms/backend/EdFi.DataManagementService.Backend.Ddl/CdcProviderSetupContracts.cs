// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Serialization;
using EdFi.DataManagementService.Backend.External;

namespace EdFi.DataManagementService.Backend.Ddl;

internal enum CdcProvider
{
    Postgresql,
    SqlServer,
}

internal enum CdcProviderSetupMode
{
    InitialCreateOrExactMatch,
    ValidateOnly,
}

internal enum CdcProviderSetupOutcome
{
    CreatedOrMatched,
    ExactMatch,
    Failed,
}

internal enum CdcSourceTableKind
{
    Document,
    DocumentCache,
    CdcHeartbeat,
}

internal enum CdcPrincipalKind
{
    None,
    SetupPrincipal,
    ConnectorPrincipal,
}

internal enum CdcProviderArtifactKind
{
    None,
    SourceTable,
    SourceColumn,
    HeartbeatTable,
    HeartbeatActionQuery,
    PostgresqlPublication,
    PostgresqlReplicaIdentity,
    PostgresqlReplicationSlot,
    SqlServerCaptureInstance,
    SqlServerGatingRole,
    Grant,
    SourceFingerprint,
    ProviderHistory,
}

internal enum CdcProviderArtifactState
{
    Created,
    Matched,
    Missing,
    Mismatched,
    Unavailable,
}

internal enum CdcProviderDiagnosticCategory
{
    SetupPrincipalFailure,
    ConnectorPrincipalPrivilegeFailure,
    MissingRequiredSourceObject,
    WorkTableCaptureViolation,
    WorkTableGrantViolation,
    ProviderHistoryUnavailable,
    ProviderHistoryLossEvidence,
    ValidationMismatch,
}

internal enum CdcProviderDiagnosticSeverity
{
    Info,
    Warning,
    Error,
}

internal enum CdcProviderRetryContinuityClassification
{
    None,
    Retryable,
    FailClosed,
    SourceHistoryUnknown,
    SourceHistoryLost,
}

internal sealed record CdcSourceFingerprint(string Version, string Value);

internal sealed record CdcSetupPrincipalContext(CdcSafeName SafePrincipalName);

internal sealed record CdcConnectorPrincipal(CdcSafeName SafePrincipalName);

internal interface ICdcConnectorPrincipalProbeFactory
{
    Task<CdcConnectorPrincipalProbeResult> ProbeAsync(
        CdcProviderSetupRequest request,
        CancellationToken cancellationToken
    );
}

internal sealed record CdcConnectorPrincipalProbeResult(
    IReadOnlyList<CdcGrantObservation> GrantInventory,
    IReadOnlyList<CdcProviderDiagnostic> Diagnostics
)
{
    public CdcConnectorPrincipalProbeResult()
        : this([], []) { }
}

internal sealed record CdcProviderArtifactOutputRequest(bool IncludeManifestPayload);

internal sealed record CdcPostgresqlProviderArtifactNames(
    CdcSafeName PublicationName,
    CdcSafeName ReplicationSlotName
);

internal sealed record CdcSqlServerProviderArtifactNames(
    CdcSafeName GatingRoleName,
    IReadOnlyDictionary<CdcSourceTableKind, CdcSafeName> CaptureInstanceNames
)
{
    public IReadOnlyDictionary<CdcSourceTableKind, CdcSafeName> CaptureInstanceNames { get; } =
        ValidateRequiredSourceTableDictionary(CaptureInstanceNames, nameof(CaptureInstanceNames));

    private static IReadOnlyDictionary<CdcSourceTableKind, CdcSafeName> ValidateRequiredSourceTableDictionary(
        IReadOnlyDictionary<CdcSourceTableKind, CdcSafeName> names,
        string parameterName
    )
    {
        ArgumentNullException.ThrowIfNull(names);

        var requiredKinds = CdcSourceInventoryContract.RequiredSourceTableKinds;
        if (names.Count != requiredKinds.Count || requiredKinds.Any(kind => !names.ContainsKey(kind)))
        {
            throw new ArgumentException(
                "SQL Server CDC capture instance names must be supplied for dms.Document, dms.DocumentCache, and dms.CdcHeartbeat only.",
                parameterName
            );
        }

        return names;
    }
}

internal sealed record CdcProviderArtifactNames(
    CdcPostgresqlProviderArtifactNames? Postgresql,
    CdcSqlServerProviderArtifactNames? SqlServer
)
{
    internal static CdcProviderArtifactNames ForPostgresql(
        CdcSafeName publicationName,
        CdcSafeName replicationSlotName
    ) => new(new CdcPostgresqlProviderArtifactNames(publicationName, replicationSlotName), null);

    internal static CdcProviderArtifactNames ForSqlServer(
        CdcSafeName gatingRoleName,
        IReadOnlyDictionary<CdcSourceTableKind, CdcSafeName> captureInstanceNames
    ) => new(null, new CdcSqlServerProviderArtifactNames(gatingRoleName, captureInstanceNames));

    internal void ValidateFor(CdcProvider provider)
    {
        var hasRequiredArtifacts = provider switch
        {
            CdcProvider.Postgresql => Postgresql is not null,
            CdcProvider.SqlServer => SqlServer is not null,
            _ => throw new ArgumentOutOfRangeException(
                nameof(provider),
                provider,
                "Unsupported CDC provider."
            ),
        };

        if (!hasRequiredArtifacts)
        {
            throw new ArgumentException(
                $"Binding-derived artifact names are required for provider {provider}.",
                nameof(provider)
            );
        }
    }
}

internal sealed record CdcSourceColumnInventory(
    DbColumnName ColumnName,
    string EmittedQuotedColumnName,
    int Ordinal,
    string ProviderDataType,
    bool IsNullable
)
{
    public string EmittedQuotedColumnName { get; } =
        ValidateSafeText(EmittedQuotedColumnName, nameof(EmittedQuotedColumnName));

    public int Ordinal { get; } =
        Ordinal > 0
            ? Ordinal
            : throw new ArgumentOutOfRangeException(
                nameof(Ordinal),
                Ordinal,
                "Column ordinal must be positive."
            );

    public string ProviderDataType { get; } = ValidateSafeText(ProviderDataType, nameof(ProviderDataType));

    private static string ValidateSafeText(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Value must be supplied.", parameterName);
        }

        if (value.Any(char.IsControl))
        {
            throw new ArgumentException("Value must not contain control characters.", parameterName);
        }

        return value;
    }
}

internal sealed record CdcSourceTableInventory(
    CdcSourceTableKind TableKind,
    DbTableName TableName,
    string EmittedQuotedTableName,
    IReadOnlyList<CdcSourceColumnInventory> Columns
)
{
    public string EmittedQuotedTableName { get; } =
        ValidateSafeText(EmittedQuotedTableName, nameof(EmittedQuotedTableName));

    public IReadOnlyList<CdcSourceColumnInventory> Columns { get; } = ValidateColumns(Columns);

    private static IReadOnlyList<CdcSourceColumnInventory> ValidateColumns(
        IReadOnlyList<CdcSourceColumnInventory> columns
    )
    {
        ArgumentNullException.ThrowIfNull(columns);

        if (columns.Count == 0)
        {
            throw new ArgumentException(
                "CDC source table inventory must include at least one column.",
                nameof(columns)
            );
        }

        var duplicateOrdinals = columns
            .GroupBy(column => column.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();

        if (duplicateOrdinals.Length > 0)
        {
            throw new ArgumentException("CDC source table column ordinals must be unique.", nameof(columns));
        }

        var expectedOrdinalOrder = Enumerable.Range(1, columns.Count).ToArray();
        if (!columns.Select(column => column.Ordinal).SequenceEqual(expectedOrdinalOrder))
        {
            throw new ArgumentException(
                "CDC source table columns must be supplied in table-ordinal order starting at 1.",
                nameof(columns)
            );
        }

        return columns;
    }

    private static string ValidateSafeText(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Value must be supplied.", parameterName);
        }

        if (value.Any(char.IsControl))
        {
            throw new ArgumentException("Value must not contain control characters.", parameterName);
        }

        return value;
    }
}

internal sealed record CdcProviderSetupRequest
{
    public CdcProviderSetupRequest(
        CdcProvider provider,
        CdcProviderSetupMode mode,
        CdcSourceFingerprint boundPhysicalSourceFingerprint,
        CdcSetupPrincipalContext setupPrincipal,
        CdcConnectorPrincipal connectorPrincipal,
        CdcProviderArtifactNames artifactNames,
        CdcProviderArtifactOutputRequest artifactOutput,
        IReadOnlyList<CdcSourceTableInventory> expectedSourceInventory,
        ICdcConnectorPrincipalProbeFactory? connectorPrincipalProbeFactory = null,
        ICdcProviderDatabaseExecutor? databaseExecutor = null
    )
    {
        ArgumentNullException.ThrowIfNull(boundPhysicalSourceFingerprint);
        ArgumentNullException.ThrowIfNull(setupPrincipal);
        ArgumentNullException.ThrowIfNull(connectorPrincipal);
        ArgumentNullException.ThrowIfNull(artifactNames);
        ArgumentNullException.ThrowIfNull(artifactOutput);

        artifactNames.ValidateFor(provider);

        Provider = provider;
        Mode = mode;
        BoundPhysicalSourceFingerprint = boundPhysicalSourceFingerprint;
        SetupPrincipal = setupPrincipal;
        ConnectorPrincipal = connectorPrincipal;
        ArtifactNames = artifactNames;
        ArtifactOutput = artifactOutput;
        ExpectedSourceInventory = CdcSourceInventoryContract.ValidateRequiredSourceInventory(
            expectedSourceInventory,
            nameof(expectedSourceInventory)
        );
        ConnectorPrincipalProbeFactory = connectorPrincipalProbeFactory;
        DatabaseExecutor = databaseExecutor;
    }

    public CdcProvider Provider { get; }
    public CdcProviderSetupMode Mode { get; }
    public CdcSourceFingerprint BoundPhysicalSourceFingerprint { get; }
    public CdcSetupPrincipalContext SetupPrincipal { get; }
    public CdcConnectorPrincipal ConnectorPrincipal { get; }
    public CdcProviderArtifactNames ArtifactNames { get; }
    public CdcProviderArtifactOutputRequest ArtifactOutput { get; }
    public IReadOnlyList<CdcSourceTableInventory> ExpectedSourceInventory { get; }

    [JsonIgnore]
    public ICdcConnectorPrincipalProbeFactory? ConnectorPrincipalProbeFactory { get; }

    [JsonIgnore]
    public ICdcProviderDatabaseExecutor? DatabaseExecutor { get; }
}

internal sealed record CdcProviderSetupResult(
    CdcProvider Provider,
    CdcProviderSetupMode Mode,
    CdcProviderSetupOutcome Outcome,
    CdcSourceFingerprint BoundPhysicalSourceFingerprint,
    CdcSourceFingerprint? ObservedSourceFingerprint,
    IReadOnlyList<CdcProviderArtifactObservation> ArtifactInventory,
    IReadOnlyList<CdcGrantObservation> GrantInventory,
    IReadOnlyList<CdcSourceTableInventory> SourceTableInventory,
    IReadOnlyList<CdcExpectedMessageKeyColumns> ExpectedMessageKeyColumns,
    CdcHeartbeatActionQuery? HeartbeatActionQuery,
    IReadOnlyList<CdcProviderHistoryObservation> ProviderHistoryObservations,
    CdcProviderManifestPayload? ManifestPayload,
    IReadOnlyList<CdcProviderDiagnostic> Diagnostics
);

internal sealed record CdcProviderArtifactObservation(
    CdcProviderArtifactKind ArtifactKind,
    CdcSafeName SafeArtifactName,
    CdcProviderArtifactState State,
    IReadOnlyDictionary<string, string> SafeObservedValues
);

internal sealed record CdcGrantObservation(
    CdcPrincipalKind PrincipalKind,
    CdcSafeName SafePrincipalName,
    CdcProviderArtifactKind ArtifactKind,
    CdcSafeName SafeObjectName,
    IReadOnlyList<string> Privileges,
    IReadOnlyList<DbColumnName> Columns
);

internal sealed record CdcExpectedMessageKeyColumns(
    CdcSourceTableKind TableKind,
    IReadOnlyList<DbColumnName> KeyColumns
);

internal sealed record CdcHeartbeatActionQuery(string Sql, string Sha256Hash);

internal sealed record CdcProviderHistoryObservation(
    CdcProviderArtifactKind ArtifactKind,
    CdcSafeName SafeArtifactName,
    IReadOnlyDictionary<string, string> SafeObservedValues,
    CdcProviderRetryContinuityClassification Classification
);

internal sealed record CdcProviderManifestPayload(CdcSafeName FileName, string Json);

internal sealed record CdcProviderDiagnostic(
    string Code,
    CdcProviderDiagnosticCategory Category,
    CdcProviderDiagnosticSeverity Severity,
    CdcPrincipalKind PrincipalKind,
    CdcProviderArtifactKind ArtifactKind,
    CdcSafeName SafeName,
    string? ExpectedValue,
    string? ObservedValue,
    string? ProviderErrorClass,
    CdcProviderRetryContinuityClassification Classification
);

internal readonly record struct CdcSafeName
{
    public CdcSafeName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Safe CDC names must not be empty.", nameof(value));
        }

        if (value.Any(char.IsControl))
        {
            throw new ArgumentException("Safe CDC names must not contain control characters.", nameof(value));
        }

        Value = value;
    }

    public string Value { get; }

    public override string ToString() => Value;
}

internal static class CdcSourceInventoryContract
{
    public static IReadOnlyList<CdcSourceTableKind> RequiredSourceTableKinds { get; } =
    [CdcSourceTableKind.Document, CdcSourceTableKind.DocumentCache, CdcSourceTableKind.CdcHeartbeat];

    public static IReadOnlyList<CdcSourceTableInventory> ValidateRequiredSourceInventory(
        IReadOnlyList<CdcSourceTableInventory> sourceInventory,
        string parameterName
    )
    {
        ArgumentNullException.ThrowIfNull(sourceInventory);

        var requiredKinds = RequiredSourceTableKinds;
        var observedKinds = sourceInventory.Select(table => table.TableKind).ToArray();
        var missingKinds = requiredKinds.Except(observedKinds).ToArray();
        var extraKinds = observedKinds.Except(requiredKinds).ToArray();
        var duplicateKinds = observedKinds
            .GroupBy(kind => kind)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();

        if (missingKinds.Length > 0 || extraKinds.Length > 0 || duplicateKinds.Length > 0)
        {
            throw new ArgumentException(
                "CDC source inventory must contain exactly dms.Document, dms.DocumentCache, and dms.CdcHeartbeat.",
                parameterName
            );
        }

        return sourceInventory;
    }
}
