// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Tests.E2E.Cdc;

internal sealed partial class CdcApiScenarios
{
    public Task StudentCrudAsync(CancellationToken token) =>
        CrudAsync(
            CdcApiResource.Student,
            CdcApiClient.NewStudent("Created"),
            "firstName",
            "CDC-E2E-01",
            token
        );
}
