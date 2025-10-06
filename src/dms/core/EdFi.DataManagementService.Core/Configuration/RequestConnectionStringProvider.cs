// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core.Configuration;

/// <summary>
/// Scoped implementation that holds the database connection string for the current request
/// </summary>
public class RequestConnectionStringProvider : IRequestConnectionStringProvider
{
    private string? _connectionString;
    private long _dmsInstanceId;
    private bool _isSet;

    /// <inheritdoc />
    public bool IsSet => _isSet;

    /// <inheritdoc />
    public void SetConnectionString(string connectionString, long dmsInstanceId)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new ArgumentException(
                "Connection string cannot be null or empty",
                nameof(connectionString)
            );
        }

        _connectionString = connectionString;
        _dmsInstanceId = dmsInstanceId;
        _isSet = true;
    }

    /// <inheritdoc />
    public string GetConnectionString()
    {
        if (!_isSet || string.IsNullOrWhiteSpace(_connectionString))
        {
            throw new InvalidOperationException(
                "Connection string has not been set for this request. "
                    + "Ensure DmsInstanceSelectionMiddleware is registered in the pipeline before repositories are accessed."
            );
        }

        return _connectionString;
    }

    /// <inheritdoc />
    public long GetDmsInstanceId()
    {
        if (!_isSet)
        {
            throw new InvalidOperationException(
                "DMS instance ID has not been set for this request. "
                    + "Ensure DmsInstanceSelectionMiddleware is registered in the pipeline."
            );
        }

        return _dmsInstanceId;
    }
}
