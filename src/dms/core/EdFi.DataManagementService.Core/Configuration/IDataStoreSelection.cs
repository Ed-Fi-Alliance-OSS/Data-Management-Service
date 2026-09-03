// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Backend;

namespace EdFi.DataManagementService.Core.Configuration;

/// <summary>
/// The request-scoped record of which data store a request resolved to, and which physical database it
/// uses. Populated by middleware and consumed by repositories.
///
/// It is deliberately two-phase. The resolver records the parent data store, which supplies tenant,
/// route-context, and client-authorization identity. A later step records the effective target, which
/// is the one physical database every database operation in the request uses. Keeping them apart is
/// what lets a request read a derivative while still being authorized and logged as its parent.
///
/// Both phases are write-once, and there is no accessor that falls back from one to the other. A
/// consumer that reads the effective target before it has been assigned gets an error rather than the
/// primary, so a pipeline that reaches the database without selecting a target fails loudly instead of
/// quietly reading the wrong one.
/// </summary>
public interface IDataStoreSelection
{
    /// <summary>
    /// Records the resolved parent data store. Called by ResolveDataStoreMiddleware, exactly once.
    /// </summary>
    void SetSelectedDataStore(DataStore dataStore);

    /// <summary>
    /// The resolved parent data store, for authorization, route context, and sanitized logging.
    /// </summary>
    DataStore GetSelectedDataStore();

    /// <summary>Whether the parent data store has been recorded for this request.</summary>
    bool IsSet { get; }

    /// <summary>
    /// Records the effective target. Called by the target-selection step, exactly once.
    /// </summary>
    void SetEffectiveTarget(EffectiveDataStoreTarget target);

    /// <summary>
    /// The one physical database this request uses. Every database consumer reads this and only this.
    /// </summary>
    EffectiveDataStoreTarget GetEffectiveTarget();

    /// <summary>Whether the effective target has been recorded for this request.</summary>
    bool IsEffectiveTargetSet { get; }
}
