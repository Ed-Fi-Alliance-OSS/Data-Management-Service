// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Ddl;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.DocumentCache.Cdc;
using Microsoft.Extensions.Logging;
using CoreCdc = EdFi.DataManagementService.Core.DocumentCache.Cdc;

namespace EdFi.DataManagementService.Backend.Cdc.Control;

/// <summary>
/// What one provider-artifact teardown removes: the governed names come from the binding's own
/// inventory, and the source tables are the deployed schema's, which SQL Server needs to name the
/// table each capture instance was created for.
/// </summary>
public sealed record CdcProviderArtifactTeardownRequest(
    CdcArtifactInventory Inventory,
    IReadOnlyList<CdcSourceTableInventory> SourceInventory,
    ICdcProviderDatabaseExecutor Executor
);

/// <summary>
/// Removes the provider-side capture artifacts one binding generation governs, reporting each as the
/// governed artifact it is.
/// </summary>
/// <remarks>
/// Only artifacts the binding's own inventory names are touched: provider CDC is never disabled at the
/// database level, and no artifact of another generation is in reach. An artifact that is already gone
/// is reported as not found rather than as a failure, so a retried retirement stays idempotent, while a
/// provider that refuses a removal propagates — a partial teardown must leave the binding record intact.
/// </remarks>
public interface ICdcProviderArtifactTeardown
{
    CoreCdc.CdcProvider Provider { get; }

    Task<IReadOnlyList<CdcGovernedArtifact>> DeleteAsync(
        CdcProviderArtifactTeardownRequest request,
        CancellationToken cancellationToken = default
    );
}

