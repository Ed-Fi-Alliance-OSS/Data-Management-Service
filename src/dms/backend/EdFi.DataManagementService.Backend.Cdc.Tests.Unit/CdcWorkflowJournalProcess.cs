// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Unit;

/// <summary>Executable test-only child controller for actual OS lock and abrupt termination tests.</summary>
internal static class CdcWorkflowJournalProcess
{
    public static async Task Main(string[] args)
    {
        LocalCdcWorkflowJournalStore store = new(
            args[0],
            TimeProvider.System,
            boundary =>
            {
                if (args[1] == boundary.ToString())
                {
                    Console.WriteLine("boundary");
                    Console.ReadLine(); // Parent kills this process without unwinding finally/disposal.
                }
            }
        );
        await using var session = await store.AcquireAsync(
            TimeSpan.FromSeconds(10),
            TimeSpan.FromMilliseconds(20),
            CancellationToken.None
        );
        if (args[1] == "hold")
        {
            Console.WriteLine("locked");
            await Console.In.ReadLineAsync();
            return;
        }
        var journal = await session.ReadAsync(CdcWorkflowJournalTestData.Target, CancellationToken.None);
        await session.RecordIntentAsync(
            journal.Target,
            journal.WorkflowId,
            Guid.NewGuid(),
            CdcWorkflowEffect.CreateDatabase,
            [],
            CancellationToken.None
        );
    }
}
