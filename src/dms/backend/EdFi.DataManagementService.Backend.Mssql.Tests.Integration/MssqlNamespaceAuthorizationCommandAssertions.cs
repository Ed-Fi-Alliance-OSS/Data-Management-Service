// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using FluentAssertions;

namespace EdFi.DataManagementService.Backend.Mssql.Tests.Integration;

/// <summary>
/// Assertions over the commands a recorded write session issued, used to prove that a NamespaceBased denial
/// wrote nothing. Because <c>SessionRelationalCommandExecutor.ForSession</c> routes every command issued
/// inside a write session — including every persister statement — through
/// <c>IRelationalWriteSession.CreateCommand</c>, the absence of persistence DML after the authorization
/// statement establishes that no document, root, child, extension, identity, or tracking write occurred.
/// </summary>
/// <remarks>
/// Markers are resilient substrings rather than full SQL equality. Co-batching is tolerated without masking
/// bad ordering: a write can plan a stored check and a proposed check, so the boundary is the <em>final</em>
/// namespace authorization command, and within it the <em>last</em> namespace marker. Persistence is rejected
/// before that command, before that marker inside it, and in any later command. The AUTH1 raise aborts the
/// remainder of its batch, and the caller's before/after state snapshot proves nothing was committed.
/// </remarks>
internal static class MssqlNamespaceAuthorizationCommandAssertions
{
    /// <summary>
    /// The namespace AUTH1 payload discriminator the MSSQL compiler embeds in its
    /// <c>CAST('AUTH1 - ns1|&lt;index&gt;|&lt;kind&gt;' AS INT)</c> raise, which distinguishes a namespace
    /// authorization statement from a relationship one.
    /// </summary>
    private const string NamespaceAuthorizationMarker = "AUTH1 - ns1|";

    private static readonly string[] _persistenceMarkers =
    [
        "INSERT INTO",
        "UPDATE ",
        "DELETE FROM",
        "MERGE ",
    ];

    /// <summary>
    /// A statement whose target is a session-scoped temp table stages a read rather than persisting a row:
    /// current-state hydration materializes its page keyset into <c>[#page]</c>, and that staging travels in
    /// whichever command carries the hydration read. Excluding a temp-table target keeps the persistence
    /// markers pointed at the document, root, child, extension, identity, and tracking relations, which are
    /// always schema-qualified.
    /// </summary>
    private const string TempTableTargetPrefix = "[#";

    public static bool IsNamespaceAuthorizationCommand(string commandText) =>
        commandText.Contains(NamespaceAuthorizationMarker, StringComparison.Ordinal);

    /// <summary>
    /// Asserts that namespace authorization executed and that the operation persisted nothing: no persistence
    /// DML before the final namespace authorization command, none ahead of that command's last namespace
    /// marker, and none in any later command.
    /// </summary>
    public static void AssertNoPersistenceAfterNamespaceAuthorization(
        IReadOnlyList<MssqlRelationalQueryAuthorizationRecordedCommand> commands
    )
    {
        ArgumentNullException.ThrowIfNull(commands);

        // A write can plan both a stored and a proposed check, so the boundary is the last authorization
        // command rather than the first: only the final check can legitimately share a batch with persistence.
        var finalAuthorizationIndex = FindFinalNamespaceAuthorizationCommandIndex(commands);

        finalAuthorizationIndex
            .Should()
            .BeGreaterThanOrEqualTo(
                0,
                "the denial must come from an executed namespace authorization statement, so this assertion "
                    + "cannot pass without one"
            );

        DescribePersistenceCommands(commands.Take(finalAuthorizationIndex))
            .Should()
            .BeEmpty(
                "nothing may be persisted before namespace authorization completes, so no command ahead of "
                    + "the final authorization statement may carry persistence DML"
            );

        var authorizationCommandText = commands[finalAuthorizationIndex].CommandText;
        var lastMarkerIndex = authorizationCommandText.LastIndexOf(
            NamespaceAuthorizationMarker,
            StringComparison.Ordinal
        );

        // Co-batched shape: authorization and persistence arrive as one command. Every persistence occurrence
        // must follow the last namespace marker, so the whole authorization statement — including the CASE
        // branch that raises AUTH1 — precedes the DML and aborts the batch before it can persist.
        FindPersistenceOccurrences(authorizationCommandText)
            .Where(occurrence => occurrence.Index < lastMarkerIndex)
            .Select(occurrence => occurrence.Describe())
            .Should()
            .BeEmpty(
                "every persistence statement co-batched with namespace authorization must follow the "
                    + "authorization statement so the AUTH1 raise aborts the batch before it persists"
            );

        DescribePersistenceCommands(commands.Skip(finalAuthorizationIndex + 1))
            .Should()
            .BeEmpty(
                "a denied namespace authorization must not be followed by any persistence command, so no "
                    + "document, root, child, extension, referential identity, or tracking row can change"
            );
    }

