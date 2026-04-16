// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using EdFi.DataManagementService.Backend.External;

namespace EdFi.DataManagementService.Backend;

/// <summary>
/// Executes a request-scoped relational command and owns the lifetime of the reader exposed to the callback.
/// </summary>
public interface IRelationalCommandExecutor
{
    SqlDialect Dialect { get; }

    Task<TResult> ExecuteReaderAsync<TResult>(
        RelationalCommand command,
        Func<IRelationalCommandReader, CancellationToken, Task<TResult>> readAsync,
        CancellationToken cancellationToken = default
    );
}

/// <summary>
/// Provider-agnostic relational command definition used by write-prerequisite adapters.
/// </summary>
public sealed record RelationalCommand
{
    public RelationalCommand(string commandText, IReadOnlyList<RelationalParameter>? parameters = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandText);

        CommandText = commandText;
        Parameters = parameters ?? [];
    }

    public string CommandText { get; }

    public IReadOnlyList<RelationalParameter> Parameters { get; }
}

/// <summary>
/// Provider-agnostic relational parameter definition with optional provider-specific configuration.
/// </summary>
public sealed record RelationalParameter
{
    public RelationalParameter(string name, object? value, Action<DbParameter>? configureParameter = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        Name = name;
        Value = value;
        ConfigureParameter = configureParameter;
    }

    public string Name { get; }

    public object? Value { get; }

    public Action<DbParameter>? ConfigureParameter { get; }
}

internal static class SessionRelationalCommandFactory
{
    public static DbCommand CreateCommand(
        DbConnection connection,
        DbTransaction transaction,
        RelationalCommand command
    )
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(command);

        var dbCommand = connection.CreateCommand();
        dbCommand.Transaction = transaction;
        dbCommand.CommandText = command.CommandText;

        AddParameters(dbCommand, command.Parameters);

        return dbCommand;
    }

    private static void AddParameters(DbCommand dbCommand, IReadOnlyList<RelationalParameter> parameters)
    {
        foreach (var parameter in parameters)
        {
            var dbParameter = dbCommand.CreateParameter();
            dbParameter.ParameterName = parameter.Name;
            dbParameter.Value = parameter.Value ?? DBNull.Value;
            parameter.ConfigureParameter?.Invoke(dbParameter);
            dbCommand.Parameters.Add(dbParameter);
        }
    }
}

/// <summary>
/// Minimal relational reader surface for shared write-prerequisite materializers.
/// </summary>
public interface IRelationalCommandReader : IAsyncDisposable
{
    Task<bool> ReadAsync(CancellationToken cancellationToken = default);

    Task<bool> NextResultAsync(CancellationToken cancellationToken = default);

    int GetOrdinal(string name);

    T GetFieldValue<T>(int ordinal);

    bool IsDBNull(int ordinal);
}

internal static class RelationalCommandReaderExtensions
{
    public static T GetRequiredFieldValue<T>(this IRelationalCommandReader reader, string columnName)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentException.ThrowIfNullOrWhiteSpace(columnName);

        return reader.GetFieldValue<T>(reader.GetOrdinal(columnName));
    }

    public static T? GetNullableFieldValue<T>(this IRelationalCommandReader reader, string columnName)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentException.ThrowIfNullOrWhiteSpace(columnName);

        var ordinal = reader.GetOrdinal(columnName);
        return reader.IsDBNull(ordinal) ? null : reader.GetFieldValue<T>(ordinal);
    }

    public static DateOnly? GetNullableDateFieldValue(this IRelationalCommandReader reader, string columnName)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentException.ThrowIfNullOrWhiteSpace(columnName);

        var ordinal = reader.GetOrdinal(columnName);
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        // Npgsql returns DateOnly for date columns; SqlClient returns DateTime.
        var value = reader.GetFieldValue<object>(ordinal);
        return value switch
        {
            DateOnly d => d,
            DateTime dt => DateOnly.FromDateTime(dt),
            _ => DateOnly.Parse(value.ToString()!, System.Globalization.CultureInfo.InvariantCulture),
        };
    }
}
