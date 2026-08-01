// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;
using EdFi.DataManagementService.Core.Utilities;

namespace EdFi.DataManagementService.Backend;

internal enum DocumentCacheAdministrativeStateLockMode
{
    Shared = 1,
    Exclusive = 2,
}

internal sealed record DocumentCacheAdministrativeLifecycleTransitionRequest
{
    public DocumentCacheAdministrativeLifecycleTransitionRequest(
        DocumentCacheLifecycleState expectedLifecycle,
        bool expectedCacheAheadRecoveryRequired,
        DocumentCacheLifecycleState nextLifecycle,
        bool nextCacheAheadRecoveryRequired
    )
    {
        ExpectedLifecycle = RequireDefined(expectedLifecycle, nameof(expectedLifecycle));
        ExpectedCacheAheadRecoveryRequired = expectedCacheAheadRecoveryRequired;
        NextLifecycle = RequireDefined(nextLifecycle, nameof(nextLifecycle));
        NextCacheAheadRecoveryRequired = nextCacheAheadRecoveryRequired;
    }

    public DocumentCacheLifecycleState ExpectedLifecycle { get; }

    public bool ExpectedCacheAheadRecoveryRequired { get; }

    public DocumentCacheLifecycleState NextLifecycle { get; }

    public bool NextCacheAheadRecoveryRequired { get; }

    private static DocumentCacheLifecycleState RequireDefined(
        DocumentCacheLifecycleState lifecycle,
        string parameterName
    ) =>
        Enum.IsDefined(lifecycle)
            ? lifecycle
            : throw new ArgumentOutOfRangeException(parameterName, lifecycle, "Unsupported lifecycle.");
}

internal enum DocumentCacheAdministrativeLifecycleTransitionStatus
{
    Transitioned = 1,
    NotTransitioned = 2,
}

internal sealed record DocumentCacheAdministrativeLifecycleTransitionResult
{
    private DocumentCacheAdministrativeLifecycleTransitionResult(
        DocumentCacheAdministrativeLifecycleTransitionStatus status,
        DocumentCacheLifecycleReadResult lifecycleReadResult,
        string message
    )
    {
        if (
            status == DocumentCacheAdministrativeLifecycleTransitionStatus.Transitioned
            && !lifecycleReadResult.Succeeded
        )
        {
            throw new ArgumentException("Transitioned results require the post-transition lifecycle.");
        }

        Status = status;
        LifecycleReadResult =
            lifecycleReadResult ?? throw new ArgumentNullException(nameof(lifecycleReadResult));
        Message = DocumentCacheAdministrativePrimitiveText.Sanitize(message);
    }

    public DocumentCacheAdministrativeLifecycleTransitionStatus Status { get; }

    public DocumentCacheLifecycleReadResult LifecycleReadResult { get; }

    public string Message { get; }

    public bool Mutated => Status == DocumentCacheAdministrativeLifecycleTransitionStatus.Transitioned;

    public static DocumentCacheAdministrativeLifecycleTransitionResult Transitioned(
        DocumentCacheLifecycleObservation lifecycle
    )
    {
        ArgumentNullException.ThrowIfNull(lifecycle);

        return new(
            DocumentCacheAdministrativeLifecycleTransitionStatus.Transitioned,
            DocumentCacheLifecycleReadResult.Success(lifecycle),
            "DocumentCache lifecycle transition completed."
        );
    }

    public static DocumentCacheAdministrativeLifecycleTransitionResult NotTransitioned(
        DocumentCacheLifecycleReadResult lifecycleReadResult
    )
    {
        ArgumentNullException.ThrowIfNull(lifecycleReadResult);

        return new(
            DocumentCacheAdministrativeLifecycleTransitionStatus.NotTransitioned,
            lifecycleReadResult,
            "DocumentCache lifecycle transition did not match the expected lifecycle or latch state."
        );
    }
}

internal sealed record DocumentCacheAdministrativeActivationTransitionResult
{
    public DocumentCacheAdministrativeActivationTransitionResult(
        DocumentCacheProviderPrerequisiteValidationResult activationPrerequisites,
        DocumentCacheAdministrativeLifecycleTransitionResult transition,
        string message
    )
    {
        ActivationPrerequisites =
            activationPrerequisites ?? throw new ArgumentNullException(nameof(activationPrerequisites));
        Transition = transition ?? throw new ArgumentNullException(nameof(transition));
        Message = DocumentCacheAdministrativePrimitiveText.Sanitize(message);
    }

