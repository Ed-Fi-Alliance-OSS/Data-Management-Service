// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;

namespace EdFi.DataManagementService.Backend.Tests.Unit.Composite;

/// <summary>
/// A <see cref="DbDataReader"/> over scripted result sets, each a list of rows, each row a list of cell
/// values with optional column names. Lets decoder tests drive exact multi-result-set streams without a
/// provider.
/// </summary>
internal sealed class ScriptedDbDataReader(
    IReadOnlyList<IReadOnlyList<object?[]>> resultSets,
    IReadOnlyList<string[]>? columnNamesPerResultSet = null
) : DbDataReader
{
    private int _resultSetIndex;
    private int _rowIndex = -1;

    private IReadOnlyList<object?[]> CurrentResultSet => resultSets[_resultSetIndex];

    private object?[] CurrentRow => CurrentResultSet[_rowIndex];

    public override int FieldCount =>
        CurrentResultSet.Count > 0
            ? CurrentResultSet[0].Length
            : (columnNamesPerResultSet?[_resultSetIndex].Length ?? 0);

    public override bool HasRows => CurrentResultSet.Count > 0;
    public override bool IsClosed => false;
    public override int RecordsAffected => 0;
    public override int Depth => 0;
    public override object this[int ordinal] => GetValue(ordinal);
    public override object this[string name] => GetValue(GetOrdinal(name));

    public override Task<bool> ReadAsync(CancellationToken cancellationToken)
    {
        if (_rowIndex + 1 >= CurrentResultSet.Count)
        {
            return Task.FromResult(false);
        }

        _rowIndex++;
        return Task.FromResult(true);
    }

    public override Task<bool> NextResultAsync(CancellationToken cancellationToken)
    {
        if (_resultSetIndex + 1 >= resultSets.Count)
        {
            return Task.FromResult(false);
        }

        _resultSetIndex++;
        _rowIndex = -1;
        return Task.FromResult(true);
    }

    public override bool Read() => ReadAsync(CancellationToken.None).GetAwaiter().GetResult();

    public override bool NextResult() => NextResultAsync(CancellationToken.None).GetAwaiter().GetResult();

    public override object GetValue(int ordinal) => CurrentRow[ordinal] ?? DBNull.Value;

    public override bool IsDBNull(int ordinal) => CurrentRow[ordinal] is null or DBNull;

    public override Task<bool> IsDBNullAsync(int ordinal, CancellationToken cancellationToken) =>
        Task.FromResult(IsDBNull(ordinal));

    public override string GetName(int ordinal) =>
        columnNamesPerResultSet?[_resultSetIndex][ordinal] ?? $"Column{ordinal}";

    public override int GetOrdinal(string name)
    {
        var names = columnNamesPerResultSet?[_resultSetIndex];

        if (names is null)
        {
            throw new NotSupportedException("This scripted result set declares no column names.");
        }

        var ordinal = Array.FindIndex(names, n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));

        return ordinal >= 0 ? ordinal : throw new IndexOutOfRangeException(name);
    }

    public override T GetFieldValue<T>(int ordinal) => (T)CurrentRow[ordinal]!;

    public override bool GetBoolean(int ordinal) => GetFieldValue<bool>(ordinal);

    public override byte GetByte(int ordinal) => GetFieldValue<byte>(ordinal);

    public override long GetBytes(
        int ordinal,
        long dataOffset,
        byte[]? buffer,
        int bufferOffset,
        int length
    ) => throw new NotSupportedException();

    public override char GetChar(int ordinal) => GetFieldValue<char>(ordinal);

    public override long GetChars(
        int ordinal,
        long dataOffset,
        char[]? buffer,
        int bufferOffset,
        int length
    ) => throw new NotSupportedException();

    public override string GetDataTypeName(int ordinal) => GetFieldType(ordinal).Name;

    public override DateTime GetDateTime(int ordinal) => GetFieldValue<DateTime>(ordinal);

    public override decimal GetDecimal(int ordinal) => GetFieldValue<decimal>(ordinal);

    public override double GetDouble(int ordinal) => GetFieldValue<double>(ordinal);

    public override System.Collections.IEnumerator GetEnumerator() => throw new NotSupportedException();

    public override Type GetFieldType(int ordinal) => CurrentRow[ordinal]?.GetType() ?? typeof(object);

    public override float GetFloat(int ordinal) => GetFieldValue<float>(ordinal);

    public override Guid GetGuid(int ordinal) => GetFieldValue<Guid>(ordinal);

    public override short GetInt16(int ordinal) => GetFieldValue<short>(ordinal);

    public override int GetInt32(int ordinal) => GetFieldValue<int>(ordinal);

    public override long GetInt64(int ordinal) => GetFieldValue<long>(ordinal);

    public override string GetString(int ordinal) => GetFieldValue<string>(ordinal);

    public override int GetValues(object[] values) => throw new NotSupportedException();
}
