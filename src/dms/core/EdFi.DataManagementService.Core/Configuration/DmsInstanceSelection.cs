// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core.Configuration;

/// <summary>
/// Scoped implementation that holds the selected DMS instance for the current request
/// </summary>
public class DmsInstanceSelection : IDmsInstanceSelection
{
    private DmsInstance? _selectedInstance;
    private bool _isSet;

    /// <inheritdoc />
    public bool IsSet => _isSet;

    /// <inheritdoc />
    public void SetSelectedDmsInstance(DmsInstance dmsInstance)
    {
        ArgumentNullException.ThrowIfNull(dmsInstance);

        if (string.IsNullOrWhiteSpace(dmsInstance.ConnectionString))
        {
            throw new ArgumentException(
                "DMS instance must have a valid connection string",
                nameof(dmsInstance)
            );
        }

        _selectedInstance = dmsInstance;
        _isSet = true;
    }

    /// <inheritdoc />
    public DmsInstance GetSelectedDmsInstance()
    {
        if (!_isSet || _selectedInstance == null)
        {
            throw new InvalidOperationException(
                "DMS instance has not been set for this request. "
                    + "Ensure ResolveDmsInstanceMiddleware is registered in the pipeline before repositories are accessed."
            );
        }

        return _selectedInstance;
    }
}
