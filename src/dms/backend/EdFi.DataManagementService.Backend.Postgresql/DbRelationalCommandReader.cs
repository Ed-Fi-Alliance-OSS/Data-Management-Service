// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using EdFi.DataManagementService.Backend;

namespace EdFi.DataManagementService.Backend.Postgresql;

internal sealed class DbRelationalCommandReader(DbDataReader reader) : IRelationalCommandReader
{
    private readonly DbDataReader _reader = reader ?? throw new ArgumentNullException(nameof(reader));

    public ValueTask DisposeAsync() => _reader.DisposeAsync();

    public Task<bool> ReadAsync(CancellationToken cancellationToken = default) =>
        _reader.ReadAsync(cancellationToken);

    public Task<bool> NextResultAsync(CancellationToken cancellationToken = default) =>
        _reader.NextResultAsync(cancellationToken);

    public int GetOrdinal(string name) => _reader.GetOrdinal(name);

    public T GetFieldValue<T>(int ordinal) => _reader.GetFieldValue<T>(ordinal);

    public bool IsDBNull(int ordinal) => _reader.IsDBNull(ordinal);
}
