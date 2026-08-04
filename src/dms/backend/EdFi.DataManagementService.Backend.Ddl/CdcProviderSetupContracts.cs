// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Security.Cryptography;
using System.Text;
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

internal sealed record CdcProviderArtifactOutputRequest
{
    public CdcProviderArtifactOutputRequest(
        bool IncludeManifestPayload,
        string? ManifestOutputDirectoryPath = null
    )
    {
        if (ManifestOutputDirectoryPath is not null && string.IsNullOrWhiteSpace(ManifestOutputDirectoryPath))
        {
            throw new ArgumentException(
                "CDC provider manifest output directory must not be empty when supplied.",
                nameof(ManifestOutputDirectoryPath)
            );
        }

        this.IncludeManifestPayload = IncludeManifestPayload || ManifestOutputDirectoryPath is not null;
        this.ManifestOutputDirectoryPath = ManifestOutputDirectoryPath;
    }

    public bool IncludeManifestPayload { get; }

    [JsonIgnore]
    public string? ManifestOutputDirectoryPath { get; }

    internal bool ShouldCreateManifestPayload =>
        IncludeManifestPayload || ManifestOutputDirectoryPath is not null;
}

internal sealed record CdcPostgresqlProviderArtifactNames(
    CdcSafeName PublicationName,
    CdcSafeName ReplicationSlotName
)
{
    private const int MaxIdentifierUtf8Bytes = 63;

    public CdcSafeName PublicationName { get; } =
        ValidatePostgresqlIdentifier(PublicationName, nameof(PublicationName));

    public CdcSafeName ReplicationSlotName { get; } =
        ValidatePostgresqlIdentifier(ReplicationSlotName, nameof(ReplicationSlotName));

    private static CdcSafeName ValidatePostgresqlIdentifier(CdcSafeName name, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(name.Value))
        {
            throw new ArgumentException("PostgreSQL CDC artifact names must be supplied.", parameterName);
        }

        if (Encoding.UTF8.GetByteCount(name.Value) > MaxIdentifierUtf8Bytes)
        {
            throw new ArgumentException(
                "PostgreSQL CDC artifact names must be at most 63 UTF-8 bytes.",
                parameterName
            );
        }

        return name;
    }
}

internal sealed record CdcPostgresqlInitialReplicationSlotProof
{
    private const string DatabaseIdentityTokenPrefix = "postgresql_database_identity_sha256:";

    public CdcPostgresqlInitialReplicationSlotProof(
        CdcSafeName replicationSlotName,
        CdcSourceFingerprint sourceFingerprint,
        CdcSafeName databaseIdentityToken,
        string retainedRestartLsn,
        string retainedConfirmedFlushLsn
    )
    {
        ArgumentNullException.ThrowIfNull(sourceFingerprint);

        ReplicationSlotName = replicationSlotName;
        SourceFingerprint = sourceFingerprint;
        DatabaseIdentityToken = ValidateDatabaseIdentityToken(
            databaseIdentityToken,
            nameof(databaseIdentityToken)
        );
        RetainedRestartLsn = ValidateRetainedPosition(retainedRestartLsn, nameof(retainedRestartLsn));
        RetainedConfirmedFlushLsn = ValidateRetainedPosition(
            retainedConfirmedFlushLsn,
            nameof(retainedConfirmedFlushLsn)
        );
    }

    public CdcSafeName ReplicationSlotName { get; }

    public CdcSourceFingerprint SourceFingerprint { get; }

    public CdcSafeName DatabaseIdentityToken { get; }

    public string RetainedRestartLsn { get; }

    public string RetainedConfirmedFlushLsn { get; }

