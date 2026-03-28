// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Core.Configuration;

/// <summary>
/// Adapts the mutable Core request instance selection to the read-only backend request connection contract.
/// </summary>
internal sealed class DmsInstanceRequestConnectionProvider(IDmsInstanceSelection dmsInstanceSelection)
    : IRequestConnectionProvider
{
    public RequestConnection GetRequestConnection()
    {
        DmsInstance selectedInstance = dmsInstanceSelection.GetSelectedDmsInstance();

        if (string.IsNullOrWhiteSpace(selectedInstance.ConnectionString))
        {
            throw new InvalidOperationException(
                $"Selected DMS instance '{selectedInstance.Id}' does not have a valid connection string."
            );
        }

        return new RequestConnection(
            new DmsInstanceId(selectedInstance.Id),
            selectedInstance.ConnectionString
        );
    }
}
