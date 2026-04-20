// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Tests.E2E.Management;

public class SearchContainerSetup(
    Func<bool>? useRelationalBackend = null,
    Func<Task>? resetLegacyDatabase = null,
    Func<Task>? resetRelationalDatabase = null
) : ContainerSetupBase
{
    private readonly Func<bool> _useRelationalBackend =
        useRelationalBackend ?? (() => AppSettings.UseRelationalBackend);
    private readonly Func<Task> _resetLegacyDatabase = resetLegacyDatabase ?? ResetDatabase;
    private readonly Func<Task> _resetRelationalDatabase = resetRelationalDatabase ?? ResetRelationalDatabase;

    public override string ApiUrl()
    {
        return $"http://localhost:{AppSettings.DmsPort}/";
    }

    public override async Task ResetData()
    {
        if (_useRelationalBackend())
        {
            await _resetRelationalDatabase();
            return;
        }

        await _resetLegacyDatabase();
    }
}
