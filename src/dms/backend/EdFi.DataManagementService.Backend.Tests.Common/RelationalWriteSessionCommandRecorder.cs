// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.Globalization;
using System.Text;
using FluentAssertions;

namespace EdFi.DataManagementService.Backend.Tests.Common;

/// <summary>
/// One command observed on a relational write session, in the order it was created.
/// </summary>
/// <param name="Ordinal">Zero-based position in the session's command stream.</param>
/// <param name="CommandText">The command text passed to the session.</param>
/// <param name="Parameters">
/// Parameters declared on the <see cref="RelationalCommand"/>. Callers that add parameters to the
/// returned <see cref="DbCommand"/> after creation (the hydration batch does this, because keyset
/// parameters are bound by the plan layer) are recorded with an empty list; the command text is
/// still complete.
/// </param>
public sealed record RecordedSessionCommand(
    int Ordinal,
    string CommandText,
    IReadOnlyList<RecordedSessionCommandParameter> Parameters
);

/// <param name="Name">Parameter name as declared on the relational command.</param>
/// <param name="Value">Parameter value as declared on the relational command.</param>
public sealed record RecordedSessionCommandParameter(string Name, object? Value);

/// <summary>
/// Records the complete ordered command stream issued inside a relational write session, plus the
/// transaction boundary events, so tests can assert exact command counts.
/// </summary>
/// <remarks>
/// <para>
/// Every in-session command is created through
/// <see cref="IRelationalWriteSession.CreateCommand(RelationalCommand)"/>, including reference
/// resolution, the in-session POST target lookup, and current-state hydration, so wrapping that one
/// method observes the whole stream. Consumers that take an
/// <see cref="IRelationalCommandExecutor"/> are covered too, because the default
/// <see cref="IRelationalWriteSession.CreateCommandExecutor"/> routes back through
/// <c>CreateCommand</c> on whichever instance it is invoked against — which is the decorator.
/// </para>
/// <para>
/// BEGIN and COMMIT are counted separately from commands and must never be folded into a command
/// count. BEGIN is attributed to session creation, which is where the provider opens the
/// transaction.
/// </para>
/// </remarks>
public sealed class RelationalWriteSessionCommandRecorder
{
    private readonly List<RecordedSessionCommand> _commands = [];

    /// <summary>Commands in creation order.</summary>
    public IReadOnlyList<RecordedSessionCommand> Commands => _commands;

    /// <summary>Number of commands created on the session.</summary>
    public int CommandCount => _commands.Count;

    /// <summary>Sessions created, which is where the provider begins the transaction.</summary>
    public int BeginCount { get; private set; }

    /// <summary>Commits requested on the session.</summary>
    public int CommitCount { get; private set; }

    /// <summary>Rollbacks requested on the session.</summary>
    public int RollbackCount { get; private set; }

    public void Reset()
    {
        _commands.Clear();
        BeginCount = 0;
        CommitCount = 0;
        RollbackCount = 0;
    }

    public void RecordBegin() => BeginCount++;

    public void RecordCommit() => CommitCount++;

    public void RecordRollback() => RollbackCount++;

    public void RecordCommand(RelationalCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);