    public static CdcSafeName CreateDatabaseIdentityToken(string databaseIdentity)
    {
        if (string.IsNullOrWhiteSpace(databaseIdentity))
        {
            throw new ArgumentException(
                "PostgreSQL CDC database identity token source must be supplied.",
                nameof(databaseIdentity)
            );
        }

        if (databaseIdentity.Any(char.IsControl))
        {
            throw new ArgumentException(
                "PostgreSQL CDC database identity token source must not contain control characters.",
                nameof(databaseIdentity)
            );
        }

        var scopedIdentity = $"postgresql-database-identity:{databaseIdentity}";
        var hash = Convert
            .ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(scopedIdentity)))
            .ToLowerInvariant();

        return new CdcSafeName($"{DatabaseIdentityTokenPrefix}{hash}");
    }

    private static CdcSafeName ValidateDatabaseIdentityToken(CdcSafeName token, string parameterName)
    {
        if (!token.Value.StartsWith(DatabaseIdentityTokenPrefix, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "PostgreSQL CDC database identity proof must use a database identity token.",
                parameterName
            );
        }

        var hash = token.Value[DatabaseIdentityTokenPrefix.Length..];
        if (hash.Length != 64 || hash.Any(character => !IsLowerHex(character)))
        {
            throw new ArgumentException(
                "PostgreSQL CDC database identity token must be a lowercase SHA-256 token.",
                parameterName
            );
        }

        return token;
    }

    private static bool IsLowerHex(char character) => character is >= '0' and <= '9' or >= 'a' and <= 'f';

    private static string ValidateRetainedPosition(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("PostgreSQL CDC retained positions must be supplied.", parameterName);
        }

        if (value.Any(char.IsControl))
        {
            throw new ArgumentException(
                "PostgreSQL CDC retained positions must not contain control characters.",
                parameterName
            );
        }

        return value;
    }
}