internal sealed class CdcProviderArtifactTeardown(
    CoreCdc.CdcProvider provider,
    ILogger<CdcProviderArtifactTeardown> logger
) : ICdcProviderArtifactTeardown
{
    public CoreCdc.CdcProvider Provider => provider;

    public async Task<IReadOnlyList<CdcGovernedArtifact>> DeleteAsync(
        CdcProviderArtifactTeardownRequest request,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Inventory);
        ArgumentNullException.ThrowIfNull(request.SourceInventory);
        ArgumentNullException.ThrowIfNull(request.Executor);

        IReadOnlyList<CdcGovernedArtifact> artifacts =
            provider == CoreCdc.CdcProvider.Postgresql
                ? await DeletePostgresqlAsync(request, cancellationToken).ConfigureAwait(false)
                : await DeleteSqlServerAsync(request, cancellationToken).ConfigureAwait(false);

        logger.LogDebug(
            "CDC retirement removed {ArtifactCount} provider capture artifacts for generation {Generation}.",
            artifacts.Count,
            request.Inventory.Generation
        );

        return artifacts;
    }

    /// <summary>
    /// The publication first and then the slot: dropping the publication stops the slot from decoding
    /// further changes, and a slot the connector still holds open refuses its own removal rather than
    /// being reported gone.
    /// </summary>
    private static async Task<IReadOnlyList<CdcGovernedArtifact>> DeletePostgresqlAsync(
        CdcProviderArtifactTeardownRequest request,
        CancellationToken cancellationToken
    )
    {
        List<CdcGovernedArtifact> artifacts = [];
        string publicationName = Required(
            request.Inventory.PostgresqlPublicationName,
            nameof(CdcArtifactInventory.PostgresqlPublicationName)
        );
        string logicalSlotName = Required(
            request.Inventory.PostgresqlLogicalSlotName,
            nameof(CdcArtifactInventory.PostgresqlLogicalSlotName)
        );

        await DropAsync(
            request.Executor,
            CdcGovernedArtifactKind.PostgresqlPublication,
            publicationName,
            RenderPublicationExistenceCommandText(publicationName),
            $"DROP PUBLICATION {Quote(SqlDialect.Pgsql, publicationName)};",
            artifacts,
            cancellationToken
        );
        await DropAsync(
            request.Executor,
            CdcGovernedArtifactKind.PostgresqlLogicalSlot,
            logicalSlotName,
            RenderLogicalSlotExistenceCommandText(logicalSlotName),
            $"SELECT pg_catalog.pg_drop_replication_slot({Literal(logicalSlotName)});",
            artifacts,
            cancellationToken
        );

        return artifacts;
    }

    /// <summary>
    /// The capture instances first and then the gating role: the role gates access to the change tables
    /// the capture instances own, so it is removed once nothing is left for it to gate.
    /// </summary>
    private static async Task<IReadOnlyList<CdcGovernedArtifact>> DeleteSqlServerAsync(
        CdcProviderArtifactTeardownRequest request,
        CancellationToken cancellationToken
    )
    {
        List<CdcGovernedArtifact> artifacts = [];

        foreach (
            (
                CdcGovernedArtifactKind artifactKind,
                CdcSourceTableKind tableKind,
                string captureInstanceName
            ) in SqlServerCaptureInstances(request.Inventory)
        )
        {
            await DropAsync(
                request.Executor,
                artifactKind,
                captureInstanceName,
                RenderCaptureInstanceExistenceCommandText(captureInstanceName),
                RenderDisableCaptureInstanceCommandText(
                    SourceTable(request.SourceInventory, tableKind),
                    captureInstanceName
                ),
                artifacts,
                cancellationToken
            );
        }

        string gatingRoleName = Required(
            request.Inventory.SqlServerCdcGatingRoleName,
            nameof(CdcArtifactInventory.SqlServerCdcGatingRoleName)
        );

        await DropAsync(
            request.Executor,
            CdcGovernedArtifactKind.SqlServerCdcGatingRole,
            gatingRoleName,
            RenderDatabaseRoleExistenceCommandText(gatingRoleName),
            $"DROP ROLE {Quote(SqlDialect.Mssql, gatingRoleName)};",
            artifacts,
            cancellationToken
        );

        return artifacts;
    }

    /// <summary>
    /// Removes one artifact after establishing whether it is there, so the proof reports what this
    /// retirement actually did rather than what it attempted.
    /// </summary>
    private static async Task DropAsync(
        ICdcProviderDatabaseExecutor executor,
        CdcGovernedArtifactKind artifactKind,
        string artifactName,
        string existenceCommandText,
        string dropCommandText,
        List<CdcGovernedArtifact> artifacts,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        IReadOnlyList<IReadOnlyDictionary<string, string?>> rows = await executor
            .QueryAsync(existenceCommandText, cancellationToken)
            .ConfigureAwait(false);
        bool present = rows.Count > 0;

        if (present)
        {
            await executor.ExecuteNonQueryAsync(dropCommandText, cancellationToken).ConfigureAwait(false);
        }

        artifacts.Add(
            new CdcGovernedArtifact(
                artifactKind,
                artifactName,
                present ? CdcCleanupState.Deleted : CdcCleanupState.NotFound,
                present
                    ? "the provider reported the governed artifact and it was removed"
                    : "the provider reported no such governed artifact"
            )
        );
    }

    private static IEnumerable<(
        CdcGovernedArtifactKind ArtifactKind,
        CdcSourceTableKind TableKind,
        string CaptureInstanceName
    )> SqlServerCaptureInstances(CdcArtifactInventory inventory)
    {
        yield return (
            CdcGovernedArtifactKind.SqlServerCaptureInstanceDocument,
            CdcSourceTableKind.Document,
            Required(
                inventory.SqlServerCaptureInstanceDocumentName,
                nameof(CdcArtifactInventory.SqlServerCaptureInstanceDocumentName)
            )
        );
        yield return (
            CdcGovernedArtifactKind.SqlServerCaptureInstanceDocumentCache,
            CdcSourceTableKind.DocumentCache,
            Required(
                inventory.SqlServerCaptureInstanceDocumentCacheName,
                nameof(CdcArtifactInventory.SqlServerCaptureInstanceDocumentCacheName)
            )
        );
        yield return (
            CdcGovernedArtifactKind.SqlServerCaptureInstanceCdcHeartbeat,
            CdcSourceTableKind.CdcHeartbeat,
            Required(
                inventory.SqlServerCaptureInstanceCdcHeartbeatName,
                nameof(CdcArtifactInventory.SqlServerCaptureInstanceCdcHeartbeatName)
            )
        );
    }

    /// <summary>
    /// The deployed source table one capture instance was created for. SQL Server names the table
    /// rather than the capture instance when it disables one, and the control plane never guesses it.
    /// </summary>
    private static DbTableName SourceTable(
        IReadOnlyList<CdcSourceTableInventory> sourceInventory,
        CdcSourceTableKind tableKind
    ) =>
        sourceInventory.FirstOrDefault(source => source.TableKind == tableKind)?.TableName
        ?? throw new InvalidOperationException(
            $"CDC retirement requires the deployed source table for `{tableKind}`."
        );

    private static string Required(string? value, string name) =>
        value
        ?? throw new InvalidOperationException(
            $"CDC retirement requires the binding artifact inventory to name `{name}`."
        );

    internal static string RenderPublicationExistenceCommandText(string publicationName) =>
        "SELECT pubname\n"
        + "FROM pg_catalog.pg_publication\n"
        + $"WHERE pubname = {Literal(publicationName)};";

    internal static string RenderLogicalSlotExistenceCommandText(string logicalSlotName) =>
        "SELECT slot_name\n"
        + "FROM pg_catalog.pg_replication_slots\n"
        + $"WHERE slot_name = {Literal(logicalSlotName)};";

    internal static string RenderCaptureInstanceExistenceCommandText(string captureInstanceName) =>
        "SELECT capture_instance\n"
        + "FROM cdc.change_tables\n"
        + $"WHERE capture_instance = {Literal(captureInstanceName)};";

    internal static string RenderDatabaseRoleExistenceCommandText(string roleName) =>
        "SELECT name\n"
        + "FROM sys.database_principals\n"
        + $"WHERE name = {Literal(roleName)} AND type = 'R';";

    internal static string RenderDisableCaptureInstanceCommandText(
        DbTableName sourceTable,
        string captureInstanceName
    ) =>
        "EXEC sys.sp_cdc_disable_table\n"
        + $"    @source_schema = N{Literal(sourceTable.Schema.Value)},\n"
        + $"    @source_name = N{Literal(sourceTable.Name)},\n"
        + $"    @capture_instance = N{Literal(captureInstanceName)};";

    private static string Quote(SqlDialect dialect, string identifier) =>
        SqlIdentifierQuoter.QuoteIdentifier(dialect, identifier);

    private static string Literal(string value) => $"'{value.Replace("'", "''")}'";
}
