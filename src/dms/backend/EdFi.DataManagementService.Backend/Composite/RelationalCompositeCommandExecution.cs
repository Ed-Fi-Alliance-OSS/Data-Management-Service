// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.Globalization;

namespace EdFi.DataManagementService.Backend.Composite;

/// <summary>
/// Executes a composite command and decodes its result sets in declared order, attributing any provider
/// failure to the logical statement that raised it.
/// </summary>
/// <remarks>
/// <para>
/// Composite commands execute through a reader and step result sets explicitly. Executing them without a
/// reader consumes every statement and loses all position information, so a failure could not be attributed
/// at all.
/// </para>
/// <para>
/// Attribution rests on the builder's one-result-set-per-statement invariant. A failure raised while opening
/// the reader can only be statement 0, because statement 0 always produces a result set and the provider
/// therefore never scans past it. A failure raised while advancing to result set <c>k</c> is statement
/// <c>k</c>. Both providers process batch statements strictly in order and stream results, so neither
/// reorders.
/// </para>
/// <para>
/// The provider exception is never wrapped or replaced. It propagates with its SQLSTATE or error number,
/// constraint name, and any AUTH1 payload intact, so the existing classifier, constraint resolver, and AUTH1
/// dispatcher remain authoritative and a previously unmapped failure still maps the same way. The statement
/// attribution is diagnostic metadata read from <see cref="Failure"/> after the throw.
/// </para>
/// </remarks>
internal sealed class RelationalCompositeCommandExecution
{
    /// <summary>
    /// Where the last execution failed, or <see langword="null"/> when it succeeded. Populated before the
    /// original exception propagates.
    /// </summary>
    public RelationalCompositeFailureContext? Failure { get; private set; }

    public async Task<IReadOnlyList<RelationalCompositeStatementOutcome>> ExecuteAsync(
        IRelationalWriteSession writeSession,
        RelationalCompositeCommand compositeCommand,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(writeSession);
        ArgumentNullException.ThrowIfNull(compositeCommand);

        Failure = null;

        var statements = compositeCommand.StatementsInOrder;
        List<RelationalCompositeStatementOutcome> outcomes = new(statements.Count);

        await using var dbCommand = writeSession.CreateCommand(compositeCommand.Command);

        DbDataReader reader;

        try
        {
            reader = await dbCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // Only statement 0 can surface here: it always produces a result set, so the provider never
            // scans past it looking for one.
            Failure = BuildFailure(statements, 0, RelationalCompositeFailureStage.OpeningReader, exception);
            throw;
        }

        await using (reader.ConfigureAwait(false))
        {
            for (var ordinal = 0; ordinal < statements.Count; ordinal++)
            {
                if (ordinal > 0)
                {
                    bool hasNextResult;

                    try
                    {
                        hasNextResult = await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception exception)
                    {
                        Failure = BuildFailure(
                            statements,
                            ordinal,
                            RelationalCompositeFailureStage.AdvancingResultSet,
                            exception
                        );
                        throw;
                    }

                    if (!hasNextResult)
                    {
                        throw new InvalidOperationException(
                            $"Composite command declared {statements.Count} logical statements but the "
                                + $"provider produced no result set for ordinal {ordinal} "
                                + $"('{statements[ordinal].Label}'). Every logical statement must emit "
                                + "exactly one result set."
                        );
                    }
                }

                var statement = statements[ordinal];

                try
                {
                    outcomes.Add(
                        new RelationalCompositeStatementOutcome(
                            ordinal,
                            statement.Label,
                            await DecodeAsync(reader, statement, cancellationToken).ConfigureAwait(false)
                        )
                    );
                }
                catch (Exception exception) when (exception is not InvalidOperationException)
                {
                    Failure = BuildFailure(
                        statements,
                        ordinal,
                        RelationalCompositeFailureStage.ReadingRows,
                        exception
                    );
                    throw;
                }
            }
        }

        return outcomes;
    }

    private static Task<object?> DecodeAsync(
        DbDataReader reader,
        RelationalCompositeStatement statement,
        CancellationToken cancellationToken
    ) =>
        statement.ResultShape switch
        {
            RelationalCompositeResultShape.Sentinel => DecodeSentinelAsync(
                reader,
                statement,
                cancellationToken
            ),
            RelationalCompositeResultShape.Scalar => DecodeScalarAsync(reader, statement, cancellationToken),
            RelationalCompositeResultShape.Rows => DecodeRowsAsync(reader, statement, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(
                nameof(statement),
                statement.ResultShape,
                "Unsupported composite result shape."
            ),
        };

    private static async Task<object?> DecodeSentinelAsync(
        DbDataReader reader,
        RelationalCompositeStatement statement,
        CancellationToken cancellationToken
    )
    {
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                $"Sentinel for statement {statement.Ordinal} ('{statement.Label}') returned no row."
            );
        }

        var echoed = Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture);

        // A mismatch means the emitted statement order and the declared order disagree, which would
        // silently misattribute every later failure.
        if (echoed != statement.Ordinal)
        {
            throw new InvalidOperationException(
                $"Sentinel for statement {statement.Ordinal} ('{statement.Label}') echoed {echoed}. "
                    + "The emitted command and the declared statement order disagree."
            );
        }

        return echoed;
    }

    private static async Task<object?> DecodeScalarAsync(
        DbDataReader reader,
        RelationalCompositeStatement statement,
        CancellationToken cancellationToken
    )
    {
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var value = await reader.IsDBNullAsync(0, cancellationToken).ConfigureAwait(false)
            ? null
            : reader.GetValue(0);

        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                $"Statement {statement.Ordinal} ('{statement.Label}') declared a scalar result but returned "
                    + "more than one row."
            );
        }

        return value;
    }

    private static async Task<object?> DecodeRowsAsync(
        DbDataReader reader,
        RelationalCompositeStatement statement,
        CancellationToken cancellationToken
    )
    {
        if (statement.Read is { } read)
        {
            return await read(reader, cancellationToken).ConfigureAwait(false);
        }

        var rowCount = 0;

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rowCount++;
        }

        return rowCount;
    }

    /// <summary>
    /// Builds the attribution for a failure. A failure carrying no provider error — a connection-level fault
    /// or cancellation — is not attributable to a statement and gets a null ordinal rather than a fabricated
    /// zero.
    /// </summary>
    private static RelationalCompositeFailureContext BuildFailure(
        IReadOnlyList<RelationalCompositeStatement> statements,
        int ordinal,
        RelationalCompositeFailureStage stage,
        Exception exception
    )
    {
        if (exception is not DbException || exception is OperationCanceledException)
        {
            return new RelationalCompositeFailureContext(
                null,
                null,
                RelationalCompositeFailureStage.Unattributable
            );
        }

        return ordinal >= 0 && ordinal < statements.Count
            ? new RelationalCompositeFailureContext(ordinal, statements[ordinal].Label, stage)
            : new RelationalCompositeFailureContext(
                null,
                null,
                RelationalCompositeFailureStage.Unattributable
            );
    }
}
