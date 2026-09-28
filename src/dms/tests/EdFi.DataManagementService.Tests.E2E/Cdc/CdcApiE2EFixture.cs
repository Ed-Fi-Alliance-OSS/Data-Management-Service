// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Tests.E2E.Cdc;

/// <summary>Base for the single eight-phase scenario test; intentionally has no attachment-only test.
/// Does not participate in Reqnroll reset hooks. Original wrapper retains infrastructure ownership.</summary>
[Explicit("Requires -EnableKafkaCdc -CdcApiE2E and CDC_API_E2E_HANDOFF_PATH.")]
[NonParallelizable]
public abstract class CdcApiE2EFixture
{
    private protected CdcAttachedContext Context { get; private set; } = null!;

    [OneTimeSetUp]
    public async Task AttachAsync() => Context = await CdcAttachedContext.AttachAsync(CancellationToken.None);

    [OneTimeTearDown]
    public async Task DetachAsync()
    {
        if (Context is null)
        {
            return;
        }

        if (TestContext.CurrentContext.Result.Outcome.Status == NUnit.Framework.Interfaces.TestStatus.Failed)
        {
            await Context.DisposeAfterFailureAsync();
        }
        else
        {
            await Context.DisposeAsync();
        }
    }
}