    public DocumentCacheProviderPrerequisiteValidationResult ActivationPrerequisites { get; }

    public DocumentCacheAdministrativeLifecycleTransitionResult Transition { get; }

    public string Message { get; }

    public bool Mutated => Transition.Mutated;
}

internal interface IDocumentCacheAdministrativePrimitives
{
    RelationalProviderToken ProviderToken { get; }

    Task<DocumentCacheLifecycleReadResult> ReadLifecycleAsync(
        IRelationalWriteSession mutexSession,
        DocumentCacheAdministrativeStateLockMode lockMode = DocumentCacheAdministrativeStateLockMode.Shared,
        CancellationToken cancellationToken = default
    );

    Task LockCanonicalDocumentsForGuardedActivationAsync(
        IRelationalWriteSession mutexSession,
        CancellationToken cancellationToken = default
    );

    Task<DocumentCacheGuardedNewEmptyActivationState> ReadGuardedNewEmptyActivationStateAsync(
        IRelationalWriteSession mutexSession,
        CancellationToken cancellationToken = default
    );

    Task<DocumentCacheProviderPrerequisiteValidationResult> ValidateActivationPrerequisitesAsync(
        IRelationalWriteSession mutexSession,
        CancellationToken cancellationToken = default
    );

    Task<DocumentCacheAdministrativeLifecycleTransitionResult> TryTransitionLifecycleAsync(
        IRelationalWriteSession mutexSession,
        DocumentCacheAdministrativeLifecycleTransitionRequest request,
        CancellationToken cancellationToken = default
    );

    Task<DocumentCacheAdministrativeActivationTransitionResult> TryTransitionLifecycleAfterActivationPrerequisitesAsync(
        IRelationalWriteSession mutexSession,
        DocumentCacheAdministrativeLifecycleTransitionRequest request,
        CancellationToken cancellationToken = default
    );
}

internal sealed record DocumentCacheAdministrativePrimitiveCommands
{
    public DocumentCacheAdministrativePrimitiveCommands(
        RelationalProviderToken providerToken,
        string sharedLifecycleObservationCommandText,
        string exclusiveLifecycleObservationCommandText,
        string guardedActivationDocumentLockCommandText,
        string guardedActivationEmptyStateCommandText,
        string transitionLifecycleCommandText,
        string? activationPrerequisiteCommandText,
        DocumentCacheLifecycleReaderQuery lifecycleReaderQuery
    )
    {
        ProviderToken = providerToken ?? throw new ArgumentNullException(nameof(providerToken));
        SharedLifecycleObservationCommandText = RequireCommandText(
            sharedLifecycleObservationCommandText,
            nameof(sharedLifecycleObservationCommandText)
        );
        ExclusiveLifecycleObservationCommandText = RequireCommandText(
            exclusiveLifecycleObservationCommandText,
            nameof(exclusiveLifecycleObservationCommandText)
        );
        GuardedActivationDocumentLockCommandText = RequireCommandText(
            guardedActivationDocumentLockCommandText,
            nameof(guardedActivationDocumentLockCommandText)
        );
        GuardedActivationEmptyStateCommandText = RequireCommandText(
            guardedActivationEmptyStateCommandText,
            nameof(guardedActivationEmptyStateCommandText)
        );
        TransitionLifecycleCommandText = RequireCommandText(
            transitionLifecycleCommandText,
            nameof(transitionLifecycleCommandText)
        );
        ActivationPrerequisiteCommandText = activationPrerequisiteCommandText;
        LifecycleReaderQuery =
            lifecycleReaderQuery ?? throw new ArgumentNullException(nameof(lifecycleReaderQuery));
    }

    public RelationalProviderToken ProviderToken { get; }

    public string SharedLifecycleObservationCommandText { get; }

    public string ExclusiveLifecycleObservationCommandText { get; }

    public string GuardedActivationDocumentLockCommandText { get; }

    public string GuardedActivationEmptyStateCommandText { get; }

    public string TransitionLifecycleCommandText { get; }

    public string? ActivationPrerequisiteCommandText { get; }

