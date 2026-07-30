// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.Text;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Backend;

internal sealed record DocumentCacheInventoryValidatorQuery(
    SqlDialect Dialect,
    RelationalProviderToken ProviderToken
);

internal static class DocumentCacheInventoryValidatorSupport
{
    private const string DmsSchema = "dms";
    private const string PgsqlDocumentEnqueueOwnerRole = "edfi_dms_enqueue_owner";
    private const string PgsqlDocumentEnqueueSearchPathConfiguration = "search_path=pg_catalog";

    private static readonly DocumentCacheInventoryValidatorQuery _pgsqlQuery = new(
        SqlDialect.Pgsql,
        RelationalProviderToken.Postgresql
    );

    private static readonly DocumentCacheInventoryValidatorQuery _mssqlQuery = new(
        SqlDialect.Mssql,
        RelationalProviderToken.SqlServer
    );

    private const string PgsqlDocumentCacheJsonObjectCheckExpression =
        "JSONB_TYPEOFDOCUMENTJSON='OBJECT'TEXT";
    private const string MssqlDocumentCacheJsonObjectCheckExpression =
        "ISJSONDOCUMENTJSON=1ANDLEFTLTRIMDOCUMENTJSON1='{'";
    private const string PgsqlDocumentCacheStateLifecycleCheckExpression =
        "PROJECTIONLIFECYCLESTATETEXT=ANYARRAY'DISABLED'CHARACTERVARYING'RESETTING'CHARACTERVARYING'REBUILDING'CHARACTERVARYING'TRACKING'CHARACTERVARYINGTEXT";
    private const string MssqlDocumentCacheStateLifecycleCheckExpression =
        "PROJECTIONLIFECYCLESTATE='DISABLED'ANDDATALENGTHPROJECTIONLIFECYCLESTATE=8ORPROJECTIONLIFECYCLESTATE='RESETTING'ANDDATALENGTHPROJECTIONLIFECYCLESTATE=9ORPROJECTIONLIFECYCLESTATE='REBUILDING'ANDDATALENGTHPROJECTIONLIFECYCLESTATE=10ORPROJECTIONLIFECYCLESTATE='TRACKING'ANDDATALENGTHPROJECTIONLIFECYCLESTATE=8";

    public static DocumentCacheInventoryValidatorQuery GetQuery(SqlDialect dialect) =>
        dialect switch
        {
            SqlDialect.Pgsql => _pgsqlQuery,
            SqlDialect.Mssql => _mssqlQuery,
            _ => throw new ArgumentOutOfRangeException(nameof(dialect), dialect, "Unsupported SQL dialect."),
        };

