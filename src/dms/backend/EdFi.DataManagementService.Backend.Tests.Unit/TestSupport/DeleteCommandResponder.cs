// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Tests.Unit.Composite;

namespace EdFi.DataManagementService.Backend.Tests.Unit.TestSupport;

/// <summary>
/// Answers the relational DELETE's raw commands with the result-set stream each one declares.
/// </summary>
/// <remarks>
/// <para>
/// It dispatches on the received SQL rather than on a call ordinal, because the number of commands a delete
/// issues depends on the arrangement — a specific-tag <c>If-Match</c> or a table-valued claim binding splits
/// the deletes into their own command. Dispatching on what the phase actually emitted keeps a fixture's
/// arrangement independent of which transport its inputs select.
/// </para>
/// <para>
/// Every field is read when a command arrives, not when it is set, so a fixture may configure the target, the
/// authorization shape, and the delete outcome in any order.
/// </para>
/// </remarks>
internal sealed class DeleteCommandResponder
{
    /// <summary>Whether the capture statement observes a target row.</summary>
    public bool TargetExists { get; set; }

    public long DocumentId { get; set; } = 345L;

    public Guid DocumentUuid { get; set; } = Guid.Parse("cccccccc-1111-2222-3333-dddddddddddd");

    public long ContentVersion { get; set; } = 42L;

    /// <summary>How many stored namespace checks the command carries; each owns one result set.</summary>
    public int NamespaceCheckCount { get; set; }

    /// <summary>Whether a stored relationship check is co-batched, and what it decided.</summary>
    public int? RelationshipAuthorizationResult { get; set; }

    /// <summary>Whether the <c>dms.Document</c> delete returns a row.</summary>
    public bool Deleted { get; set; }

    /// <summary>Raised instead of answering the command that carries the deletes.</summary>
    public Exception? DeleteExceptionToThrow { get; set; }

    /// <summary>Raised instead of answering the command that captures the target.</summary>
    public Exception? CaptureExceptionToThrow { get; set; }

    /// <summary>Every command this responder classified as carrying the deletes.</summary>
    public List<RelationalCommand> DeleteCommands { get; } = [];

    /// <summary>Invoked with each command that carries the deletes, before it is answered.</summary>
    public Action<RelationalCommand>? OnDeleteCommand { get; set; }

    public object Respond(RelationalCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);

        var capturesTarget = CapturesTarget(command.CommandText);
        var deletes = command.CommandText.Contains("DELETE FROM", StringComparison.Ordinal);

        if (deletes)
        {
            DeleteCommands.Add(command);
            OnDeleteCommand?.Invoke(command);
        }

        if (capturesTarget && CaptureExceptionToThrow is not null)
        {
            throw CaptureExceptionToThrow;
        }

        if (deletes && DeleteExceptionToThrow is not null)
        {
            throw DeleteExceptionToThrow;
        }

        List<IReadOnlyList<object?[]>> resultSets = [];
        List<string[]> columnNames = [];

        if (capturesTarget)
        {
            resultSets.Add(
                TargetExists
                    ?
                    [
                        [DocumentId, ContentVersion, DocumentUuid, DocumentId.ToString()],
                    ]
                    :
                    [
                        [null, null, null, ""],
                    ]
            );
            columnNames.Add(["DocumentId", "ContentVersion", "DocumentUuid", "CapturedToken"]);

            for (var check = 0; check < NamespaceCheckCount; check++)
            {
                resultSets.Add([
                    [1],
                ]);
                columnNames.Add(["Authorized"]);
            }
        }

        if (RelationshipAuthorizationResult is { } authorizationResult && ContainsRelationshipCheck(command))
        {
            resultSets.Add([
                [authorizationResult, ContentVersion],
            ]);
            columnNames.Add(["AuthorizationResult", "ContentVersion"]);
        }

        if (deletes)
        {
            // The co-batched root delete carries a sentinel echoing its own ordinal; the ordered-segment
            // delete command is a plain two-statement command whose only rows are the document delete's.
            if (capturesTarget)
            {
                resultSets.Add([
                    [resultSets.Count],
                ]);
                columnNames.Add(["LogicalStatementOrdinal"]);
            }

            resultSets.Add(
                Deleted
                    ?
                    [
                        [DocumentId],
                    ]
                    : []
            );
            columnNames.Add(["DocumentId"]);
        }

        return new ScriptedDbDataReader(resultSets, columnNames);
    }

    /// <summary>
    /// Whether the command holds the capture statement, identified by the row lock only it takes.
    /// </summary>
    private static bool CapturesTarget(string commandText) =>
        commandText.Contains("FOR UPDATE", StringComparison.Ordinal)
        || commandText.Contains("UPDLOCK", StringComparison.Ordinal);

    /// <summary>
    /// Whether the command carries the relationship check, identified by the column its row projects. A
    /// co-batched check rides the capture command; a table-valued claim binding runs as its own command.
    /// </summary>
    private static bool ContainsRelationshipCheck(RelationalCommand command) =>
        command.CommandText.Contains("AuthorizationResult", StringComparison.Ordinal);
}