    public DocumentCacheLifecycleReaderQuery LifecycleReaderQuery { get; }

    public string GetLifecycleObservationCommandText(DocumentCacheAdministrativeStateLockMode lockMode) =>
        lockMode switch
        {
            DocumentCacheAdministrativeStateLockMode.Shared => SharedLifecycleObservationCommandText,
            DocumentCacheAdministrativeStateLockMode.Exclusive => ExclusiveLifecycleObservationCommandText,
            _ => throw new ArgumentOutOfRangeException(nameof(lockMode), lockMode, "Unsupported lock mode."),
        };

    private static string RequireCommandText(string commandText, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(commandText))
        {
            throw new ArgumentException("Command text is required.", parameterName);
        }

        return commandText;
    }
}

internal static class DocumentCacheAdministrativePrimitivesSupport
{
    private const string CanonicalDocumentsEmptyColumnName = "CanonicalDocumentsEmpty";
    private const string DocumentCacheEmptyColumnName = "DocumentCacheEmpty";
    private const string DocumentProjectionWorkEmptyColumnName = "DocumentProjectionWorkEmpty";
    private const string ReadCommittedSnapshotColumnName = "ReadCommittedSnapshot";
    private const string NestedTriggersColumnName = "NestedTriggers";

    private static readonly DocumentCacheAdministrativePrimitiveCommands _pgsqlCommands = CreateCommands(
        SqlDialect.Pgsql,
        RelationalProviderToken.Postgresql
    );

    private static readonly DocumentCacheAdministrativePrimitiveCommands _mssqlCommands = CreateCommands(
        SqlDialect.Mssql,
        RelationalProviderToken.SqlServer
    );

    public static DocumentCacheAdministrativePrimitiveCommands GetCommands(SqlDialect dialect) =>
        dialect switch
        {
            SqlDialect.Pgsql => _pgsqlCommands,
            SqlDialect.Mssql => _mssqlCommands,
            _ => throw new ArgumentOutOfRangeException(nameof(dialect), dialect, "Unsupported SQL dialect."),
        };

    public static Task<DocumentCacheLifecycleReadResult> ReadLifecycleAsync(
        IRelationalWriteSession mutexSession,
        DocumentCacheAdministrativePrimitiveCommands commands,
        DocumentCacheAdministrativeStateLockMode lockMode,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(mutexSession);
        ArgumentNullException.ThrowIfNull(commands);

        return ReadLifecycleAsync(
            mutexSession.CreateCommandExecutor(),
            commands,
            lockMode,
            cancellationToken
        );
    }

    public static Task LockCanonicalDocumentsForGuardedActivationAsync(
        IRelationalWriteSession mutexSession,
        DocumentCacheAdministrativePrimitiveCommands commands,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(mutexSession);
        ArgumentNullException.ThrowIfNull(commands);

        return ExecuteNoResultAsync(
            mutexSession.CreateCommandExecutor(),
            new RelationalCommand(commands.GuardedActivationDocumentLockCommandText),
            cancellationToken
        );
    }