        _commands.Add(
            new RecordedSessionCommand(
                _commands.Count,
                command.CommandText,
                [
                    .. command.Parameters.Select(parameter => new RecordedSessionCommandParameter(
                        parameter.Name,
                        parameter.Value
                    )),
                ]
            )
        );
    }

    /// <summary>
    /// Asserts the exact number of commands issued on the session. An exact assertion is deliberate:
    /// an upper bound would not catch a regression back to a per-table N+1 pattern, which is the
    /// specific failure this recorder exists to detect.
    /// </summary>
    public void ShouldHaveCommandCount(int expectedCommandCount) =>
        CommandCount.Should().Be(expectedCommandCount, Describe());

    /// <summary>
    /// Asserts BEGIN/COMMIT/ROLLBACK counts, which are tracked separately from the command count.
    /// </summary>
    public void ShouldHaveTransactionBoundary(
        int expectedBeginCount,
        int expectedCommitCount,
        int expectedRollbackCount
    )
    {
        BeginCount.Should().Be(expectedBeginCount, Describe());
        CommitCount.Should().Be(expectedCommitCount, Describe());
        RollbackCount.Should().Be(expectedRollbackCount, Describe());
    }

    /// <summary>
    /// Renders the observed stream for assertion failure messages. Without this, an exact-count
    /// failure reports only two integers and gives no way to see which command appeared or vanished.
    /// </summary>
    public string Describe()
    {
        StringBuilder builder = new();
        builder.AppendLine(
            CultureInfo.InvariantCulture,
            $"observed {_commands.Count} session command(s); begin={BeginCount}, commit={CommitCount}, rollback={RollbackCount}:"
        );

        foreach (var command in _commands)
        {
            builder.AppendLine(
                CultureInfo.InvariantCulture,
                $"  [{command.Ordinal}] {Summarize(command.CommandText)}"
            );
        }

        return builder.ToString();
    }

    /// <summary>
    /// Collapses a command to a single readable line. Hydration batches are hundreds of lines, so an
    /// unsummarized dump would bury the difference the assertion is trying to show.
    /// </summary>
    private static string Summarize(string commandText)
    {
        var firstMeaningfulLine = commandText
            .Split('\n')
            .Select(line => line.Trim())
            .FirstOrDefault(line => line.Length > 0 && !line.StartsWith("--", StringComparison.Ordinal));

        var summary = firstMeaningfulLine ?? commandText.Trim();

        return summary.Length <= 120 ? summary : string.Concat(summary.AsSpan(0, 117), "...");
    }
}

/// <summary>
/// Wraps a write session factory so BEGIN is counted and every session it produces is recorded.
/// </summary>
public sealed class RecordingRelationalWriteSessionFactory(
    IRelationalWriteSessionFactory innerFactory,
    RelationalWriteSessionCommandRecorder recorder
) : IRelationalWriteSessionFactory
{
    private readonly IRelationalWriteSessionFactory _innerFactory =
        innerFactory ?? throw new ArgumentNullException(nameof(innerFactory));

    private readonly RelationalWriteSessionCommandRecorder _recorder =
        recorder ?? throw new ArgumentNullException(nameof(recorder));

    public async Task<IRelationalWriteSession> CreateAsync(CancellationToken cancellationToken = default)
    {
        var innerSession = await _innerFactory.CreateAsync(cancellationToken).ConfigureAwait(false);
        _recorder.RecordBegin();

        return new RecordingRelationalWriteSession(innerSession, _recorder);
    }
}

/// <summary>
/// Records every command created on a write session. Intentionally does not override
/// <see cref="IRelationalWriteSession.CreateCommandExecutor"/>: the default implementation binds to
/// this decorator, so executor-based consumers route back through <see cref="CreateCommand"/> here.
/// </summary>
public sealed class RecordingRelationalWriteSession(
    IRelationalWriteSession innerSession,
    RelationalWriteSessionCommandRecorder recorder
) : IRelationalWriteSession
{
    private readonly IRelationalWriteSession _innerSession =
        innerSession ?? throw new ArgumentNullException(nameof(innerSession));

    private readonly RelationalWriteSessionCommandRecorder _recorder =
        recorder ?? throw new ArgumentNullException(nameof(recorder));

    public DbConnection Connection => _innerSession.Connection;

    public DbTransaction Transaction => _innerSession.Transaction;

    public DbCommand CreateCommand(RelationalCommand command)
    {
        _recorder.RecordCommand(command);
        return _innerSession.CreateCommand(command);
    }

    public Task CommitAsync(CancellationToken cancellationToken = default)
    {
        _recorder.RecordCommit();
        return _innerSession.CommitAsync(cancellationToken);
    }

    public Task RollbackAsync(CancellationToken cancellationToken = default)
    {
        _recorder.RecordRollback();
        return _innerSession.RollbackAsync(cancellationToken);
    }

    public ValueTask DisposeAsync() => _innerSession.DisposeAsync();
}