    /// <summary>
    /// Asserts the operation issued no write-session command at all, which is how a planner-terminal denial
    /// (for example the SQL Server namespace prefix cap) must fail closed — before any session opens.
    /// </summary>
    public static void AssertNoCommandsIssued(
        IReadOnlyList<MssqlRelationalQueryAuthorizationRecordedCommand> commands
    )
    {
        ArgumentNullException.ThrowIfNull(commands);

        commands
            .Select(static command => command.CommandText)
            .Should()
            .BeEmpty("a planner-terminal denial must resolve before the write session issues any command");
    }

    private static int FindFinalNamespaceAuthorizationCommandIndex(
        IReadOnlyList<MssqlRelationalQueryAuthorizationRecordedCommand> commands
    )
    {
        for (var index = commands.Count - 1; index >= 0; index--)
        {
            if (IsNamespaceAuthorizationCommand(commands[index].CommandText))
            {
                return index;
            }
        }

        return -1;
    }

    private static IReadOnlyList<string> DescribePersistenceCommands(
        IEnumerable<MssqlRelationalQueryAuthorizationRecordedCommand> commands
    ) =>
        [
            .. commands
                .Where(static command => FindPersistenceOccurrences(command.CommandText).Count != 0)
                .Select(static command => Summarize(command.CommandText)),
        ];

    /// <summary>
    /// Every occurrence of every persistence marker in <paramref name="commandText"/>, ordered by position, so
    /// a batch carrying more than one statement is evaluated in full rather than by its first match alone.
    /// </summary>
    private static IReadOnlyList<PersistenceOccurrence> FindPersistenceOccurrences(string commandText)
    {
        List<PersistenceOccurrence> occurrences = [];

        foreach (var marker in _persistenceMarkers)
        {
            var searchIndex = commandText.IndexOf(marker, StringComparison.Ordinal);

            while (searchIndex >= 0)
            {
                if (!TargetsTempTable(commandText, marker, searchIndex))
                {
                    occurrences.Add(new PersistenceOccurrence(marker, searchIndex));
                }

                searchIndex = commandText.IndexOf(
                    marker,
                    searchIndex + marker.Length,
                    StringComparison.Ordinal
                );
            }
        }

        return [.. occurrences.OrderBy(static occurrence => occurrence.Index)];
    }

    private static bool TargetsTempTable(string commandText, string marker, int markerIndex) =>
        commandText
            .AsSpan(markerIndex + marker.Length)
            .TrimStart()
            .StartsWith(TempTableTargetPrefix, StringComparison.Ordinal);

    private static string Summarize(string commandText) =>
        commandText.Length <= SummarizedCommandLength
            ? commandText
            : commandText[..SummarizedCommandLength] + "…";

    private const int SummarizedCommandLength = 240;

    private sealed record PersistenceOccurrence(string Marker, int Index)
    {
        public string Describe() => $"'{Marker}' at index {Index}";
    }
}