    public static async Task<DocumentCacheGuardedNewEmptyActivationState> ReadGuardedNewEmptyActivationStateAsync(
        IRelationalWriteSession mutexSession,
        DocumentCacheAdministrativePrimitiveCommands commands,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(mutexSession);
        ArgumentNullException.ThrowIfNull(commands);

        return await mutexSession
            .CreateCommandExecutor()
            .ExecuteReaderAsync(
                new RelationalCommand(commands.GuardedActivationEmptyStateCommandText),
                static async (reader, readerCancellationToken) =>
                {
                    if (!await reader.ReadAsync(readerCancellationToken).ConfigureAwait(false))
                    {
                        throw new InvalidOperationException(
                            "Guarded new-empty activation state observation did not return a row."
                        );
                    }

                    bool canonicalDocumentsEmpty = ReadRequiredBoolean(
                        reader,
                        CanonicalDocumentsEmptyColumnName
                    );
                    bool documentCacheEmpty = ReadRequiredBoolean(reader, DocumentCacheEmptyColumnName);
                    bool documentProjectionWorkEmpty = ReadRequiredBoolean(
                        reader,
                        DocumentProjectionWorkEmptyColumnName
                    );

                    return new DocumentCacheGuardedNewEmptyActivationState(
                        canonicalDocumentsEmpty,
                        documentCacheEmpty,
                        documentProjectionWorkEmpty,
                        canonicalDocumentsEmpty && documentCacheEmpty && documentProjectionWorkEmpty
                            ? "Guarded new-empty state observed."
                            : "Guarded new-empty activation requires empty canonical documents, cache rows, and durable work."
                    );
                },
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    public static Task<DocumentCacheProviderPrerequisiteValidationResult> ValidateActivationPrerequisitesAsync(
        IRelationalWriteSession mutexSession,
        DocumentCacheAdministrativePrimitiveCommands commands,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(mutexSession);
        ArgumentNullException.ThrowIfNull(commands);

        if (commands.ActivationPrerequisiteCommandText is null)
        {
            return Task.FromResult(
                DocumentCacheProviderPrerequisiteValidationResult.ActivationPreflight(
                    DocumentCacheSqlServerPrerequisiteDetails.NotApplicable()
                )
            );
        }

        return ValidateSqlServerActivationPrerequisitesAsync(
            mutexSession.CreateCommandExecutor(),
            commands.ActivationPrerequisiteCommandText,
            cancellationToken
        );
    }

    public static async Task<DocumentCacheAdministrativeLifecycleTransitionResult> TryTransitionLifecycleAsync(
        IRelationalWriteSession mutexSession,
        DocumentCacheAdministrativePrimitiveCommands commands,
        DocumentCacheAdministrativeLifecycleTransitionRequest request,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(mutexSession);
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(request);

        DocumentCacheLifecycleReadResult transitionReadResult = await mutexSession
            .CreateCommandExecutor()
            .ExecuteReaderAsync(
                new RelationalCommand(
                    commands.TransitionLifecycleCommandText,
                    CreateTransitionParameters(request)
                ),
                (reader, readerCancellationToken) =>
                    ReadLifecycleAsync(reader, commands.LifecycleReaderQuery, readerCancellationToken),
                cancellationToken
            )
            .ConfigureAwait(false);

        if (transitionReadResult.Succeeded)
        {
            return DocumentCacheAdministrativeLifecycleTransitionResult.Transitioned(
                transitionReadResult.Lifecycle!
            );
        }

        DocumentCacheLifecycleReadResult currentLifecycle = await ReadLifecycleAsync(
                mutexSession.CreateCommandExecutor(),
                commands,
                DocumentCacheAdministrativeStateLockMode.Exclusive,
                cancellationToken
            )
            .ConfigureAwait(false);

        return DocumentCacheAdministrativeLifecycleTransitionResult.NotTransitioned(currentLifecycle);
    }

    public static async Task<DocumentCacheAdministrativeActivationTransitionResult> TryTransitionLifecycleAfterActivationPrerequisitesAsync(
        IRelationalWriteSession mutexSession,
        DocumentCacheAdministrativePrimitiveCommands commands,
        DocumentCacheAdministrativeLifecycleTransitionRequest request,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(mutexSession);
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(request);

        DocumentCacheProviderPrerequisiteValidationResult activationPrerequisites =
            await ValidateActivationPrerequisitesAsync(mutexSession, commands, cancellationToken)
                .ConfigureAwait(false);

        if (!activationPrerequisites.IsSatisfied)
        {
            DocumentCacheLifecycleReadResult currentLifecycle = await ReadLifecycleAsync(
                    mutexSession.CreateCommandExecutor(),
                    commands,
                    DocumentCacheAdministrativeStateLockMode.Exclusive,
                    cancellationToken
                )
                .ConfigureAwait(false);

            return new DocumentCacheAdministrativeActivationTransitionResult(
                activationPrerequisites,
                DocumentCacheAdministrativeLifecycleTransitionResult.NotTransitioned(currentLifecycle),
                "Activation prerequisite validation failed before lifecycle mutation."
            );
        }

        DocumentCacheAdministrativeLifecycleTransitionResult transition = await TryTransitionLifecycleAsync(
                mutexSession,
                commands,
                request,
                cancellationToken
            )
            .ConfigureAwait(false);

        return new DocumentCacheAdministrativeActivationTransitionResult(
            activationPrerequisites,
            transition,
            "Activation prerequisites were validated immediately before lifecycle transition."
        );
    }

    private static async Task<DocumentCacheLifecycleReadResult> ReadLifecycleAsync(
        IRelationalCommandExecutor executor,
        DocumentCacheAdministrativePrimitiveCommands commands,
        DocumentCacheAdministrativeStateLockMode lockMode,
        CancellationToken cancellationToken
    )
    {
        try
        {
            return await executor
                .ExecuteReaderAsync(
                    new RelationalCommand(commands.GetLifecycleObservationCommandText(lockMode)),
                    (reader, readerCancellationToken) =>
                        ReadLifecycleAsync(reader, commands.LifecycleReaderQuery, readerCancellationToken),
                    cancellationToken
                )
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return DocumentCacheLifecycleReadResult.Failure(
                DocumentCacheLifecycleReadStatus.Unreadable,
                "dms.DocumentCacheState is unreadable."
            );
        }
    }

    private static async Task<DocumentCacheLifecycleReadResult> ReadLifecycleAsync(
        IRelationalCommandReader reader,
        DocumentCacheLifecycleReaderQuery query,
        CancellationToken cancellationToken
    )
    {
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return DocumentCacheLifecycleReadResult.Failure(
                DocumentCacheLifecycleReadStatus.Missing,
                "dms.DocumentCacheState singleton row is missing."
            );
        }

        string? lifecycleText = ReadOptionalString(reader, query.LifecycleColumnName);
        bool? cacheAheadRecoveryRequired = ReadOptionalBoolean(
            reader,
            query.CacheAheadRecoveryRequiredColumnName
        );

        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return DocumentCacheLifecycleReadResult.Failure(
                DocumentCacheLifecycleReadStatus.Invalid,
                "dms.DocumentCacheState must contain exactly one singleton row."
            );
        }

        if (
            lifecycleText is null
            || cacheAheadRecoveryRequired is null
            || !Enum.TryParse(lifecycleText, ignoreCase: false, out DocumentCacheLifecycleState lifecycle)
            || !Enum.IsDefined(lifecycle)
        )
        {
            return DocumentCacheLifecycleReadResult.Failure(
                DocumentCacheLifecycleReadStatus.Invalid,
                "dms.DocumentCacheState lifecycle row is invalid."
            );
        }

        return DocumentCacheLifecycleReadResult.Success(
            new DocumentCacheLifecycleObservation(lifecycle, cacheAheadRecoveryRequired.Value)
        );
    }

