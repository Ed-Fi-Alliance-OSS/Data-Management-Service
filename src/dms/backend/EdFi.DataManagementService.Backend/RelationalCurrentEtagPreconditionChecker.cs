// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.Profile;

namespace EdFi.DataManagementService.Backend;

internal sealed record RelationalCurrentEtagPreconditionCheckRequest
{
    public RelationalCurrentEtagPreconditionCheckRequest(
        MappingSet mappingSet,
        ResourceReadPlan readPlan,
        RelationalWriteTargetContext.ExistingDocument targetContext,
        WritePrecondition.IfMatch precondition
    )
    {
        MappingSet = mappingSet ?? throw new ArgumentNullException(nameof(mappingSet));
        ReadPlan = readPlan ?? throw new ArgumentNullException(nameof(readPlan));
        TargetContext = targetContext ?? throw new ArgumentNullException(nameof(targetContext));
        Precondition = precondition ?? throw new ArgumentNullException(nameof(precondition));
    }

    public MappingSet MappingSet { get; init; }

    public ResourceReadPlan ReadPlan { get; init; }

    public RelationalWriteTargetContext.ExistingDocument TargetContext { get; init; }

    public WritePrecondition.IfMatch Precondition { get; init; }
}

internal sealed record RelationalCurrentEtagPreconditionCheckResult(
    RelationalWriteCurrentState CurrentState,
    RelationalWriteTargetContext.ExistingDocument TargetContext,
    string CurrentEtag,
    bool IsMatch
);

public sealed record RelationalDeleteEtagPreconditionCheckResult(
    RelationalWriteTargetContext.ExistingDocument TargetContext,
    string CurrentEtag,
    bool IsMatch
);

public interface IRelationalDeleteEtagPreconditionChecker
{
    Task<RelationalDeleteEtagPreconditionCheckResult?> CheckAsync(
        MappingSet mappingSet,
        ResourceReadPlan readPlan,
        RelationalWriteTargetContext.ExistingDocument targetContext,
        WritePrecondition.IfMatch precondition,
        IRelationalWriteSession writeSession,
        CancellationToken cancellationToken = default
    );
}

internal interface IRelationalCurrentEtagPreconditionChecker
{
    Task<RelationalCurrentEtagPreconditionCheckResult?> CheckAsync(
        RelationalCurrentEtagPreconditionCheckRequest request,
        IRelationalWriteSession writeSession,
        CancellationToken cancellationToken = default
    );
}

