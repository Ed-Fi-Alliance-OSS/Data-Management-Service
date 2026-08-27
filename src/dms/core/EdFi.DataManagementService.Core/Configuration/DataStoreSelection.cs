// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Backend;

namespace EdFi.DataManagementService.Core.Configuration;

/// <summary>
/// Scoped implementation that holds the selected data store and effective target for one request.
/// </summary>
public class DataStoreSelection : IDataStoreSelection
{
    private DataStore? _selectedDataStore;
    private EffectiveDataStoreTarget? _effectiveTarget;

    /// <inheritdoc />
    public bool IsSet => _selectedDataStore is not null;

    /// <inheritdoc />
    public bool IsEffectiveTargetSet => _effectiveTarget is not null;

    /// <inheritdoc />
    public void SetSelectedDataStore(DataStore dataStore)
    {
        // The write-once check comes before the argument checks, so a second assignment reports the
        // duplicated phase whatever it was handed. Validating first would let a second call with a
        // blank or absent data store report a bad argument instead, hiding the real defect.
        if (_selectedDataStore is not null)
        {
            throw new InvalidOperationException(
                "The parent data store has already been selected for this request. "
                    + "Only ResolveDataStoreMiddleware assigns it, and it assigns it once."
            );
        }

        ArgumentNullException.ThrowIfNull(dataStore);

        if (string.IsNullOrWhiteSpace(dataStore.ConnectionString))
        {
            throw new ArgumentException("data store must have a valid connection string", nameof(dataStore));
        }

        _selectedDataStore = dataStore;
    }

    /// <inheritdoc />
    public DataStore GetSelectedDataStore()
    {
        return _selectedDataStore
            ?? throw new InvalidOperationException(
                "The parent data store has not been selected for this request. "
                    + "Ensure ResolveDataStoreMiddleware is registered in the pipeline before it is read."
            );
    }

    /// <inheritdoc />
    public void SetEffectiveTarget(EffectiveDataStoreTarget target)
    {
        // Phase before argument, for the same reason as above.
        if (_effectiveTarget is not null)
        {
            throw new InvalidOperationException(
                "The effective target has already been selected for this request. "
                    + "Only the target-selection step assigns it, and it assigns it once, so that every "
                    + "database operation in the request uses the same physical database."
            );
        }

        ArgumentNullException.ThrowIfNull(target);

        _effectiveTarget = target;
    }

    /// <inheritdoc />
    public EffectiveDataStoreTarget GetEffectiveTarget()
    {
        // Deliberately not a fallback to the parent's connection string. A pipeline that reaches the
        // database without selecting a target has a composition defect, and reading the primary here
        // would hide it by serving the wrong database for a request that asked for a derivative.
        return _effectiveTarget
            ?? throw new InvalidOperationException(
                "The effective target has not been selected for this request. "
                    + "Ensure SelectEffectiveDataStoreTargetMiddleware is registered in the pipeline "
                    + "before any database access."
            );
    }
}
