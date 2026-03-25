// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;

namespace EdFi.DataManagementService.Backend;

/// <summary>
/// Executes a request-scoped relational command and owns the lifetime of the reader exposed to the callback.
/// </summary>
internal interface IRelationalCommandExecutor
{
    Task<TResult> ExecuteReaderAsync<TResult>(
        RelationalCommand command,
        Func<IRelationalCommandReader, CancellationToken, Task<TResult>> readAsync,
        CancellationToken cancellationToken = default
    );
}

/// <summary>
/// Provider-agnostic relational command definition used by write-prerequisite adapters.
/// </summary>
internal sealed record RelationalCommand
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
internal sealed record RelationalParameter
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

/// <summary>
/// Minimal relational reader surface for shared write-prerequisite materializers.
/// </summary>
internal interface IRelationalCommandReader : IAsyncDisposable
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
}
