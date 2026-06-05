// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core.Configuration;

/// <summary>
/// Scoped implementation that holds the selected data store for the current request
/// </summary>
public class DataStoreSelection : IDataStoreSelection
{
    private DataStore? _selectedInstance;
    private bool _isSet;

    /// <inheritdoc />
    public bool IsSet => _isSet;

    /// <inheritdoc />
    public void SetSelectedDataStore(DataStore dataStore)
    {
        ArgumentNullException.ThrowIfNull(dataStore);

        if (string.IsNullOrWhiteSpace(dataStore.ConnectionString))
        {
            throw new ArgumentException("data store must have a valid connection string", nameof(dataStore));
        }

        _selectedInstance = dataStore;
        _isSet = true;
    }

    /// <inheritdoc />
    public DataStore GetSelectedDataStore()
    {
        if (!_isSet || _selectedInstance == null)
        {
            throw new InvalidOperationException(
                "data store has not been set for this request. "
                    + "Ensure ResolveDataStoreMiddleware is registered in the pipeline before repositories are accessed."
            );
        }

        return _selectedInstance;
    }
}