    public static async Task<DocumentCacheProviderInventoryValidationResult> ValidateInventoryAsync(
        Func<DbConnection> connectionFactory,
        DocumentCacheInventoryValidatorQuery query,
        ILogger logger,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(logger);

        try
        {
            await using var connection = connectionFactory();
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            var inventoryIssues = new List<InventoryIssue>();
            var enqueueIssues = new List<EnqueueIssue>();

            var existingTables = new HashSet<string>(StringComparer.Ordinal);
            var columnsByTable = new Dictionary<string, IReadOnlyList<ColumnSnapshot>>(
                StringComparer.Ordinal
            );

            foreach (TableSpec tableSpec in BuildTableSpecs(query.Dialect))
            {
                string tableKey = ToTableKey(tableSpec.Table);

                if (
                    !await TableExistsAsync(connection, query.Dialect, tableSpec.Table, cancellationToken)
                        .ConfigureAwait(false)
                )
                {
                    inventoryIssues.Add(
                        new InventoryIssue(
                            DocumentCacheInventoryStatus.Missing,
                            $"{Display(tableSpec.Table)} table is missing."
                        )
                    );
                    continue;
                }

                existingTables.Add(tableKey);

                IReadOnlyList<ColumnSnapshot> columns = await ReadColumnsAsync(
                        connection,
                        query.Dialect,
                        tableSpec.Table,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
                columnsByTable[tableKey] = columns;
                ValidateColumns(tableSpec, columns, inventoryIssues);
            }

            await ValidateConstraintsAsync(connection, query.Dialect, inventoryIssues, cancellationToken)
                .ConfigureAwait(false);
            await ValidateIndexesAsync(connection, query.Dialect, inventoryIssues, cancellationToken)
                .ConfigureAwait(false);
            await ValidateDocumentCacheUuidTriggerAsync(
                    connection,
                    query.Dialect,
                    inventoryIssues,
                    cancellationToken
                )
                .ConfigureAwait(false);
            await ValidateSingletonRowsAsync(
                    connection,
                    query.Dialect,
                    existingTables,
                    columnsByTable,
                    inventoryIssues,
                    cancellationToken
                )
                .ConfigureAwait(false);
            await ValidateEnqueueArtifactsAsync(connection, query.Dialect, enqueueIssues, cancellationToken)
                .ConfigureAwait(false);

            return new DocumentCacheProviderInventoryValidationResult(
                ToInventoryResult(inventoryIssues),
                ToEnqueueResult(enqueueIssues)
            );
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            LogInventoryValidationFailure(logger, exception);
            return new DocumentCacheProviderInventoryValidationResult(
                new DocumentCacheInventoryValidationResult(
                    DocumentCacheInventoryStatus.Unreadable,
                    "DocumentCache inventory is unreadable."
                ),
                new DocumentCacheEnqueueTriggerValidationResult(
                    DocumentCacheEnqueueTriggerStatus.Unreadable,
                    "DocumentCache enqueue inventory is unreadable."
                )
            );
        }
    }

    private static void LogInventoryValidationFailure(ILogger logger, Exception exception)
    {
        logger.LogDebug(
            "DocumentCache inventory validation failed while reading provider metadata for category {FailureCategory}; exception type {ExceptionType}",
            DocumentCacheTargetDiagnosticCategory.InventoryFailure,
            exception.GetType().Name
        );
    }

    private static IReadOnlyList<TableSpec> BuildTableSpecs(SqlDialect dialect)
    {
        return
        [
            new TableSpec(
                DataStoreIdentityTableDefinition.Table,
                [
                    Smallint(DataStoreIdentityTableDefinition.DataStoreIdentitySingletonId.Value, dialect),
                    Uuid(DataStoreIdentityTableDefinition.SourceIdentity.Value, dialect),
                ],
                ExactColumnSet: true
            ),
            new TableSpec(
                DocumentCacheInventoryDefinition.Document,
                [
                    Bigint(DocumentCacheInventoryDefinition.DocumentColumns.DocumentId.Value, dialect),
                    Uuid(DocumentCacheInventoryDefinition.DocumentColumns.DocumentUuid.Value, dialect),
                    Bigint(DocumentCacheInventoryDefinition.DocumentColumns.ContentVersion.Value, dialect),
                ],
                ExactColumnSet: false
            ),
            new TableSpec(
                DocumentCacheInventoryDefinition.DocumentCache,
                [
                    Bigint(DocumentCacheInventoryDefinition.DocumentCacheColumns.DocumentId.Value, dialect),
                    Uuid(DocumentCacheInventoryDefinition.DocumentCacheColumns.DocumentUuid.Value, dialect),
                    String(
                        DocumentCacheInventoryDefinition.DocumentCacheColumns.ProjectName.Value,
                        dialect,
                        256
                    ),
                    String(
                        DocumentCacheInventoryDefinition.DocumentCacheColumns.ResourceName.Value,
                        dialect,
                        256
                    ),
                    String(
                        DocumentCacheInventoryDefinition.DocumentCacheColumns.ResourceVersion.Value,
                        dialect,
                        32
                    ),
                    Bigint(
                        DocumentCacheInventoryDefinition.DocumentCacheColumns.ContentVersion.Value,
                        dialect
                    ),
                    String(
                        DocumentCacheInventoryDefinition.DocumentCacheColumns.StreamEtag.Value,
                        dialect,
                        64,
                        forceAsciiForMssql: true
                    ),
                    DateTime(
                        DocumentCacheInventoryDefinition.DocumentCacheColumns.LastModifiedAt.Value,
                        dialect
                    ),
                    Json(DocumentCacheInventoryDefinition.DocumentCacheColumns.DocumentJson.Value, dialect),
                    DateTime(DocumentCacheInventoryDefinition.DocumentCacheColumns.ComputedAt.Value, dialect),
                ],
                ExactColumnSet: true
            ),
            new TableSpec(
                DocumentCacheInventoryDefinition.DocumentCacheState,
                [
                    Smallint(
                        DocumentCacheInventoryDefinition.DocumentCacheStateColumns.StateId.Value,
                        dialect
                    ),
                    Lifecycle(
                        DocumentCacheInventoryDefinition
                            .DocumentCacheStateColumns
                            .ProjectionLifecycleState
                            .Value,
                        dialect
                    ),
                    Boolean(
                        DocumentCacheInventoryDefinition
                            .DocumentCacheStateColumns
                            .CacheAheadRecoveryRequired
                            .Value,
                        dialect
                    ),
                ],
                ExactColumnSet: true
            ),
            new TableSpec(
                DocumentCacheInventoryDefinition.DocumentProjectionWork,
                [
                    Bigint(
                        DocumentCacheInventoryDefinition.DocumentProjectionWorkColumns.DocumentId.Value,
                        dialect
                    ),
                    Bigint(
                        DocumentCacheInventoryDefinition
                            .DocumentProjectionWorkColumns
                            .RequiredContentVersion
                            .Value,
                        dialect
                    ),
                    DateTime(
                        DocumentCacheInventoryDefinition.DocumentProjectionWorkColumns.FirstEnqueuedAt.Value,
                        dialect
                    ),
                    DateTime(
                        DocumentCacheInventoryDefinition.DocumentProjectionWorkColumns.LastEnqueuedAt.Value,
                        dialect
                    ),
                ],
                ExactColumnSet: true
            ),
        ];
    }

    private static RequiredColumn Bigint(string name, SqlDialect dialect) =>
        new(name, [dialect == SqlDialect.Pgsql ? "int8" : "bigint"], IsNullable: false);

    private static RequiredColumn Smallint(string name, SqlDialect dialect) =>
        new(name, [dialect == SqlDialect.Pgsql ? "int2" : "smallint"], IsNullable: false);

    private static RequiredColumn Uuid(string name, SqlDialect dialect) =>
        new(name, [dialect == SqlDialect.Pgsql ? "uuid" : "uniqueidentifier"], IsNullable: false);

    private static RequiredColumn Boolean(string name, SqlDialect dialect) =>
        new(name, [dialect == SqlDialect.Pgsql ? "bool" : "bit"], IsNullable: false);

    private static RequiredColumn DateTime(string name, SqlDialect dialect) =>
        new(name, [dialect == SqlDialect.Pgsql ? "timestamptz" : "datetime2"], IsNullable: false);

    private static RequiredColumn String(
        string name,
        SqlDialect dialect,
        int maxLength,
        bool forceAsciiForMssql = false
    ) =>
        new(
            name,
            [dialect == SqlDialect.Mssql && forceAsciiForMssql ? "varchar" : MssqlAwareVarchar(dialect)],
            IsNullable: false,
            MaxLength: maxLength
        );

    private static RequiredColumn Lifecycle(string name, SqlDialect dialect) =>
        new(
            name,
            ["varchar"],
            IsNullable: false,
            MaxLength: 16,
            CollationName: dialect == SqlDialect.Mssql ? "Latin1_General_100_BIN2" : null
        );

    private static RequiredColumn Json(string name, SqlDialect dialect) =>
        new(
            name,
            [dialect == SqlDialect.Pgsql ? "jsonb" : "nvarchar"],
            IsNullable: false,
            MaxLength: dialect == SqlDialect.Mssql ? -1 : null
        );

    private static string MssqlAwareVarchar(SqlDialect dialect) =>
        dialect == SqlDialect.Pgsql ? "varchar" : "nvarchar";

    private static async Task ValidateConstraintsAsync(
        DbConnection connection,
        SqlDialect dialect,
        List<InventoryIssue> inventoryIssues,
        CancellationToken cancellationToken
    )
    {
        await RequirePrimaryKeyAsync(
                connection,
                dialect,
                DataStoreIdentityTableDefinition.Table,
                DocumentCacheInventoryDefinition.DataStoreIdentityConstraints.PrimaryKey,
                [DataStoreIdentityTableDefinition.DataStoreIdentitySingletonId.Value],
                inventoryIssues,
                cancellationToken
            )
            .ConfigureAwait(false);
        await RequireCheckConstraintAsync(
                connection,
                dialect,
                DataStoreIdentityTableDefinition.Table,
                DocumentCacheInventoryDefinition.DataStoreIdentityConstraints.Singleton,
                definition =>
                    HasExpectedSingletonCheck(
                        definition,
                        DataStoreIdentityTableDefinition.DataStoreIdentitySingletonId.Value
                    ),
                inventoryIssues,
                cancellationToken
            )
            .ConfigureAwait(false);

        await RequirePrimaryKeyAsync(
                connection,
                dialect,
                DocumentCacheInventoryDefinition.DocumentCache,
                DocumentCacheInventoryDefinition.DocumentCacheConstraints.PrimaryKey,
                [DocumentCacheInventoryDefinition.DocumentCacheColumns.DocumentId.Value],
                inventoryIssues,
                cancellationToken
            )
            .ConfigureAwait(false);
        await RequireCheckConstraintAsync(
                connection,
                dialect,
                DocumentCacheInventoryDefinition.DocumentCache,
                dialect == SqlDialect.Pgsql
                    ? DocumentCacheInventoryDefinition.DocumentCacheConstraints.PgsqlJsonObject
                    : DocumentCacheInventoryDefinition.DocumentCacheConstraints.MssqlJsonObject,
                definition => HasExpectedDocumentCacheJsonObjectCheck(dialect, definition),
                inventoryIssues,
                cancellationToken
            )
            .ConfigureAwait(false);
        await RequireForeignKeyAsync(
                connection,
                dialect,
                DocumentCacheInventoryDefinition.DocumentCache,
                DocumentCacheInventoryDefinition.DocumentCacheConstraints.ForeignKeyToDocument,
                [DocumentCacheInventoryDefinition.DocumentCacheColumns.DocumentId.Value],
                DocumentCacheInventoryDefinition.Document,
                [DocumentCacheInventoryDefinition.DocumentColumns.DocumentId.Value],
                deleteAction: "CASCADE",
                updateAction: "NO_ACTION",
                inventoryIssues,
                cancellationToken
            )
            .ConfigureAwait(false);

        await RequirePrimaryKeyAsync(
                connection,
                dialect,
                DocumentCacheInventoryDefinition.DocumentCacheState,
                DocumentCacheInventoryDefinition.DocumentCacheStateConstraints.PrimaryKey,
                [DocumentCacheInventoryDefinition.DocumentCacheStateColumns.StateId.Value],
                inventoryIssues,
                cancellationToken
            )
            .ConfigureAwait(false);
        await RequireCheckConstraintAsync(
                connection,
                dialect,
                DocumentCacheInventoryDefinition.DocumentCacheState,
                DocumentCacheInventoryDefinition.DocumentCacheStateConstraints.Singleton,
                definition =>
                    HasExpectedSingletonCheck(
                        definition,
                        DocumentCacheInventoryDefinition.DocumentCacheStateColumns.StateId.Value
                    ),
                inventoryIssues,
                cancellationToken
            )
            .ConfigureAwait(false);
        await RequireCheckConstraintAsync(
                connection,
                dialect,
                DocumentCacheInventoryDefinition.DocumentCacheState,
                DocumentCacheInventoryDefinition.DocumentCacheStateConstraints.Lifecycle,
                definition => HasExpectedDocumentCacheStateLifecycleCheck(dialect, definition),
                inventoryIssues,
                cancellationToken
            )
            .ConfigureAwait(false);

        await RequirePrimaryKeyAsync(
                connection,
                dialect,
                DocumentCacheInventoryDefinition.DocumentProjectionWork,
                DocumentCacheInventoryDefinition.DocumentProjectionWorkConstraints.PrimaryKey,
                [DocumentCacheInventoryDefinition.DocumentProjectionWorkColumns.DocumentId.Value],
                inventoryIssues,
                cancellationToken
            )
            .ConfigureAwait(false);
        await RequireForeignKeyAsync(
                connection,
                dialect,
                DocumentCacheInventoryDefinition.DocumentProjectionWork,
                DocumentCacheInventoryDefinition.DocumentProjectionWorkConstraints.ForeignKeyToDocument,
                [DocumentCacheInventoryDefinition.DocumentProjectionWorkColumns.DocumentId.Value],
                DocumentCacheInventoryDefinition.Document,
                [DocumentCacheInventoryDefinition.DocumentColumns.DocumentId.Value],
                deleteAction: "CASCADE",
                updateAction: "NO_ACTION",
                inventoryIssues,
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    private static async Task ValidateIndexesAsync(
        DbConnection connection,
        SqlDialect dialect,
        List<InventoryIssue> inventoryIssues,
        CancellationToken cancellationToken
    )
    {
        IndexSnapshot? index = await ReadIndexAsync(
                connection,
                dialect,
                DocumentCacheInventoryDefinition.DocumentProjectionWork,
                DocumentCacheInventoryDefinition.DocumentProjectionWorkIndexes.FirstEnqueuedAtDocumentId,
                cancellationToken
            )
            .ConfigureAwait(false);

        if (index is null)
        {
            inventoryIssues.Add(
                new InventoryIssue(
                    DocumentCacheInventoryStatus.Missing,
                    $"{DocumentCacheInventoryDefinition.DocumentProjectionWorkIndexes.FirstEnqueuedAtDocumentId} index is missing."
                )
            );
            return;
        }

        if (!index.IsUsable)
        {
            inventoryIssues.Add(
                new InventoryIssue(
                    DocumentCacheInventoryStatus.Invalid,
                    $"{DocumentCacheInventoryDefinition.DocumentProjectionWorkIndexes.FirstEnqueuedAtDocumentId} index is not usable."
                )
            );
        }

        if (index.IsFilteredOrPartial)
        {
            inventoryIssues.Add(
                new InventoryIssue(
                    DocumentCacheInventoryStatus.Invalid,
                    $"{DocumentCacheInventoryDefinition.DocumentProjectionWorkIndexes.FirstEnqueuedAtDocumentId} index is filtered or partial."
                )
            );
        }

        ValidateColumnOrder(
            index.Columns,
            [
                DocumentCacheInventoryDefinition.DocumentProjectionWorkColumns.FirstEnqueuedAt.Value,
                DocumentCacheInventoryDefinition.DocumentProjectionWorkColumns.DocumentId.Value,
            ],
            DocumentCacheInventoryDefinition.DocumentProjectionWorkIndexes.FirstEnqueuedAtDocumentId,
            inventoryIssues
        );
    }

    private static async Task ValidateDocumentCacheUuidTriggerAsync(
        DbConnection connection,
        SqlDialect dialect,
        List<InventoryIssue> inventoryIssues,
        CancellationToken cancellationToken
    )
    {
        TriggerSnapshot? trigger = await ReadTriggerAsync(
                connection,
                dialect,
                DocumentCacheInventoryDefinition.DocumentCache,
                DocumentCacheInventoryDefinition.DocumentCacheTriggers.ValidateDocumentUuid,
                cancellationToken
            )
            .ConfigureAwait(false);

        if (trigger is null)
        {
            inventoryIssues.Add(
                new InventoryIssue(
                    DocumentCacheInventoryStatus.Missing,
                    $"{DocumentCacheInventoryDefinition.DocumentCacheTriggers.ValidateDocumentUuid} trigger is missing."
                )
            );
            return;
        }

        if (!trigger.IsEnabled)
        {
            inventoryIssues.Add(
                new InventoryIssue(
                    DocumentCacheInventoryStatus.Invalid,
                    $"{DocumentCacheInventoryDefinition.DocumentCacheTriggers.ValidateDocumentUuid} trigger is not enabled for ordinary sessions."
                )
            );
        }

        if (dialect == SqlDialect.Pgsql && !HasExpectedPgsqlDocumentCacheUuidTriggerShape(trigger))
        {
            inventoryIssues.Add(
                new InventoryIssue(
                    DocumentCacheInventoryStatus.Invalid,
                    $"{DocumentCacheInventoryDefinition.DocumentCacheTriggers.ValidateDocumentUuid} trigger shape is invalid."
                )
            );
        }

        if (
            dialect == SqlDialect.Pgsql
            && (
                trigger.FunctionSchema != DmsSchema
                || trigger.FunctionName
                    != DocumentCacheInventoryDefinition
                        .DocumentCacheTriggers
                        .PgsqlValidateDocumentUuidFunction
            )
        )
        {
            inventoryIssues.Add(
                new InventoryIssue(
                    DocumentCacheInventoryStatus.Invalid,
                    $"{DocumentCacheInventoryDefinition.DocumentCacheTriggers.ValidateDocumentUuid} trigger uses an unexpected function."
                )
            );
        }

        if (
            dialect == SqlDialect.Pgsql
            && !HasExpectedPgsqlDocumentCacheUuidValidationFunctionDefinition(trigger.Definition)
        )
        {
            inventoryIssues.Add(
                new InventoryIssue(
                    DocumentCacheInventoryStatus.Invalid,
                    $"{DocumentCacheInventoryDefinition.DocumentCacheTriggers.ValidateDocumentUuid} trigger function has unexpected semantics."
                )
            );
        }

        if (dialect == SqlDialect.Mssql && !HasExpectedMssqlAfterInsertUpdateTriggerShape(trigger))
        {
            inventoryIssues.Add(
                new InventoryIssue(
                    DocumentCacheInventoryStatus.Invalid,
                    $"{DocumentCacheInventoryDefinition.DocumentCacheTriggers.ValidateDocumentUuid} trigger shape is invalid."
                )
            );
        }

        if (
            dialect == SqlDialect.Mssql
            && !HasExpectedMssqlDocumentCacheUuidValidationTriggerDefinition(trigger.Definition)
        )
        {
            inventoryIssues.Add(
                new InventoryIssue(
                    DocumentCacheInventoryStatus.Invalid,
                    $"{DocumentCacheInventoryDefinition.DocumentCacheTriggers.ValidateDocumentUuid} trigger has unexpected semantics."
                )
            );
        }
    }

    private static async Task ValidateSingletonRowsAsync(
        DbConnection connection,
        SqlDialect dialect,
        HashSet<string> existingTables,
        Dictionary<string, IReadOnlyList<ColumnSnapshot>> columnsByTable,
        List<InventoryIssue> inventoryIssues,
        CancellationToken cancellationToken
    )
    {
        await RequireSingletonRowAsync(
                connection,
                dialect,
                DataStoreIdentityTableDefinition.Table,
                DataStoreIdentityTableDefinition.DataStoreIdentitySingletonId.Value,
                existingTables,
                columnsByTable,
                inventoryIssues,
                cancellationToken
            )
            .ConfigureAwait(false);
        await RequireSingletonRowAsync(
                connection,
                dialect,
                DocumentCacheInventoryDefinition.DocumentCacheState,
                DocumentCacheInventoryDefinition.DocumentCacheStateColumns.StateId.Value,
                existingTables,
                columnsByTable,
                inventoryIssues,
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    private static async Task ValidateEnqueueArtifactsAsync(
        DbConnection connection,
        SqlDialect dialect,
        List<EnqueueIssue> enqueueIssues,
        CancellationToken cancellationToken
    )
    {
        if (dialect == SqlDialect.Pgsql)
        {
            await RequireFunctionAsync(
                    connection,
                    dialect,
                    DocumentCacheInventoryDefinition.DocumentEnqueueArtifacts.PgsqlInsertFunction,
                    enqueueIssues,
                    cancellationToken
                )
                .ConfigureAwait(false);
            await RequireFunctionAsync(
                    connection,
                    dialect,
                    DocumentCacheInventoryDefinition.DocumentEnqueueArtifacts.PgsqlUpdateFunction,
                    enqueueIssues,
                    cancellationToken
                )
                .ConfigureAwait(false);

            await RequireEnqueueTriggerAsync(
                    connection,
                    dialect,
                    DocumentCacheInventoryDefinition.DocumentEnqueueArtifacts.PgsqlInsertTrigger,
                    DocumentCacheInventoryDefinition.DocumentEnqueueArtifacts.PgsqlInsertFunction,
                    enqueueIssues,
                    cancellationToken
                )
                .ConfigureAwait(false);
            await RequireEnqueueTriggerAsync(
                    connection,
                    dialect,
                    DocumentCacheInventoryDefinition.DocumentEnqueueArtifacts.PgsqlUpdateTrigger,
                    DocumentCacheInventoryDefinition.DocumentEnqueueArtifacts.PgsqlUpdateFunction,
                    enqueueIssues,
                    cancellationToken
                )
                .ConfigureAwait(false);
            return;
        }

        await RequireEnqueueTriggerAsync(
                connection,
                dialect,
                DocumentCacheInventoryDefinition.DocumentEnqueueArtifacts.MssqlTrigger,
                expectedFunctionName: null,
                enqueueIssues,
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    private static async Task RequireFunctionAsync(
        DbConnection connection,
        SqlDialect dialect,
        string functionName,
        List<EnqueueIssue> enqueueIssues,
        CancellationToken cancellationToken
    )
    {
        if (dialect != SqlDialect.Pgsql)
        {
            return;
        }

        FunctionSnapshot? function = await ReadPgsqlFunctionAsync(connection, functionName, cancellationToken)
            .ConfigureAwait(false);

        if (function is null)
        {
            enqueueIssues.Add(
                new EnqueueIssue(
                    DocumentCacheEnqueueTriggerStatus.Missing,
                    $"{functionName} function is missing."
                )
            );
            return;
        }

        if (!string.Equals(function.OwnerRole, PgsqlDocumentEnqueueOwnerRole, StringComparison.Ordinal))
        {
            enqueueIssues.Add(
                new EnqueueIssue(
                    DocumentCacheEnqueueTriggerStatus.Invalid,
                    $"{functionName} function is not owned by the required enqueue owner role."
                )
            );
        }

        if (!HasExpectedPgsqlFunctionSearchPath(function.Configuration))
        {
            enqueueIssues.Add(
                new EnqueueIssue(
                    DocumentCacheEnqueueTriggerStatus.Invalid,
                    $"{functionName} function does not set search_path exactly to pg_catalog."
                )
            );
        }

        if (!HasExpectedPgsqlDocumentEnqueueFunctionDefinition(functionName, function.Definition))
        {
            enqueueIssues.Add(
                new EnqueueIssue(
                    DocumentCacheEnqueueTriggerStatus.Invalid,
                    $"{functionName} function has unexpected semantics."
                )
            );
        }
    }

    private static async Task RequireEnqueueTriggerAsync(
        DbConnection connection,
        SqlDialect dialect,
        string triggerName,
        string? expectedFunctionName,
        List<EnqueueIssue> enqueueIssues,
        CancellationToken cancellationToken
    )
    {
        TriggerSnapshot? trigger = await ReadTriggerAsync(
                connection,
                dialect,
                DocumentCacheInventoryDefinition.Document,
                triggerName,
                cancellationToken
            )
            .ConfigureAwait(false);

        if (trigger is null)
        {
            enqueueIssues.Add(
                new EnqueueIssue(
                    DocumentCacheEnqueueTriggerStatus.Missing,
                    $"{triggerName} trigger is missing."
                )
            );
            return;
        }

        if (!trigger.IsEnabled)
        {
            enqueueIssues.Add(
                new EnqueueIssue(
                    DocumentCacheEnqueueTriggerStatus.Disabled,
                    $"{triggerName} trigger is not enabled for ordinary sessions."
                )
            );
        }

        if (dialect == SqlDialect.Pgsql && !HasExpectedPgsqlDocumentEnqueueTriggerShape(triggerName, trigger))
        {
            enqueueIssues.Add(
                new EnqueueIssue(
                    DocumentCacheEnqueueTriggerStatus.Invalid,
                    $"{triggerName} trigger shape is invalid."
                )
            );
        }

        if (
            dialect == SqlDialect.Pgsql
            && expectedFunctionName is not null
            && (trigger.FunctionSchema != DmsSchema || trigger.FunctionName != expectedFunctionName)
        )
        {
            enqueueIssues.Add(
                new EnqueueIssue(
                    DocumentCacheEnqueueTriggerStatus.Invalid,
                    $"{triggerName} trigger uses an unexpected function."
                )
            );
        }

        if (dialect == SqlDialect.Mssql && !HasExpectedMssqlAfterInsertUpdateTriggerShape(trigger))
        {
            enqueueIssues.Add(
                new EnqueueIssue(
                    DocumentCacheEnqueueTriggerStatus.Invalid,
                    $"{triggerName} trigger shape is invalid."
                )
            );
        }

        if (
            dialect == SqlDialect.Mssql
            && !HasExpectedMssqlDocumentEnqueueTriggerDefinition(trigger.Definition)
        )
        {
            enqueueIssues.Add(
                new EnqueueIssue(
                    DocumentCacheEnqueueTriggerStatus.Invalid,
                    $"{triggerName} trigger has unexpected semantics."
                )
            );
        }
    }

    private static async Task RequirePrimaryKeyAsync(
        DbConnection connection,
        SqlDialect dialect,
        DbTableName table,
        string constraintName,
        IReadOnlyList<string> expectedColumns,
        List<InventoryIssue> inventoryIssues,
        CancellationToken cancellationToken
    )
    {
        IReadOnlyList<string>? columns = await ReadPrimaryKeyColumnsAsync(
                connection,
                dialect,
                table,
                constraintName,
                cancellationToken
            )
            .ConfigureAwait(false);

        if (columns is null)
        {
            inventoryIssues.Add(
                new InventoryIssue(
                    DocumentCacheInventoryStatus.Missing,
                    $"{constraintName} primary key is missing."
                )
            );
            return;
        }

        ValidateColumnOrder(columns, expectedColumns, constraintName, inventoryIssues);
    }

    private static async Task RequireCheckConstraintAsync(
        DbConnection connection,
        SqlDialect dialect,
        DbTableName table,
        string constraintName,
        Func<string, bool> hasExpectedDefinition,
        List<InventoryIssue> inventoryIssues,
        CancellationToken cancellationToken
    )
    {
        CheckConstraintSnapshot? constraint = await ReadCheckConstraintAsync(
                connection,
                dialect,
                table,
                constraintName,
                cancellationToken
            )
            .ConfigureAwait(false);

        if (constraint is null)
        {
            inventoryIssues.Add(
                new InventoryIssue(
                    DocumentCacheInventoryStatus.Missing,
                    $"{constraintName} check constraint is missing."
                )
            );
            return;
        }

        if (!constraint.IsEnabled)
        {
            inventoryIssues.Add(
                new InventoryIssue(
                    DocumentCacheInventoryStatus.Invalid,
                    $"{constraintName} check constraint is disabled or untrusted."
                )
            );
        }

        if (!hasExpectedDefinition(constraint.Definition))
        {
            inventoryIssues.Add(
                new InventoryIssue(
                    DocumentCacheInventoryStatus.Invalid,
                    $"{constraintName} check constraint has unexpected semantics."
                )
            );
        }
    }

    private static async Task RequireForeignKeyAsync(
        DbConnection connection,
        SqlDialect dialect,
        DbTableName table,
        string constraintName,
        IReadOnlyList<string> expectedColumns,
        DbTableName referencedTable,
        IReadOnlyList<string> expectedReferencedColumns,
        string deleteAction,
        string updateAction,
        List<InventoryIssue> inventoryIssues,
        CancellationToken cancellationToken
    )
    {
        ForeignKeySnapshot? foreignKey = await ReadForeignKeyAsync(
                connection,
                dialect,
                table,
                constraintName,
                cancellationToken
            )
            .ConfigureAwait(false);

        if (foreignKey is null)
        {
            inventoryIssues.Add(
                new InventoryIssue(
                    DocumentCacheInventoryStatus.Missing,
                    $"{constraintName} foreign key is missing."
                )
            );
            return;
        }

        ValidateColumnOrder(foreignKey.Columns, expectedColumns, constraintName, inventoryIssues);
        ValidateColumnOrder(
            foreignKey.ReferencedColumns,
            expectedReferencedColumns,
            $"{constraintName} referenced columns",
            inventoryIssues
        );

        if (
            foreignKey.ReferencedSchema != referencedTable.Schema.Value
            || foreignKey.ReferencedTable != referencedTable.Name
        )
        {
            inventoryIssues.Add(
                new InventoryIssue(
                    DocumentCacheInventoryStatus.Invalid,
                    $"{constraintName} foreign key references an unexpected table."
                )
            );
        }

        if (foreignKey.DeleteAction != deleteAction || foreignKey.UpdateAction != updateAction)
        {
            inventoryIssues.Add(
                new InventoryIssue(
                    DocumentCacheInventoryStatus.Invalid,
                    $"{constraintName} foreign key has unexpected referential actions."
                )
            );
        }

        if (!foreignKey.IsEnabled || !foreignKey.IsTrusted)
        {
            inventoryIssues.Add(
                new InventoryIssue(
                    DocumentCacheInventoryStatus.Invalid,
                    $"{constraintName} foreign key is disabled or untrusted."
                )
            );
        }
    }

    private static void ValidateColumns(
        TableSpec tableSpec,
        IReadOnlyList<ColumnSnapshot> columns,
        List<InventoryIssue> inventoryIssues
    )
    {
        var columnsByName = columns.ToDictionary(column => column.Name, StringComparer.Ordinal);
        var expectedNames = tableSpec.Columns.Select(column => column.Name).ToHashSet(StringComparer.Ordinal);

        foreach (RequiredColumn expectedColumn in tableSpec.Columns)
        {
            if (!columnsByName.TryGetValue(expectedColumn.Name, out ColumnSnapshot? actualColumn))
            {
                inventoryIssues.Add(
                    new InventoryIssue(
                        DocumentCacheInventoryStatus.Missing,
                        $"{Display(tableSpec.Table)}.{expectedColumn.Name} column is missing."
                    )
                );
                continue;
            }

            if (actualColumn.IsNullable != expectedColumn.IsNullable)
            {
                inventoryIssues.Add(
                    new InventoryIssue(
                        DocumentCacheInventoryStatus.Invalid,
                        $"{Display(tableSpec.Table)}.{expectedColumn.Name} nullability is invalid."
                    )
                );
            }

            if (!expectedColumn.TypeNames.Contains(actualColumn.DataType, StringComparer.OrdinalIgnoreCase))
            {
                inventoryIssues.Add(
                    new InventoryIssue(
                        DocumentCacheInventoryStatus.Invalid,
                        $"{Display(tableSpec.Table)}.{expectedColumn.Name} type is invalid."
                    )
                );
            }

            if (expectedColumn.MaxLength is not null && actualColumn.MaxLength != expectedColumn.MaxLength)
            {
                inventoryIssues.Add(
                    new InventoryIssue(
                        DocumentCacheInventoryStatus.Invalid,
                        $"{Display(tableSpec.Table)}.{expectedColumn.Name} length is invalid."
                    )
                );
            }

            if (
                expectedColumn.CollationName is not null
                && !string.Equals(
                    actualColumn.CollationName,
                    expectedColumn.CollationName,
                    StringComparison.OrdinalIgnoreCase
                )
            )
            {
                inventoryIssues.Add(
                    new InventoryIssue(
                        DocumentCacheInventoryStatus.Invalid,
                        $"{Display(tableSpec.Table)}.{expectedColumn.Name} collation is invalid."
                    )
                );
            }
        }

        if (!tableSpec.ExactColumnSet)
        {
            return;
        }

        string[] unexpectedColumns = columns
            .Select(column => column.Name)
            .Where(columnName => !expectedNames.Contains(columnName))
            .ToArray();

        if (unexpectedColumns.Length > 0)
        {
            inventoryIssues.Add(
                new InventoryIssue(
                    DocumentCacheInventoryStatus.Invalid,
                    $"{Display(tableSpec.Table)} has unexpected columns."
                )
            );
        }
    }

    private static void ValidateColumnOrder(
        IReadOnlyList<string> actualColumns,
        IReadOnlyList<string> expectedColumns,
        string objectName,
        List<InventoryIssue> inventoryIssues
    )
    {
        if (actualColumns.SequenceEqual(expectedColumns, StringComparer.Ordinal))
        {
            return;
        }

        inventoryIssues.Add(
            new InventoryIssue(DocumentCacheInventoryStatus.Invalid, $"{objectName} column order is invalid.")
        );
    }

    private static async Task<bool> TableExistsAsync(
        DbConnection connection,
        SqlDialect dialect,
        DbTableName table,
        CancellationToken cancellationToken
    )
    {
        string sql = dialect switch
        {
            SqlDialect.Pgsql => """
                SELECT 1
                FROM pg_catalog.pg_class c
                INNER JOIN pg_catalog.pg_namespace n
                    ON n.oid = c.relnamespace
                WHERE n.nspname = @schema
                  AND c.relname = @table
                  AND c.relkind = 'r'
                """,
            SqlDialect.Mssql => """
                SELECT 1
                FROM sys.tables t
                INNER JOIN sys.schemas s
                    ON s.schema_id = t.schema_id
                WHERE s.name = @schema
                  AND t.name = @table
                """,
            _ => throw new ArgumentOutOfRangeException(nameof(dialect), dialect, "Unsupported SQL dialect."),
        };

        return await ExecuteScalarAsync<int?>(
                connection,
                sql,
                [Parameter("schema", table.Schema.Value), Parameter("table", table.Name)],
                cancellationToken
            )
            .ConfigureAwait(false)
            is not null;
    }

    private static async Task<IReadOnlyList<ColumnSnapshot>> ReadColumnsAsync(
        DbConnection connection,
        SqlDialect dialect,
        DbTableName table,
        CancellationToken cancellationToken
    )
    {
        string sql = dialect switch
        {
            SqlDialect.Pgsql => """
                SELECT
                    column_name AS ColumnName,
                    ordinal_position AS OrdinalPosition,
                    is_nullable AS IsNullable,
                    udt_name AS DataType,
                    character_maximum_length AS MaxLength,
                    collation_name AS CollationName
                FROM information_schema.columns
                WHERE table_schema = @schema
                  AND table_name = @table
                ORDER BY ordinal_position
                """,
            SqlDialect.Mssql => """
                SELECT
                    c.name AS ColumnName,
                    c.column_id AS OrdinalPosition,
                    CASE WHEN c.is_nullable = 1 THEN 'YES' ELSE 'NO' END AS IsNullable,
                    t.name AS DataType,
                    CASE
                        WHEN t.name IN (N'nvarchar', N'nchar') AND c.max_length > 0 THEN c.max_length / 2
                        ELSE c.max_length
                    END AS MaxLength,
                    c.collation_name AS CollationName
                FROM sys.columns c
                INNER JOIN sys.types t
                    ON t.user_type_id = c.user_type_id
                INNER JOIN sys.tables tables
                    ON tables.object_id = c.object_id
                INNER JOIN sys.schemas schemas
                    ON schemas.schema_id = tables.schema_id
                WHERE schemas.name = @schema
                  AND tables.name = @table
                ORDER BY c.column_id
                """,
            _ => throw new ArgumentOutOfRangeException(nameof(dialect), dialect, "Unsupported SQL dialect."),
        };

        return await ExecuteReaderAsync(
                connection,
                sql,
                [Parameter("schema", table.Schema.Value), Parameter("table", table.Name)],
                reader => new ColumnSnapshot(
                    reader.GetString(reader.GetOrdinal("ColumnName")),
                    reader.GetInt32(reader.GetOrdinal("OrdinalPosition")),
                    string.Equals(
                        reader.GetString(reader.GetOrdinal("IsNullable")),
                        "YES",
                        StringComparison.OrdinalIgnoreCase
                    ),
                    reader.GetString(reader.GetOrdinal("DataType")),
                    ReadNullableInt32(reader, "MaxLength"),
                    ReadNullableString(reader, "CollationName")
                ),
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<string>?> ReadPrimaryKeyColumnsAsync(
        DbConnection connection,
        SqlDialect dialect,
        DbTableName table,
        string constraintName,
        CancellationToken cancellationToken
    )
    {
        string sql = dialect switch
        {
            SqlDialect.Pgsql => """
                SELECT a.attname AS ColumnName
                FROM pg_catalog.pg_constraint c
                INNER JOIN pg_catalog.pg_class rel
                    ON rel.oid = c.conrelid
                INNER JOIN pg_catalog.pg_namespace n
                    ON n.oid = rel.relnamespace
                INNER JOIN unnest(c.conkey) WITH ORDINALITY AS key_columns(attnum, ordinal)
                    ON TRUE
                INNER JOIN pg_catalog.pg_attribute a
                    ON a.attrelid = rel.oid
                   AND a.attnum = key_columns.attnum
                WHERE c.contype = 'p'
                  AND c.conname = @constraintName
                  AND n.nspname = @schema
                  AND rel.relname = @table
                ORDER BY key_columns.ordinal
                """,
            SqlDialect.Mssql => """
                SELECT col.name AS ColumnName
                FROM sys.key_constraints kc
                INNER JOIN sys.tables tables
                    ON tables.object_id = kc.parent_object_id
                INNER JOIN sys.schemas schemas
                    ON schemas.schema_id = tables.schema_id
                INNER JOIN sys.index_columns ic
                    ON ic.object_id = kc.parent_object_id
                   AND ic.index_id = kc.unique_index_id
                INNER JOIN sys.columns col
                    ON col.object_id = ic.object_id
                   AND col.column_id = ic.column_id
                WHERE kc.type = 'PK'
                  AND kc.name = @constraintName
                  AND schemas.name = @schema
                  AND tables.name = @table
                ORDER BY ic.key_ordinal
                """,
            _ => throw new ArgumentOutOfRangeException(nameof(dialect), dialect, "Unsupported SQL dialect."),
        };

        IReadOnlyList<string> columns = await ExecuteReaderAsync(
                connection,
                sql,
                [
                    Parameter("schema", table.Schema.Value),
                    Parameter("table", table.Name),
                    Parameter("constraintName", constraintName),
                ],
                reader => reader.GetString(reader.GetOrdinal("ColumnName")),
                cancellationToken
            )
            .ConfigureAwait(false);

        return columns.Count == 0 ? null : columns;
    }

    private static async Task<CheckConstraintSnapshot?> ReadCheckConstraintAsync(
        DbConnection connection,
        SqlDialect dialect,
        DbTableName table,
        string constraintName,
        CancellationToken cancellationToken
    )
    {
        string sql = dialect switch
        {
            SqlDialect.Pgsql => """
                SELECT pg_catalog.pg_get_constraintdef(c.oid, true) AS Definition,
                       c.convalidated AS IsEnabled
                FROM pg_catalog.pg_constraint c
                INNER JOIN pg_catalog.pg_class rel
                    ON rel.oid = c.conrelid
                INNER JOIN pg_catalog.pg_namespace n
                    ON n.oid = rel.relnamespace
                WHERE c.contype = 'c'
                  AND c.conname = @constraintName
                  AND n.nspname = @schema
                  AND rel.relname = @table
                """,
            SqlDialect.Mssql => """
                SELECT checks.definition AS Definition,
                       CASE
                           WHEN checks.is_disabled = 0 AND checks.is_not_trusted = 0 THEN 1
                           ELSE 0
                       END AS IsEnabled
                FROM sys.check_constraints checks
                INNER JOIN sys.tables tables
                    ON tables.object_id = checks.parent_object_id
                INNER JOIN sys.schemas schemas
                    ON schemas.schema_id = tables.schema_id
                WHERE checks.name = @constraintName
                  AND schemas.name = @schema
                  AND tables.name = @table
                """,
            _ => throw new ArgumentOutOfRangeException(nameof(dialect), dialect, "Unsupported SQL dialect."),
        };

        return await ExecuteSingleOrDefaultAsync(
                connection,
                sql,
                [
                    Parameter("schema", table.Schema.Value),
                    Parameter("table", table.Name),
                    Parameter("constraintName", constraintName),
                ],
                reader => new CheckConstraintSnapshot(
                    reader.GetString(reader.GetOrdinal("Definition")),
                    ReadBooleanLike(reader, "IsEnabled")
                ),
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    private static async Task<ForeignKeySnapshot?> ReadForeignKeyAsync(
        DbConnection connection,
        SqlDialect dialect,
        DbTableName table,
        string constraintName,
        CancellationToken cancellationToken
    )
    {
        string sql = dialect switch
        {
            SqlDialect.Pgsql => """
                SELECT
                    a.attname AS ColumnName,
                    refn.nspname AS ReferencedSchema,
                    refrel.relname AS ReferencedTable,
                    refa.attname AS ReferencedColumn,
                    CASE c.confdeltype WHEN 'c' THEN 'CASCADE' WHEN 'a' THEN 'NO_ACTION' ELSE c.confdeltype::text END AS DeleteAction,
                    CASE c.confupdtype WHEN 'c' THEN 'CASCADE' WHEN 'a' THEN 'NO_ACTION' ELSE c.confupdtype::text END AS UpdateAction,
                    TRUE AS IsEnabled,
                    c.convalidated AS IsTrusted,
                    key_columns.ordinal AS Ordinal
                FROM pg_catalog.pg_constraint c
                INNER JOIN pg_catalog.pg_class rel
                    ON rel.oid = c.conrelid
                INNER JOIN pg_catalog.pg_namespace n
                    ON n.oid = rel.relnamespace
                INNER JOIN pg_catalog.pg_class refrel
                    ON refrel.oid = c.confrelid
                INNER JOIN pg_catalog.pg_namespace refn
                    ON refn.oid = refrel.relnamespace
                INNER JOIN unnest(c.conkey) WITH ORDINALITY AS key_columns(attnum, ordinal)
                    ON TRUE
                INNER JOIN unnest(c.confkey) WITH ORDINALITY AS ref_key_columns(attnum, ordinal)
                    ON ref_key_columns.ordinal = key_columns.ordinal
                INNER JOIN pg_catalog.pg_attribute a
                    ON a.attrelid = rel.oid
                   AND a.attnum = key_columns.attnum
                INNER JOIN pg_catalog.pg_attribute refa
                    ON refa.attrelid = refrel.oid
                   AND refa.attnum = ref_key_columns.attnum
                WHERE c.contype = 'f'
                  AND c.conname = @constraintName
                  AND n.nspname = @schema
                  AND rel.relname = @table
                ORDER BY key_columns.ordinal
                """,
            SqlDialect.Mssql => """
                SELECT
                    parent_columns.name AS ColumnName,
                    referenced_schemas.name AS ReferencedSchema,
                    referenced_tables.name AS ReferencedTable,
                    referenced_columns.name AS ReferencedColumn,
                    foreign_keys.delete_referential_action_desc AS DeleteAction,
                    foreign_keys.update_referential_action_desc AS UpdateAction,
                    CASE WHEN foreign_keys.is_disabled = 0 THEN 1 ELSE 0 END AS IsEnabled,
                    CASE WHEN foreign_keys.is_not_trusted = 0 THEN 1 ELSE 0 END AS IsTrusted,
                    foreign_key_columns.constraint_column_id AS Ordinal
                FROM sys.foreign_keys foreign_keys
                INNER JOIN sys.tables parent_tables
                    ON parent_tables.object_id = foreign_keys.parent_object_id
                INNER JOIN sys.schemas parent_schemas
                    ON parent_schemas.schema_id = parent_tables.schema_id
                INNER JOIN sys.tables referenced_tables
                    ON referenced_tables.object_id = foreign_keys.referenced_object_id
                INNER JOIN sys.schemas referenced_schemas
                    ON referenced_schemas.schema_id = referenced_tables.schema_id
                INNER JOIN sys.foreign_key_columns foreign_key_columns
                    ON foreign_key_columns.constraint_object_id = foreign_keys.object_id
                INNER JOIN sys.columns parent_columns
                    ON parent_columns.object_id = parent_tables.object_id
                   AND parent_columns.column_id = foreign_key_columns.parent_column_id
                INNER JOIN sys.columns referenced_columns
                    ON referenced_columns.object_id = referenced_tables.object_id
                   AND referenced_columns.column_id = foreign_key_columns.referenced_column_id
                WHERE foreign_keys.name = @constraintName
                  AND parent_schemas.name = @schema
                  AND parent_tables.name = @table
                ORDER BY foreign_key_columns.constraint_column_id
                """,
            _ => throw new ArgumentOutOfRangeException(nameof(dialect), dialect, "Unsupported SQL dialect."),
        };

        IReadOnlyList<ForeignKeyRow> rows = await ExecuteReaderAsync(
                connection,
                sql,
                [
                    Parameter("schema", table.Schema.Value),
                    Parameter("table", table.Name),
                    Parameter("constraintName", constraintName),
                ],
                reader => new ForeignKeyRow(
                    reader.GetString(reader.GetOrdinal("ColumnName")),
                    reader.GetString(reader.GetOrdinal("ReferencedSchema")),
                    reader.GetString(reader.GetOrdinal("ReferencedTable")),
                    reader.GetString(reader.GetOrdinal("ReferencedColumn")),
                    reader.GetString(reader.GetOrdinal("DeleteAction")),
                    reader.GetString(reader.GetOrdinal("UpdateAction")),
                    ReadBooleanLike(reader, "IsEnabled"),
                    ReadBooleanLike(reader, "IsTrusted")
                ),
                cancellationToken
            )
            .ConfigureAwait(false);

        if (rows.Count == 0)
        {
            return null;
        }

        ForeignKeyRow first = rows[0];
        return new ForeignKeySnapshot(
            rows.Select(row => row.Column).ToArray(),
            first.ReferencedSchema,
            first.ReferencedTable,
            rows.Select(row => row.ReferencedColumn).ToArray(),
            first.DeleteAction,
            first.UpdateAction,
            first.IsEnabled,
            first.IsTrusted
        );
    }

    private static async Task<IndexSnapshot?> ReadIndexAsync(
        DbConnection connection,
        SqlDialect dialect,
        DbTableName table,
        string indexName,
        CancellationToken cancellationToken
    )
    {
        string sql = dialect switch
        {
            SqlDialect.Pgsql => """
                SELECT
                    a.attname AS ColumnName,
                    idx.indisvalid AND idx.indisready AS IsUsable,
                    idx.indpred IS NOT NULL AS IsFilteredOrPartial
                FROM pg_catalog.pg_index idx
                INNER JOIN pg_catalog.pg_class index_rel
                    ON index_rel.oid = idx.indexrelid
                INNER JOIN pg_catalog.pg_class table_rel
                    ON table_rel.oid = idx.indrelid
                INNER JOIN pg_catalog.pg_namespace n
                    ON n.oid = table_rel.relnamespace
                INNER JOIN unnest(idx.indkey) WITH ORDINALITY AS key_columns(attnum, ordinal)
                    ON TRUE
                INNER JOIN pg_catalog.pg_attribute a
                    ON a.attrelid = table_rel.oid
                   AND a.attnum = key_columns.attnum
                WHERE n.nspname = @schema
                  AND table_rel.relname = @table
                  AND index_rel.relname = @indexName
                ORDER BY key_columns.ordinal
                """,
            SqlDialect.Mssql => """
                SELECT
                    columns.name AS ColumnName,
                    CASE WHEN indexes.is_disabled = 0 THEN 1 ELSE 0 END AS IsUsable,
                    indexes.has_filter AS IsFilteredOrPartial
                FROM sys.indexes indexes
                INNER JOIN sys.tables tables
                    ON tables.object_id = indexes.object_id
                INNER JOIN sys.schemas schemas
                    ON schemas.schema_id = tables.schema_id
                INNER JOIN sys.index_columns index_columns
                    ON index_columns.object_id = indexes.object_id
                   AND index_columns.index_id = indexes.index_id
                INNER JOIN sys.columns columns
                    ON columns.object_id = tables.object_id
                   AND columns.column_id = index_columns.column_id
                WHERE schemas.name = @schema
                  AND tables.name = @table
                  AND indexes.name = @indexName
                  AND index_columns.is_included_column = 0
                ORDER BY index_columns.key_ordinal
                """,
            _ => throw new ArgumentOutOfRangeException(nameof(dialect), dialect, "Unsupported SQL dialect."),
        };

        IReadOnlyList<IndexRow> rows = await ExecuteReaderAsync(
                connection,
                sql,
                [
                    Parameter("schema", table.Schema.Value),
                    Parameter("table", table.Name),
                    Parameter("indexName", indexName),
                ],
                reader => new IndexRow(
                    reader.GetString(reader.GetOrdinal("ColumnName")),
                    ReadBooleanLike(reader, "IsUsable"),
                    ReadBooleanLike(reader, "IsFilteredOrPartial")
                ),
                cancellationToken
            )
            .ConfigureAwait(false);

        return rows.Count == 0
            ? null
            : new IndexSnapshot(
                rows.Select(row => row.Column).ToArray(),
                rows.All(row => row.IsUsable),
                rows.Any(row => row.IsFilteredOrPartial)
            );
    }

    private static async Task<TriggerSnapshot?> ReadTriggerAsync(
        DbConnection connection,
        SqlDialect dialect,
        DbTableName table,
        string triggerName,
        CancellationToken cancellationToken
    )
    {
        string sql = dialect switch
        {
            SqlDialect.Pgsql => """
                SELECT
                    trigger_info.tgenabled IN ('O', 'A') AS IsEnabled,
                    trigger_info.tgisinternal AS IsInternal,
                    (trigger_info.tgtype::int & 1) = 1 AS IsRowLevel,
                    (trigger_info.tgtype::int & 2) = 2 AS IsBefore,
                    (trigger_info.tgtype::int & 64) = 64 AS IsInsteadOf,
                    (trigger_info.tgtype::int & 4) = 4 AS IsInsert,
                    (trigger_info.tgtype::int & 8) = 8 AS IsDelete,
                    (trigger_info.tgtype::int & 16) = 16 AS IsUpdate,
                    (trigger_info.tgtype::int & 32) = 32 AS IsTruncate,
                    NULLIF(trigger_info.tgoldtable::text, '') AS OldTransitionTable,
                    NULLIF(trigger_info.tgnewtable::text, '') AS NewTransitionTable,
                    proc_namespace.nspname AS FunctionSchema,
                    proc.proname AS FunctionName,
                    pg_catalog.pg_get_functiondef(proc.oid) AS Definition
                FROM pg_catalog.pg_trigger trigger_info
                INNER JOIN pg_catalog.pg_class rel
                    ON rel.oid = trigger_info.tgrelid
                INNER JOIN pg_catalog.pg_namespace n
                    ON n.oid = rel.relnamespace
                INNER JOIN pg_catalog.pg_proc proc
                    ON proc.oid = trigger_info.tgfoid
                INNER JOIN pg_catalog.pg_namespace proc_namespace
                    ON proc_namespace.oid = proc.pronamespace
                WHERE trigger_info.tgname = @triggerName
                  AND n.nspname = @schema
                  AND rel.relname = @table
                """,
            SqlDialect.Mssql => """
                SELECT
                    CASE WHEN triggers.is_disabled = 0 THEN 1 ELSE 0 END AS IsEnabled,
                    CASE WHEN triggers.is_ms_shipped = 0 THEN 0 ELSE 1 END AS IsInternal,
                    CAST(0 AS bit) AS IsRowLevel,
                    CAST(0 AS bit) AS IsBefore,
                    CASE WHEN triggers.is_instead_of_trigger = 1 THEN 1 ELSE 0 END AS IsInsteadOf,
                    CASE
                        WHEN EXISTS (
                            SELECT 1
                            FROM sys.trigger_events trigger_events
                            WHERE trigger_events.object_id = triggers.object_id
                              AND trigger_events.type_desc = N'INSERT'
                        ) THEN 1 ELSE 0
                    END AS IsInsert,
                    CASE
                        WHEN EXISTS (
                            SELECT 1
                            FROM sys.trigger_events trigger_events
                            WHERE trigger_events.object_id = triggers.object_id
                              AND trigger_events.type_desc = N'DELETE'
                        ) THEN 1 ELSE 0
                    END AS IsDelete,
                    CASE
                        WHEN EXISTS (
                            SELECT 1
                            FROM sys.trigger_events trigger_events
                            WHERE trigger_events.object_id = triggers.object_id
                              AND trigger_events.type_desc = N'UPDATE'
                        ) THEN 1 ELSE 0
                    END AS IsUpdate,
                    CAST(0 AS bit) AS IsTruncate,
                    CAST(NULL AS nvarchar(128)) AS OldTransitionTable,
                    CAST(NULL AS nvarchar(128)) AS NewTransitionTable,
                    CAST(NULL AS nvarchar(128)) AS FunctionSchema,
                    CAST(NULL AS nvarchar(128)) AS FunctionName,
                    OBJECT_DEFINITION(triggers.object_id) AS Definition
                FROM sys.triggers triggers
                INNER JOIN sys.tables tables
                    ON tables.object_id = triggers.parent_id
                INNER JOIN sys.schemas schemas
                    ON schemas.schema_id = tables.schema_id
                WHERE triggers.name = @triggerName
                  AND schemas.name = @schema
                  AND tables.name = @table
                """,
            _ => throw new ArgumentOutOfRangeException(nameof(dialect), dialect, "Unsupported SQL dialect."),
        };

        return await ExecuteSingleOrDefaultAsync(
                connection,
                sql,
                [
                    Parameter("schema", table.Schema.Value),
                    Parameter("table", table.Name),
                    Parameter("triggerName", triggerName),
                ],
                reader => new TriggerSnapshot(
                    ReadBooleanLike(reader, "IsEnabled"),
                    ReadNullableBooleanLike(reader, "IsInternal"),
                    ReadNullableBooleanLike(reader, "IsRowLevel"),
                    ReadNullableBooleanLike(reader, "IsBefore"),
                    ReadNullableBooleanLike(reader, "IsInsteadOf"),
                    ReadNullableBooleanLike(reader, "IsInsert"),
                    ReadNullableBooleanLike(reader, "IsDelete"),
                    ReadNullableBooleanLike(reader, "IsUpdate"),
                    ReadNullableBooleanLike(reader, "IsTruncate"),
                    ReadNullableString(reader, "OldTransitionTable"),
                    ReadNullableString(reader, "NewTransitionTable"),
                    ReadNullableString(reader, "FunctionSchema"),
                    ReadNullableString(reader, "FunctionName"),
                    ReadNullableString(reader, "Definition")
                ),
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    private static async Task<FunctionSnapshot?> ReadPgsqlFunctionAsync(
        DbConnection connection,
        string functionName,
        CancellationToken cancellationToken
    )
    {
        const string sql = """
            SELECT
                n.nspname AS FunctionSchema,
                proc.proname AS FunctionName,
                pg_catalog.pg_get_userbyid(proc.proowner) AS OwnerRole,
                COALESCE(proc.proconfig, ARRAY[]::text[]) AS Configuration,
                pg_catalog.pg_get_functiondef(proc.oid) AS Definition
            FROM pg_catalog.pg_proc proc
            INNER JOIN pg_catalog.pg_namespace n
                ON n.oid = proc.pronamespace
            WHERE n.nspname = @schema
              AND proc.proname = @functionName
              AND pg_catalog.pg_get_function_identity_arguments(proc.oid) = ''
            """;

        return await ExecuteSingleOrDefaultAsync(
                connection,
                sql,
                [Parameter("schema", DmsSchema), Parameter("functionName", functionName)],
                reader => new FunctionSnapshot(
                    reader.GetString(reader.GetOrdinal("FunctionSchema")),
                    reader.GetString(reader.GetOrdinal("FunctionName")),
                    reader.GetString(reader.GetOrdinal("OwnerRole")),
                    ReadStringArray(reader, "Configuration"),
                    reader.GetString(reader.GetOrdinal("Definition"))
                ),
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    private static async Task RequireSingletonRowAsync(
        DbConnection connection,
        SqlDialect dialect,
        DbTableName table,
        string singletonColumn,
        HashSet<string> existingTables,
        Dictionary<string, IReadOnlyList<ColumnSnapshot>> columnsByTable,
        List<InventoryIssue> inventoryIssues,
        CancellationToken cancellationToken
    )
    {
        string tableKey = ToTableKey(table);
        if (!existingTables.Contains(tableKey))
        {
            return;
        }

        if (
            !columnsByTable.TryGetValue(tableKey, out IReadOnlyList<ColumnSnapshot>? columns)
            || !columns.Any(column => column.Name == singletonColumn)
        )
        {
            return;
        }

        string tableSql = SqlIdentifierQuoter.QuoteTableName(dialect, table);
        string columnSql = SqlIdentifierQuoter.QuoteIdentifier(dialect, singletonColumn);
        string sql = dialect switch
        {
            SqlDialect.Pgsql => $"SELECT 1 FROM {tableSql} WHERE {columnSql} = 1 LIMIT 1",
            SqlDialect.Mssql => $"SELECT TOP (1) 1 FROM {tableSql} WHERE {columnSql} = 1",
            _ => throw new ArgumentOutOfRangeException(nameof(dialect), dialect, "Unsupported SQL dialect."),
        };

        bool singletonExists =
            await ExecuteScalarAsync<int?>(connection, sql, [], cancellationToken).ConfigureAwait(false)
            is not null;

        if (!singletonExists)
        {
            inventoryIssues.Add(
                new InventoryIssue(
                    DocumentCacheInventoryStatus.Missing,
                    $"{Display(table)} singleton row is missing."
                )
            );
        }
    }

    private static DocumentCacheInventoryValidationResult ToInventoryResult(
        IReadOnlyList<InventoryIssue> issues
    )
    {
        if (issues.Count == 0)
        {
            return new DocumentCacheInventoryValidationResult(
                DocumentCacheInventoryStatus.Satisfied,
                "DocumentCache inventory is satisfied."
            );
        }

        DocumentCacheInventoryStatus status = DocumentCacheInventoryStatus.Invalid;

        if (issues.Any(issue => issue.Status == DocumentCacheInventoryStatus.Missing))
        {
            status = DocumentCacheInventoryStatus.Missing;
        }

        if (issues.Any(issue => issue.Status == DocumentCacheInventoryStatus.Unreadable))
        {
            status = DocumentCacheInventoryStatus.Unreadable;
        }

        return new DocumentCacheInventoryValidationResult(status, BuildIssueMessage(issues));
    }

    private static DocumentCacheEnqueueTriggerValidationResult ToEnqueueResult(
        IReadOnlyList<EnqueueIssue> issues
    )
    {
        if (issues.Count == 0)
        {
            return new DocumentCacheEnqueueTriggerValidationResult(
                DocumentCacheEnqueueTriggerStatus.Satisfied,
                "DocumentCache enqueue inventory is satisfied."
            );
        }

        DocumentCacheEnqueueTriggerStatus status = DocumentCacheEnqueueTriggerStatus.Invalid;

        if (issues.Any(issue => issue.Status == DocumentCacheEnqueueTriggerStatus.Disabled))
        {
            status = DocumentCacheEnqueueTriggerStatus.Disabled;
        }

        if (issues.Any(issue => issue.Status == DocumentCacheEnqueueTriggerStatus.Missing))
        {
            status = DocumentCacheEnqueueTriggerStatus.Missing;
        }

        if (issues.Any(issue => issue.Status == DocumentCacheEnqueueTriggerStatus.Unreadable))
        {
            status = DocumentCacheEnqueueTriggerStatus.Unreadable;
        }

        return new DocumentCacheEnqueueTriggerValidationResult(status, BuildIssueMessage(issues));
    }

    private static string BuildIssueMessage<TIssue>(IReadOnlyList<TIssue> issues)
        where TIssue : IValidationIssue => string.Join(" ", issues.Take(6).Select(issue => issue.Message));

    private static bool HasExpectedPgsqlDocumentCacheUuidValidationFunctionDefinition(string? definition) =>
        HasNormalizedTokens(
            definition,
            "_CANONICAL_DOCUMENT_UUID",
            "DMSDOCUMENT",
            "DOCUMENTUUID",
            "NEWDOCUMENTID",
            "NEWDOCUMENTUUID",
            "<>",
            "RAISEEXCEPTION",
            "RETURNNEW"
        );

    private static bool HasExpectedPgsqlDocumentCacheUuidTriggerShape(TriggerSnapshot trigger) =>
        trigger.IsInternal == false
        && trigger.IsRowLevel == true
        && trigger.IsBefore == true
        && trigger.IsInsteadOf == false
        && trigger.IsInsert == true
        && trigger.IsUpdate == true
        && trigger.IsDelete == false
        && trigger.IsTruncate == false
        && trigger.OldTransitionTable is null
        && trigger.NewTransitionTable is null;

    private static bool HasExpectedPgsqlDocumentEnqueueTriggerShape(
        string triggerName,
        TriggerSnapshot trigger
    ) =>
        triggerName switch
        {
            DocumentCacheInventoryDefinition.DocumentEnqueueArtifacts.PgsqlInsertTrigger =>
                HasExpectedPgsqlDocumentEnqueueTriggerShape(
                    trigger,
                    expectsInsertEvent: true,
                    expectsUpdateEvent: false,
                    expectedOldTransitionTable: null,
                    expectedNewTransitionTable: "new_rows"
                ),
            DocumentCacheInventoryDefinition.DocumentEnqueueArtifacts.PgsqlUpdateTrigger =>
                HasExpectedPgsqlDocumentEnqueueTriggerShape(
                    trigger,
                    expectsInsertEvent: false,
                    expectsUpdateEvent: true,
                    expectedOldTransitionTable: "old_rows",
                    expectedNewTransitionTable: "new_rows"
                ),
            _ => false,
        };

    private static bool HasExpectedPgsqlDocumentEnqueueTriggerShape(
        TriggerSnapshot trigger,
        bool expectsInsertEvent,
        bool expectsUpdateEvent,
        string? expectedOldTransitionTable,
        string? expectedNewTransitionTable
    ) =>
        trigger.IsInternal == false
        && trigger.IsRowLevel == false
        && trigger.IsBefore == false
        && trigger.IsInsteadOf == false
        && trigger.IsInsert == expectsInsertEvent
        && trigger.IsUpdate == expectsUpdateEvent
        && trigger.IsDelete == false
        && trigger.IsTruncate == false
        && string.Equals(trigger.OldTransitionTable, expectedOldTransitionTable, StringComparison.Ordinal)
        && string.Equals(trigger.NewTransitionTable, expectedNewTransitionTable, StringComparison.Ordinal);

    private static bool HasExpectedMssqlDocumentCacheUuidValidationTriggerDefinition(string? definition) =>
        HasNormalizedTokens(
            definition,
            "INSERTED",
            "DMSDOCUMENT",
            "DOCUMENTID",
            "DOCUMENTUUID",
            "<>",
            "THROW"
        );

    private static bool HasExpectedMssqlAfterInsertUpdateTriggerShape(TriggerSnapshot trigger) =>
        trigger.IsInternal == false
        && trigger.IsRowLevel == false
        && trigger.IsBefore == false
        && trigger.IsInsteadOf == false
        && trigger.IsInsert == true
        && trigger.IsUpdate == true
        && trigger.IsDelete == false
        && trigger.IsTruncate == false;

    private static bool HasExpectedPgsqlDocumentEnqueueFunctionDefinition(
        string functionName,
        string? definition
    )
    {
        if (
            !HasNormalizedTokens(
                definition,
                "SECURITYDEFINER",
                "DOCUMENTCACHESTATE",
                "PROJECTIONLIFECYCLESTATE",
                "STATEID=1",
                "'DISABLED'",
                "'RESETTING'",
                "'REBUILDING'",
                "'TRACKING'",
                "STATEMENT_TIMESTAMP",
                "DOCUMENTPROJECTIONWORK",
                "REQUIREDCONTENTVERSION",
                "FIRSTENQUEUEDAT",
                "LASTENQUEUEDAT",
                "ONCONFLICT",
                "DOUPDATE",
                "WORKREQUIREDCONTENTVERSION<EXCLUDEDREQUIREDCONTENTVERSION",
                "RETURNNULL"
            )
        )
        {
            return false;
        }

        if (functionName == DocumentCacheInventoryDefinition.DocumentEnqueueArtifacts.PgsqlInsertFunction)
        {
            return HasNormalizedTokens(definition, "FROMNEW_ROWS");
        }

        if (functionName == DocumentCacheInventoryDefinition.DocumentEnqueueArtifacts.PgsqlUpdateFunction)
        {
            return HasNormalizedTokens(definition, "NEW_ROWS", "OLD_ROWS", "<>");
        }

        return false;
    }

    private static bool HasExpectedPgsqlFunctionSearchPath(IReadOnlyList<string> configuration) =>
        configuration.Count(IsPgsqlSearchPathConfiguration) == 1
        && configuration.Any(configurationValue =>
            string.Equals(
                configurationValue,
                PgsqlDocumentEnqueueSearchPathConfiguration,
                StringComparison.Ordinal
            )
        );

    private static bool IsPgsqlSearchPathConfiguration(string configurationValue) =>
        configurationValue.StartsWith("search_path=", StringComparison.Ordinal);

    private static bool HasExpectedMssqlDocumentEnqueueTriggerDefinition(string? definition) =>
        HasNormalizedTokens(
            definition,
            "DOCUMENTCACHESTATE",
            "PROJECTIONLIFECYCLESTATE",
            "STATEID=1",
            "'DISABLED'",
            "'RESETTING'",
            "'REBUILDING'",
            "'TRACKING'",
            "INSERTED",
            "DELETED",
            "MAX",
            "GROUPBYIDOCUMENTID",
            "DMSDOCUMENTPROJECTIONWORK",
            "UPDATEWORK",
            "SETWORKREQUIREDCONTENTVERSION=REQREQUIREDCONTENTVERSION",
            "WORKREQUIREDCONTENTVERSION<REQREQUIREDCONTENTVERSION",
            "INSERTINTODMSDOCUMENTPROJECTIONWORK",
            "LEFTJOINDMSDOCUMENTPROJECTIONWORK"
        );

    private static bool HasNormalizedTokens(string? definition, params string[] tokens) =>
        !string.IsNullOrWhiteSpace(definition)
        && ContainsAll(NormalizeDefinition(StripSqlComments(definition)), tokens);

    private static bool ContainsAll(string value, params string[] tokens) =>
        Array.TrueForAll(tokens, token => value.Contains(token, StringComparison.OrdinalIgnoreCase));

    private static bool HasExpectedSingletonCheck(string definition, string singletonColumn) =>
        NormalizeCheckExpression(definition) == $"{NormalizeCheckToken(singletonColumn)}=1";

    private static bool HasExpectedDocumentCacheJsonObjectCheck(SqlDialect dialect, string definition) =>
        NormalizeCheckExpression(definition)
        == (
            dialect == SqlDialect.Pgsql
                ? PgsqlDocumentCacheJsonObjectCheckExpression
                : MssqlDocumentCacheJsonObjectCheckExpression
        );

    private static bool HasExpectedDocumentCacheStateLifecycleCheck(SqlDialect dialect, string definition) =>
        NormalizeCheckExpression(definition)
        == (
            dialect == SqlDialect.Pgsql
                ? PgsqlDocumentCacheStateLifecycleCheckExpression
                : MssqlDocumentCacheStateLifecycleCheckExpression
        );

    private static string NormalizeCheckExpression(string definition)
    {
        string normalized = NormalizeCheckToken(definition);
        return normalized.StartsWith("CHECK", StringComparison.Ordinal)
            ? normalized["CHECK".Length..]
            : normalized;
    }

    private static string NormalizeCheckToken(string definition) =>
        new(
            definition
                .Where(character =>
                    char.IsLetterOrDigit(character)
                    || character == '='
                    || character == '<'
                    || character == '>'
                    || character == '_'
                    || character == '\''
                    || character == '{'
                )
                .Select(char.ToUpperInvariant)
                .ToArray()
        );

    private static string NormalizeDefinition(string definition) =>
        new(
            definition
                .Where(character =>
                    char.IsLetterOrDigit(character)
                    || character == '='
                    || character == '<'
                    || character == '>'
                    || character == '_'
                    || character == '\''
                )
                .Select(char.ToUpperInvariant)
                .ToArray()
        );

    private static string StripSqlComments(string definition)
    {
        var uncommented = new StringBuilder(definition.Length);

        bool inSingleQuotedString = false;
        bool inDoubleQuotedIdentifier = false;
        bool inBracketedIdentifier = false;
        int blockCommentDepth = 0;

        int index = 0;
        while (index < definition.Length)
        {
            char character = definition[index];
            char? nextCharacter = index + 1 < definition.Length ? definition[index + 1] : null;

            if (blockCommentDepth > 0)
            {
                if (character == '/' && nextCharacter == '*')
                {
                    blockCommentDepth++;
                    index += 2;
                    continue;
                }

                if (character == '*' && nextCharacter == '/')
                {
                    blockCommentDepth--;
                    index += 2;
                    continue;
                }

                if (character is '\r' or '\n')
                {
                    uncommented.Append(character);
                }

                index++;
                continue;
            }

            if (inSingleQuotedString)
            {
                uncommented.Append(character);

                if (character == '\'' && nextCharacter == '\'')
                {
                    uncommented.Append(nextCharacter.Value);
                    index += 2;
                    continue;
                }

                if (character == '\'')
                {
                    inSingleQuotedString = false;
                }

                index++;
                continue;
            }

            if (inDoubleQuotedIdentifier)
            {
                uncommented.Append(character);

                if (character == '"' && nextCharacter == '"')
                {
                    uncommented.Append(nextCharacter.Value);
                    index += 2;
                    continue;
                }

                if (character == '"')
                {
                    inDoubleQuotedIdentifier = false;
                }

                index++;
                continue;
            }

            if (inBracketedIdentifier)
            {
                uncommented.Append(character);

                if (character == ']' && nextCharacter == ']')
                {
                    uncommented.Append(nextCharacter.Value);
                    index += 2;
                    continue;
                }

                if (character == ']')
                {
                    inBracketedIdentifier = false;
                }

                index++;
                continue;
            }

            if (character == '-' && nextCharacter == '-')
            {
                index += 2;
                while (index < definition.Length && definition[index] is not '\r' and not '\n')
                {
                    index++;
                }

                continue;
            }

            if (character == '/' && nextCharacter == '*')
            {
                blockCommentDepth++;
                index += 2;
                continue;
            }

            if (character == '\'')
            {
                inSingleQuotedString = true;
            }
            else if (character == '"')
            {
                inDoubleQuotedIdentifier = true;
            }
            else if (character == '[')
            {
                inBracketedIdentifier = true;
            }

            uncommented.Append(character);
            index++;
        }

        return uncommented.ToString();
    }

    private static string Display(DbTableName table) => $"{table.Schema.Value}.{table.Name}";

    private static string ToTableKey(DbTableName table) => $"{table.Schema.Value}.{table.Name}";

    private static QueryParameter Parameter(string name, object value) => new(name, value);

    private static async Task<T?> ExecuteScalarAsync<T>(
        DbConnection connection,
        string sql,
        IReadOnlyList<QueryParameter> parameters,
        CancellationToken cancellationToken
    )
    {
        await using DbCommand command = CreateCommand(connection, sql, parameters);
        object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        if (value is null || value is DBNull)
        {
            return default;
        }

        Type targetType = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
        return (T)Convert.ChangeType(value, targetType);
    }

    private static async Task<T?> ExecuteSingleOrDefaultAsync<T>(
        DbConnection connection,
        string sql,
        IReadOnlyList<QueryParameter> parameters,
        Func<DbDataReader, T> map,
        CancellationToken cancellationToken
    )
    {
        IReadOnlyList<T> rows = await ExecuteReaderAsync(connection, sql, parameters, map, cancellationToken)
            .ConfigureAwait(false);

        return rows.Count == 0 ? default : rows[0];
    }

    private static async Task<IReadOnlyList<T>> ExecuteReaderAsync<T>(
        DbConnection connection,
        string sql,
        IReadOnlyList<QueryParameter> parameters,
        Func<DbDataReader, T> map,
        CancellationToken cancellationToken
    )
    {
        await using DbCommand command = CreateCommand(connection, sql, parameters);
        await using DbDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        var rows = new List<T>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(map(reader));
        }

        return rows;
    }

    private static DbCommand CreateCommand(
        DbConnection connection,
        string sql,
        IReadOnlyList<QueryParameter> parameters
    )
    {
        DbCommand command = connection.CreateCommand();
        command.CommandText = sql;

        foreach (QueryParameter parameterValue in parameters)
        {
            DbParameter parameter = command.CreateParameter();
            parameter.ParameterName = parameterValue.Name;
            parameter.Value = parameterValue.Value;
            command.Parameters.Add(parameter);
        }

        return command;
    }

    private static int? ReadNullableInt32(DbDataReader reader, string columnName)
    {
        int ordinal = reader.GetOrdinal(columnName);
        return reader.IsDBNull(ordinal) ? null : Convert.ToInt32(reader.GetValue(ordinal));
    }

    private static string? ReadNullableString(DbDataReader reader, string columnName)
    {
        int ordinal = reader.GetOrdinal(columnName);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private static IReadOnlyList<string> ReadStringArray(DbDataReader reader, string columnName)
    {
        int ordinal = reader.GetOrdinal(columnName);
        if (reader.IsDBNull(ordinal))
        {
            return [];
        }

        object value = reader.GetValue(ordinal);
        return value switch
        {
            string[] arrayValue => arrayValue,
            IEnumerable<string> enumerableValue => enumerableValue.ToArray(),
            _ => throw new InvalidOperationException(
                $"Column {columnName} could not be read as a string array."
            ),
        };
    }

    private static bool ReadBooleanLike(DbDataReader reader, string columnName)
    {
        int ordinal = reader.GetOrdinal(columnName);
        object value = reader.GetValue(ordinal);

        return ConvertBooleanLike(value);
    }

    private static bool? ReadNullableBooleanLike(DbDataReader reader, string columnName)
    {
        int ordinal = reader.GetOrdinal(columnName);
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        return ConvertBooleanLike(reader.GetValue(ordinal));
    }

    private static bool ConvertBooleanLike(object value)
    {
        return value switch
        {
            bool boolValue => boolValue,
            byte byteValue => byteValue != 0,
            short shortValue => shortValue != 0,
            int intValue => intValue != 0,
            long longValue => longValue != 0,
            _ => Convert.ToBoolean(value),
        };
    }

    private sealed record TableSpec(
        DbTableName Table,
        IReadOnlyList<RequiredColumn> Columns,
        bool ExactColumnSet
    );

    private sealed record RequiredColumn(
        string Name,
        IReadOnlyList<string> TypeNames,
        bool IsNullable,
        int? MaxLength = null,
        string? CollationName = null
    );

    private sealed record ColumnSnapshot(
        string Name,
        int OrdinalPosition,
        bool IsNullable,
        string DataType,
        int? MaxLength,
        string? CollationName
    );

    private sealed record CheckConstraintSnapshot(string Definition, bool IsEnabled);

    private sealed record ForeignKeyRow(
        string Column,
        string ReferencedSchema,
        string ReferencedTable,
        string ReferencedColumn,
        string DeleteAction,
        string UpdateAction,
        bool IsEnabled,
        bool IsTrusted
    );

    private sealed record ForeignKeySnapshot(
        IReadOnlyList<string> Columns,
        string ReferencedSchema,
        string ReferencedTable,
        IReadOnlyList<string> ReferencedColumns,
        string DeleteAction,
        string UpdateAction,
        bool IsEnabled,
        bool IsTrusted
    );

    private sealed record IndexRow(string Column, bool IsUsable, bool IsFilteredOrPartial);

    private sealed record IndexSnapshot(
        IReadOnlyList<string> Columns,
        bool IsUsable,
        bool IsFilteredOrPartial
    );

    private sealed record TriggerSnapshot(
        bool IsEnabled,
        bool? IsInternal,
        bool? IsRowLevel,
        bool? IsBefore,
        bool? IsInsteadOf,
        bool? IsInsert,
        bool? IsDelete,
        bool? IsUpdate,
        bool? IsTruncate,
        string? OldTransitionTable,
        string? NewTransitionTable,
        string? FunctionSchema,
        string? FunctionName,
        string? Definition
    );

    private sealed record FunctionSnapshot(
        string FunctionSchema,
        string FunctionName,
        string OwnerRole,
        IReadOnlyList<string> Configuration,
        string Definition
    );

    private sealed record QueryParameter(string Name, object Value);

    private interface IValidationIssue
    {
        string Message { get; }
    }

    private sealed record InventoryIssue(DocumentCacheInventoryStatus Status, string Message)
        : IValidationIssue;

    private sealed record EnqueueIssue(DocumentCacheEnqueueTriggerStatus Status, string Message)
        : IValidationIssue;
}
