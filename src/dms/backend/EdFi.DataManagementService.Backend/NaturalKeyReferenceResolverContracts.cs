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
/// row is a miss. Adapters that have to split a batch (SQL Server's per-command parameter ceiling)
/// re-attribute their sub-batch rows to the original batch's coordinates before returning, so the caller
/// always reads <see cref="GroupIndex"/> and <see cref="Ordinal"/> against the batch it handed in.
/// </remarks>
/// <param name="GroupIndex">The zero-based index of the group in <c>NaturalKeyLookupBatch.Groups</c>.</param>
/// <param name="Ordinal">
/// The one-based position of the matched entry within its group — <c>Entries[Ordinal - 1]</c>. Rows arrive
/// in unspecified order, so this is the only safe way to attribute one.
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
/// The counterpart of <see cref="IReferenceResolverAdapter" /> for the natural-key resolver. One call is
/// one logical round trip: the PostgreSQL adapter always issues exactly one command, and the SQL Server
/// adapter issues one command per parameter-budget slice of the batch.
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
/// Mirrors <see cref="IReferenceResolverAdapterFactory" /> and exists for the same reason: the query path
/// consumes the DI-scoped ambient adapter, while the write path news the resolver up against an already
/// open write connection and transaction.
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