internal sealed record CdcSqlServerProviderArtifactNames(
    CdcSafeName GatingRoleName,
    IReadOnlyDictionary<CdcSourceTableKind, CdcSafeName> CaptureInstanceNames
)
{
    private const int MaxGatingRoleNameLength = 128;
    private const int MaxCaptureInstanceNameLength = 100;

    public CdcSafeName GatingRoleName { get; } =
        ValidateSqlServerIdentifier(GatingRoleName, MaxGatingRoleNameLength, nameof(GatingRoleName));

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

        var validatedNames = names
            .OrderBy(pair => pair.Key)
            .ToDictionary(
                pair => pair.Key,
                pair =>
                    ValidateSqlServerIdentifier(
                        pair.Value,
                        MaxCaptureInstanceNameLength,
                        $"{parameterName}[{pair.Key}]"
                    )
            );

        var duplicateNames = validatedNames
            .Values.GroupBy(name => name.Value, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();

        if (duplicateNames.Length > 0)
        {
            throw new ArgumentException(
                "SQL Server CDC capture instance names must be unique within the database.",
                parameterName
            );
        }

        return validatedNames;
    }

    private static CdcSafeName ValidateSqlServerIdentifier(
        CdcSafeName name,
        int maxLength,
        string parameterName
    )
    {
        if (string.IsNullOrWhiteSpace(name.Value))
        {
            throw new ArgumentException("SQL Server CDC artifact names must be supplied.", parameterName);
        }

        if (name.Value.Length > maxLength)
        {
            throw new ArgumentException(
                $"SQL Server CDC artifact names must be at most {maxLength} characters.",
                parameterName
            );
        }

        return name;
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
        var hasOnlyRequiredArtifacts = provider switch
        {
            CdcProvider.Postgresql => Postgresql is not null && SqlServer is null,
            CdcProvider.SqlServer => SqlServer is not null && Postgresql is null,
            _ => throw new ArgumentOutOfRangeException(
                nameof(provider),
                provider,
                "Unsupported CDC provider."
            ),
        };

        if (!hasOnlyRequiredArtifacts)
        {
            throw new ArgumentException(
                $"Binding-derived artifact names must contain only names for provider {provider}.",
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

internal enum CdcDmsManagedTableKind
{
    Core,
    Authorization,
    Resource,
    TrackedChange,
}

internal sealed record CdcDmsManagedTableInventory(
    CdcDmsManagedTableKind TableKind,
    DbTableName TableName,
    string EmittedQuotedTableName
)
{
    public string EmittedQuotedTableName { get; } =
        ValidateSafeText(EmittedQuotedTableName, nameof(EmittedQuotedTableName));

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
        IReadOnlyList<CdcDmsManagedTableInventory> dmsManagedTableInventory,
        CdcPostgresqlInitialReplicationSlotProof? postgresqlInitialReplicationSlotProof = null,
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
        if (provider != CdcProvider.Postgresql && postgresqlInitialReplicationSlotProof is not null)
        {
            throw new ArgumentException(
                "PostgreSQL initial replication slot proof can only be supplied for PostgreSQL CDC setup.",
                nameof(postgresqlInitialReplicationSlotProof)
            );
        }

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
        DmsManagedTableInventory = CdcDmsManagedTableInventoryContract.Normalize(
            dmsManagedTableInventory,
            nameof(dmsManagedTableInventory)
        );
        PostgresqlInitialReplicationSlotProof = postgresqlInitialReplicationSlotProof;
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
    public IReadOnlyList<CdcDmsManagedTableInventory> DmsManagedTableInventory { get; }
    public CdcPostgresqlInitialReplicationSlotProof? PostgresqlInitialReplicationSlotProof { get; }

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
    [CdcSourceTableKind.DocumentCache, CdcSourceTableKind.Document, CdcSourceTableKind.CdcHeartbeat];

    private static readonly IReadOnlyDictionary<CdcSourceTableKind, int> _requiredSourceTableOrdinalByKind =
        RequiredSourceTableKinds
            .Select((kind, ordinal) => (kind, ordinal))
            .ToDictionary(entry => entry.kind, entry => entry.ordinal);

    public static int RequiredSourceTableOrdinal(CdcSourceTableKind tableKind) =>
        _requiredSourceTableOrdinalByKind.TryGetValue(tableKind, out var ordinal) ? ordinal : int.MaxValue;

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
                "CDC source inventory must contain exactly dms.DocumentCache, dms.Document, and dms.CdcHeartbeat.",
                parameterName
            );
        }

        foreach (var table in sourceInventory)
        {
            ValidateExpectedColumnOrdinals(table, parameterName);
        }

        return sourceInventory;
    }

    private static void ValidateExpectedColumnOrdinals(CdcSourceTableInventory table, string parameterName)
    {
        var expectedOrdinalOrder = Enumerable.Range(1, table.Columns.Count).ToArray();
        if (table.Columns.Select(column => column.Ordinal).SequenceEqual(expectedOrdinalOrder))
        {
            return;
        }

        throw new ArgumentException(
            "CDC expected source table columns must be supplied in table-ordinal order starting at 1.",
            parameterName
        );
    }
}

internal static class CdcDmsManagedTableInventoryContract
{
    public static IReadOnlyList<CdcDmsManagedTableInventory> Normalize(
        IReadOnlyList<CdcDmsManagedTableInventory> tables,
        string parameterName
    )
    {
        ArgumentNullException.ThrowIfNull(tables);

        if (tables.Count == 0)
        {
            throw new ArgumentException(
                "CDC DMS-managed table inventory must be supplied by the ordinary DDL metadata layer.",
                parameterName
            );
        }

        var duplicateTables = tables
            .GroupBy(table => table.TableName)
            .Where(group => group.Count() > 1)
            .Select(group => $"{group.Key.Schema.Value}.{group.Key.Name}")
            .ToArray();

        if (duplicateTables.Length > 0)
        {
            throw new ArgumentException(
                "CDC DMS-managed table inventory must not contain duplicate physical tables.",
                parameterName
            );
        }

        return tables
            .OrderBy(table => table.TableName.Schema.Value, StringComparer.Ordinal)
            .ThenBy(table => table.TableName.Name, StringComparer.Ordinal)
            .ThenBy(table => table.TableKind)
            .ToArray();
    }
}