    private static async Task ExecuteNoResultAsync(
        IRelationalCommandExecutor executor,
        RelationalCommand command,
        CancellationToken cancellationToken
    )
    {
        await executor
            .ExecuteReaderAsync(
                command,
                static (reader, readerCancellationToken) =>
                {
                    _ = reader;
                    _ = readerCancellationToken;
                    return Task.FromResult(true);
                },
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    private static async Task<DocumentCacheProviderPrerequisiteValidationResult> ValidateSqlServerActivationPrerequisitesAsync(
        IRelationalCommandExecutor executor,
        string activationPrerequisiteCommandText,
        CancellationToken cancellationToken
    )
    {
        try
        {
            DocumentCacheSqlServerPrerequisiteDetails details = await executor
                .ExecuteReaderAsync(
                    new RelationalCommand(activationPrerequisiteCommandText),
                    static async (reader, readerCancellationToken) =>
                    {
                        if (!await reader.ReadAsync(readerCancellationToken).ConfigureAwait(false))
                        {
                            return UnreadableSqlServerPrerequisites();
                        }

                        return new DocumentCacheSqlServerPrerequisiteDetails(
                            ReadSqlServerPrerequisite(
                                DocumentCacheProviderPrerequisiteName.ReadCommittedSnapshot,
                                ReadOptionalInt(reader, ReadCommittedSnapshotColumnName),
                                "SQL Server READ_COMMITTED_SNAPSHOT is enabled.",
                                "SQL Server READ_COMMITTED_SNAPSHOT is disabled.",
                                "SQL Server READ_COMMITTED_SNAPSHOT is unreadable."
                            ),
                            ReadSqlServerPrerequisite(
                                DocumentCacheProviderPrerequisiteName.NestedTriggers,
                                ReadOptionalInt(reader, NestedTriggersColumnName),
                                "SQL Server nested triggers are enabled.",
                                "SQL Server nested triggers are disabled.",
                                "SQL Server nested triggers are unreadable."
                            )
                        );
                    },
                    cancellationToken
                )
                .ConfigureAwait(false);

            return DocumentCacheProviderPrerequisiteValidationResult.ActivationPreflight(details);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return DocumentCacheProviderPrerequisiteValidationResult.ActivationPreflight(
                UnreadableSqlServerPrerequisites()
            );
        }
    }

    private static DocumentCacheProviderPrerequisiteResult ReadSqlServerPrerequisite(
        DocumentCacheProviderPrerequisiteName name,
        int? value,
        string satisfiedMessage,
        string disabledMessage,
        string unreadableMessage
    ) =>
        value switch
        {
            1 => new DocumentCacheProviderPrerequisiteResult(
                name,
                DocumentCacheProviderPrerequisiteStatus.Satisfied,
                satisfiedMessage
            ),
            0 => new DocumentCacheProviderPrerequisiteResult(
                name,
                DocumentCacheProviderPrerequisiteStatus.Disabled,
                disabledMessage
            ),
            _ => Unreadable(name, unreadableMessage),
        };

    private static DocumentCacheSqlServerPrerequisiteDetails UnreadableSqlServerPrerequisites() =>
        new(
            Unreadable(
                DocumentCacheProviderPrerequisiteName.ReadCommittedSnapshot,
                "SQL Server READ_COMMITTED_SNAPSHOT is unreadable."
            ),
            Unreadable(
                DocumentCacheProviderPrerequisiteName.NestedTriggers,
                "SQL Server nested triggers are unreadable."
            )
        );

    private static DocumentCacheProviderPrerequisiteResult Unreadable(
        DocumentCacheProviderPrerequisiteName name,
        string message
    ) => new(name, DocumentCacheProviderPrerequisiteStatus.Unreadable, message);

    private static IReadOnlyList<RelationalParameter> CreateTransitionParameters(
        DocumentCacheAdministrativeLifecycleTransitionRequest request
    ) =>
        [
            new("@expectedLifecycle", request.ExpectedLifecycle.ToString()),
            new("@expectedCacheAheadRecoveryRequired", request.ExpectedCacheAheadRecoveryRequired),
            new("@nextLifecycle", request.NextLifecycle.ToString()),
            new("@nextCacheAheadRecoveryRequired", request.NextCacheAheadRecoveryRequired),
        ];

    private static string? ReadOptionalString(IRelationalCommandReader reader, string columnName)
    {
        int ordinal = reader.GetOrdinal(columnName);

        return reader.IsDBNull(ordinal) ? null : reader.GetFieldValue<string>(ordinal);
    }

    private static bool ReadRequiredBoolean(IRelationalCommandReader reader, string columnName) =>
        ReadOptionalBoolean(reader, columnName)
        ?? throw new InvalidOperationException($"Required boolean column '{columnName}' was null.");

    private static bool? ReadOptionalBoolean(IRelationalCommandReader reader, string columnName)
    {
        int ordinal = reader.GetOrdinal(columnName);
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        object value = reader.GetFieldValue<object>(ordinal);
        return value switch
        {
            bool booleanValue => booleanValue,
            byte byteValue => byteValue != 0,
            short shortValue => shortValue != 0,
            int intValue => intValue != 0,
            long longValue => longValue != 0,
            _ => Convert.ToBoolean(value, CultureInfo.InvariantCulture),
        };
    }

    private static int? ReadOptionalInt(IRelationalCommandReader reader, string columnName)
    {
        int ordinal = reader.GetOrdinal(columnName);
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        object value = reader.GetFieldValue<object>(ordinal);
        return Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }

    private static DocumentCacheAdministrativePrimitiveCommands CreateCommands(
        SqlDialect dialect,
        RelationalProviderToken providerToken
    )
    {
        string lifecycleColumn = DocumentCacheInventoryDefinition
            .DocumentCacheStateColumns
            .ProjectionLifecycleState
            .Value;
        string cacheAheadRecoveryRequiredColumn = DocumentCacheInventoryDefinition
            .DocumentCacheStateColumns
            .CacheAheadRecoveryRequired
            .Value;

        return new DocumentCacheAdministrativePrimitiveCommands(
            providerToken,
            RenderLifecycleObservationCommandText(dialect, exclusive: false),
            RenderLifecycleObservationCommandText(dialect, exclusive: true),
            RenderGuardedActivationDocumentLockCommandText(dialect),
            RenderGuardedActivationEmptyStateCommandText(dialect),
            RenderTransitionLifecycleCommandText(dialect),
            dialect == SqlDialect.Mssql ? RenderSqlServerActivationPrerequisiteCommandText() : null,
            new DocumentCacheLifecycleReaderQuery(
                ExistsCommandText: string.Empty,
                ReadLifecycleCommandText: string.Empty,
                lifecycleColumn,
                cacheAheadRecoveryRequiredColumn,
                providerToken
            )
        );
    }

    private static string RenderLifecycleObservationCommandText(SqlDialect dialect, bool exclusive)
    {
        string qualifiedTable = Quote(DocumentCacheInventoryDefinition.DocumentCacheState, dialect);
        string stateIdColumn = Quote(
            DocumentCacheInventoryDefinition.DocumentCacheStateColumns.StateId,
            dialect
        );
        string lifecycleColumn = Quote(
            DocumentCacheInventoryDefinition.DocumentCacheStateColumns.ProjectionLifecycleState,
            dialect
        );
        string cacheAheadRecoveryRequiredColumn = Quote(
            DocumentCacheInventoryDefinition.DocumentCacheStateColumns.CacheAheadRecoveryRequired,
            dialect
        );

        return dialect switch
        {
            SqlDialect.Pgsql => $"""
                SELECT {lifecycleColumn}, {cacheAheadRecoveryRequiredColumn}
                FROM {qualifiedTable}
                WHERE {stateIdColumn} = 1
                {(exclusive ? "FOR UPDATE" : "FOR SHARE")};
                """,
            SqlDialect.Mssql => $"""
                SELECT TOP (2) {lifecycleColumn}, {cacheAheadRecoveryRequiredColumn}
                FROM {qualifiedTable} WITH ({(exclusive ? "XLOCK, " : string.Empty)}HOLDLOCK)
                WHERE {stateIdColumn} = 1;
                """,
            _ => throw new ArgumentOutOfRangeException(nameof(dialect), dialect, "Unsupported SQL dialect."),
        };
    }

    private static string RenderGuardedActivationDocumentLockCommandText(SqlDialect dialect)
    {
        string documentTable = Quote(DocumentCacheInventoryDefinition.Document, dialect);
        string documentIdColumn = Quote(DocumentCacheInventoryDefinition.DocumentColumns.DocumentId, dialect);

        return dialect switch
        {
            SqlDialect.Pgsql => $"LOCK TABLE {documentTable} IN SHARE MODE;",
            SqlDialect.Mssql => $"""
                SELECT TOP (1) {documentIdColumn}
                FROM {documentTable} WITH (TABLOCK, HOLDLOCK);
                """,
            _ => throw new ArgumentOutOfRangeException(nameof(dialect), dialect, "Unsupported SQL dialect."),
        };
    }

    private static string RenderGuardedActivationEmptyStateCommandText(SqlDialect dialect)
    {
        string documentTable = Quote(DocumentCacheInventoryDefinition.Document, dialect);
        string cacheTable = Quote(DocumentCacheInventoryDefinition.DocumentCache, dialect);
        string workTable = Quote(DocumentCacheInventoryDefinition.DocumentProjectionWork, dialect);

        return dialect switch
        {
            SqlDialect.Pgsql => $"""
                SELECT
                    NOT EXISTS (SELECT 1 FROM {documentTable} LIMIT 1) AS "{CanonicalDocumentsEmptyColumnName}",
                    NOT EXISTS (SELECT 1 FROM {cacheTable} LIMIT 1) AS "{DocumentCacheEmptyColumnName}",
                    NOT EXISTS (SELECT 1 FROM {workTable} LIMIT 1) AS "{DocumentProjectionWorkEmptyColumnName}";
                """,
            SqlDialect.Mssql => $"""
                SELECT
                    CAST(CASE WHEN NOT EXISTS (SELECT TOP (1) 1 FROM {documentTable}) THEN 1 ELSE 0 END AS bit) AS [{CanonicalDocumentsEmptyColumnName}],
                    CAST(CASE WHEN NOT EXISTS (SELECT TOP (1) 1 FROM {cacheTable}) THEN 1 ELSE 0 END AS bit) AS [{DocumentCacheEmptyColumnName}],
                    CAST(CASE WHEN NOT EXISTS (SELECT TOP (1) 1 FROM {workTable}) THEN 1 ELSE 0 END AS bit) AS [{DocumentProjectionWorkEmptyColumnName}];
                """,
            _ => throw new ArgumentOutOfRangeException(nameof(dialect), dialect, "Unsupported SQL dialect."),
        };
    }

    private static string RenderTransitionLifecycleCommandText(SqlDialect dialect)
    {
        string stateTable = Quote(DocumentCacheInventoryDefinition.DocumentCacheState, dialect);
        string stateIdColumn = Quote(
            DocumentCacheInventoryDefinition.DocumentCacheStateColumns.StateId,
            dialect
        );
        string lifecycleColumn = Quote(
            DocumentCacheInventoryDefinition.DocumentCacheStateColumns.ProjectionLifecycleState,
            dialect
        );
        string cacheAheadRecoveryRequiredColumn = Quote(
            DocumentCacheInventoryDefinition.DocumentCacheStateColumns.CacheAheadRecoveryRequired,
            dialect
        );

        return dialect switch
        {
            SqlDialect.Pgsql => $"""
                UPDATE {stateTable}
                SET {lifecycleColumn} = @nextLifecycle,
                    {cacheAheadRecoveryRequiredColumn} = @nextCacheAheadRecoveryRequired
                WHERE {stateIdColumn} = 1
                  AND {lifecycleColumn} = @expectedLifecycle
                  AND {cacheAheadRecoveryRequiredColumn} = @expectedCacheAheadRecoveryRequired
                RETURNING {lifecycleColumn}, {cacheAheadRecoveryRequiredColumn};
                """,
            SqlDialect.Mssql => $"""
                DECLARE @transitioned table (
                    {lifecycleColumn} varchar(16) NOT NULL,
                    {cacheAheadRecoveryRequiredColumn} bit NOT NULL
                );

                UPDATE {stateTable} WITH (XLOCK, HOLDLOCK)
                SET {lifecycleColumn} = @nextLifecycle,
                    {cacheAheadRecoveryRequiredColumn} = @nextCacheAheadRecoveryRequired
                OUTPUT inserted.{lifecycleColumn}, inserted.{cacheAheadRecoveryRequiredColumn}
                INTO @transitioned
                WHERE {stateIdColumn} = 1
                  AND {lifecycleColumn} = @expectedLifecycle
                  AND {cacheAheadRecoveryRequiredColumn} = @expectedCacheAheadRecoveryRequired;

                SELECT TOP (2) {lifecycleColumn}, {cacheAheadRecoveryRequiredColumn}
                FROM @transitioned;
                """,
            _ => throw new ArgumentOutOfRangeException(nameof(dialect), dialect, "Unsupported SQL dialect."),
        };
    }

    private static string RenderSqlServerActivationPrerequisiteCommandText() =>
        $"""
            SELECT
                (
                    SELECT CONVERT(int, [is_read_committed_snapshot_on])
                    FROM [sys].[databases]
                    WHERE [name] = DB_NAME()
                ) AS [{ReadCommittedSnapshotColumnName}],
                (
                    SELECT CONVERT(int, [value_in_use])
                    FROM [sys].[configurations]
                    WHERE [name] = N'nested triggers'
                ) AS [{NestedTriggersColumnName}];
            """;

    private static string Quote(DbTableName tableName, SqlDialect dialect) =>
        SqlIdentifierQuoter.QuoteTableName(dialect, tableName);

    private static string Quote(DbColumnName columnName, SqlDialect dialect) =>
        SqlIdentifierQuoter.QuoteIdentifier(dialect, columnName);
}

file static class DocumentCacheAdministrativePrimitiveText
{
    private const int MaximumLength = 512;

    public static string Sanitize(string? message)
    {
        string sanitized = LoggingSanitizer.SanitizeForLogging(message);
        return sanitized.Length <= MaximumLength ? sanitized : sanitized[..MaximumLength];
    }
}
