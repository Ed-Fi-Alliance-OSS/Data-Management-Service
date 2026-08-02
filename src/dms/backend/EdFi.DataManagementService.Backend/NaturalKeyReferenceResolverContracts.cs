// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using EdFi.DataManagementService.Backend.External;

namespace EdFi.DataManagementService.Backend;

/// <summary>
/// One matched row from a natural-key lookup batch, attributed back to the batch entry that produced it.
/// </summary>
/// <remarks>
/// Only hits produce rows — every statement the builders emit is an <c>INNER JOIN</c> — so an entry with no
/// row is a miss. Rows arrive in unspecified order, so <see cref="GroupIndex"/> together with
/// <see cref="Ordinal"/> is the only safe way to attribute one. Both adapters return rows already in the
/// coordinates of the batch the caller handed in.
/// </remarks>
/// <param name="GroupIndex">The zero-based index of the group in <c>NaturalKeyLookupBatch.Groups</c>.</param>
/// <param name="Ordinal">
/// The one-based position of the matched entry within its group — <c>Entries[Ordinal - 1]</c>.
/// </param>
/// <param name="DocumentId">The matched document id.</param>
/// <param name="Discriminator">
/// The matched row's discriminator: <c>{ProjectName}:{ResourceName}</c> for an abstract probe group, the
/// bare descriptor resource name for a descriptor group, and <see langword="null"/> otherwise.
/// </param>
/// <param name="ResourceKeyId">
/// The matched <c>dms.Descriptor</c> row's mirrored resource key id, for descriptor groups only.
/// </param>
public sealed record NaturalKeyLookupRow(
    int GroupIndex,
    int Ordinal,
    long DocumentId,
    string? Discriminator,
    short? ResourceKeyId
);

/// <summary>
/// Narrow adapter seam for executing one natural-key lookup batch through a dialect-specific backend.
/// </summary>
/// <remarks>
/// One call is one round trip on both dialects: each adapter issues exactly one command, because neither binds a
/// parameter per entry — PostgreSQL passes one array parameter per probe column per group and SQL Server
/// passes one JSON payload per group.
/// </remarks>
public interface INaturalKeyLookupAdapter
{
    /// <summary>
    /// Resolves every entry in <paramref name="batch" />, returning one row per hit.
    /// </summary>
    Task<IReadOnlyList<NaturalKeyLookupRow>> ResolveAsync(
        NaturalKeyLookupBatch batch,
        CancellationToken cancellationToken = default
    );
}

/// <summary>
/// Creates the request-scoped dialect adapter used by <see cref="NaturalKeyReferenceResolver" />.
/// </summary>
/// <remarks>
/// Two creation modes because the two paths differ: the query path consumes the DI-scoped ambient adapter,
/// while the write path news the resolver up against an already open write connection and transaction.
/// </remarks>
public interface INaturalKeyLookupAdapterFactory
{
    /// <summary>
    /// Creates the adapter for the current request scope.
    /// </summary>
    INaturalKeyLookupAdapter CreateAdapter();

    /// <summary>
    /// Creates an adapter bound to an already-open write connection and transaction.
    /// </summary>
    INaturalKeyLookupAdapter CreateSessionAdapter(DbConnection connection, DbTransaction transaction);
}
