// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using EdFi.DataManagementService.Core.External.Backend;

namespace EdFi.DataManagementService.Core.Configuration;

/// <summary>
/// One configured target that a tenant's data-store configuration claims.
/// </summary>
/// <param name="TenantKey">The tenant whose configuration claims this target.</param>
/// <param name="ParentDataStoreId">The data store the target belongs to, primary or derivative alike.</param>
/// <param name="Kind">Whether this is the parent's own database, a read replica, or a snapshot.</param>
/// <param name="ConfiguredConnectionString">
/// The connection string as configured in CMS, copied verbatim. It is deliberately never
/// provider-realized here: realizing one requires provider parsing, which belongs only inside a
/// connection-acquisition boundary, and a value no provider could open must still be publishable.
/// </param>
public readonly record struct ConfiguredTargetOwner(
    string TenantKey,
    long ParentDataStoreId,
    EffectiveTargetKind Kind,
    string ConfiguredConnectionString
);

/// <summary>
/// An immutable, totally ordered global view of configured ownership across every loaded tenant.
/// </summary>
/// <param name="Version">
/// Monotonic and assigned under the provider's publication lock, so a later snapshot always carries a
/// higher version than every snapshot reconciled before it.
/// </param>
/// <param name="Owners">
/// Every configured target of every loaded tenant, not only the tenant whose load produced this
/// snapshot. A consumer deciding what it may stop owning needs the whole union: a connection string
/// one tenant drops may still be claimed by another.
/// </param>
public sealed record DataStoreOwnershipSnapshot(long Version, ImmutableArray<ConfiguredTargetOwner> Owners);

/// <summary>
/// Receives the complete configured-ownership snapshot after every successful configuration
/// publication.
/// </summary>
/// <remarks>
/// Implementations must be failure-atomic: compute a candidate state without mutating anything, then
/// publish it with operations that cannot throw. An exception must never leave a partially retired
/// owner set, and the safe direction is always to leak a pool rather than dispose or clear one that is
/// still owned or still in use.
///
/// Implementations must never reject a snapshot or fail publication over a connection string no
/// provider can parse - such a value must still be publishable, participate in ownership or in
/// nothing, and fail only at the acquisition boundary of the request that selects it. A reconciler
/// that is itself a connection-acquisition boundary may realize configured strings tolerantly when
/// its provider defines pool ownership by the effective (canonicalized) string, because configured
/// text alone cannot see two spellings of one pool; realization is pure string work, and no
/// implementation may open a connection, create a pooled resource, or perform any I/O here.
/// Implementations must not assume they are called on any particular thread; the provider invokes
/// them in registration order while holding its publication lock, so an implementation that blocks
/// holds up every other tenant's publication.
///
/// The provider guarantees a reconciler is not invoked at all for a failed load. It cannot guarantee
/// what an implementation did once invoked, which is why failure atomicity is the implementation's
/// obligation. Each implementation is also responsible for ignoring a snapshot whose version is not
/// greater than the last it applied; the publication lock makes an out-of-order delivery unreachable,
/// but the check is local and cheap.
/// </remarks>
public interface IDataStoreOwnershipReconciler
{
    void Reconcile(DataStoreOwnershipSnapshot snapshot);
}