internal sealed class RelationalCurrentEtagPreconditionChecker(
    IRelationalWriteCurrentStateLoader currentStateLoader,
    IRelationalReadMaterializer readMaterializer,
    IReadableProfileProjector readableProfileProjector
) : IRelationalCurrentEtagPreconditionChecker, IRelationalDeleteEtagPreconditionChecker
{
    private readonly IRelationalWriteCurrentStateLoader _currentStateLoader =
        currentStateLoader ?? throw new ArgumentNullException(nameof(currentStateLoader));

    private readonly IRelationalReadMaterializer _readMaterializer =
        readMaterializer ?? throw new ArgumentNullException(nameof(readMaterializer));

    private readonly IReadableProfileProjector _readableProfileProjector =
        readableProfileProjector ?? throw new ArgumentNullException(nameof(readableProfileProjector));

    public async Task<RelationalDeleteEtagPreconditionCheckResult?> CheckAsync(
        MappingSet mappingSet,
        ResourceReadPlan readPlan,
        RelationalWriteTargetContext.ExistingDocument targetContext,
        WritePrecondition.IfMatch precondition,
        IRelationalWriteSession writeSession,
        CancellationToken cancellationToken = default
    )
    {
        var result = await CheckAsync(
                new RelationalCurrentEtagPreconditionCheckRequest(
                    mappingSet,
                    readPlan,
                    targetContext,
                    precondition
                ),
                writeSession,
                cancellationToken
            )
            .ConfigureAwait(false);

        return result is null
            ? null
            : new RelationalDeleteEtagPreconditionCheckResult(
                result.TargetContext,
                result.CurrentEtag,
                result.IsMatch
            );
    }

    public async Task<RelationalCurrentEtagPreconditionCheckResult?> CheckAsync(
        RelationalCurrentEtagPreconditionCheckRequest request,
        IRelationalWriteSession writeSession,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(writeSession);

        var currentContentVersion = await TryLockAndReadContentVersionAsync(
                request.MappingSet.Key.Dialect,
                request.TargetContext.DocumentId,
                writeSession,
                cancellationToken
            )
            .ConfigureAwait(false);

        if (currentContentVersion is null)
        {
            return null;
        }

        var lockConfirmedTargetContext = request.TargetContext with
        {
            ObservedContentVersion = currentContentVersion.Value,
        };

        var currentState = await _currentStateLoader
            .LoadAsync(
                new RelationalWriteCurrentStateLoadRequest(
                    request.ReadPlan,
                    lockConfirmedTargetContext,
                    // External-response ETag comparison always needs descriptor URI hydration when
                    // the read plan serves descriptor-valued members, regardless of profile use.
                    includeDescriptorProjection: true
                ),
                writeSession,
                cancellationToken
            )
            .ConfigureAwait(false);

        if (currentState is null)
        {
            return null;
        }

        var comparisonSurface = BuildComparisonSurface(request, currentState);
        var currentEtag = RelationalApiMetadataFormatter.FormatEtag(comparisonSurface);
        var refreshedTargetContext = request.TargetContext with
        {
            ObservedContentVersion = currentState.DocumentMetadata.ContentVersion,
        };

        return new RelationalCurrentEtagPreconditionCheckResult(
            currentState,
            refreshedTargetContext,
            currentEtag,
            string.Equals(request.Precondition.Value, currentEtag, StringComparison.Ordinal)
        );
    }

    private static async Task<long?> TryLockAndReadContentVersionAsync(
        SqlDialect dialect,
        long documentId,
        IRelationalWriteSession writeSession,
        CancellationToken cancellationToken
    )
    {
        await using var command = writeSession.CreateCommand(
            RelationalDocumentLockCommandBuilder.BuildContentVersionCommand(dialect, documentId)
        );

        var scalarResult = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        if (scalarResult is null or DBNull)
        {
            return null;
        }

        return Convert.ToInt64(scalarResult, CultureInfo.InvariantCulture);
    }

    private JsonNode BuildComparisonSurface(
        RelationalCurrentEtagPreconditionCheckRequest request,
        RelationalWriteCurrentState currentState
    )
    {
        var currentRepresentation = _readMaterializer.Materialize(
            new RelationalReadMaterializationRequest(
                request.ReadPlan,
                currentState.DocumentMetadata,
                currentState.TableRowsInDependencyOrder,
                currentState.DescriptorRowsInPlanOrder,
                RelationalGetRequestReadMode.ExternalResponse
            )
        );

        var etagProjectionContext = request.Precondition.EtagProjectionContext;

        if (etagProjectionContext is null)
        {
            return currentRepresentation;
        }

        return _readableProfileProjector.Project(
            currentRepresentation,
            etagProjectionContext.ContentTypeDefinition,
            etagProjectionContext.IdentityPropertyNames
        );
    }
}

internal static class RelationalDocumentLockCommandBuilder
{
    private const string DocumentIdParameterName = "@documentId";

    public static RelationalCommand BuildContentVersionCommand(SqlDialect dialect, long documentId)
    {
        return dialect switch
        {
            SqlDialect.Pgsql => new RelationalCommand(
                """
                SELECT
                    document."ContentVersion" AS "ContentVersion"
                FROM dms."Document" document
                WHERE document."DocumentId" = @documentId
                FOR UPDATE
                """,
                [new RelationalParameter(DocumentIdParameterName, documentId)]
            ),
            SqlDialect.Mssql => new RelationalCommand(
                """
                SELECT
                    document.[ContentVersion] AS [ContentVersion]
                FROM [dms].[Document] document WITH (UPDLOCK, HOLDLOCK, ROWLOCK)
                WHERE document.[DocumentId] = @documentId
                """,
                [new RelationalParameter(DocumentIdParameterName, documentId)]
            ),
            _ => throw new ArgumentOutOfRangeException(nameof(dialect), dialect, null),
        };
    }
}
