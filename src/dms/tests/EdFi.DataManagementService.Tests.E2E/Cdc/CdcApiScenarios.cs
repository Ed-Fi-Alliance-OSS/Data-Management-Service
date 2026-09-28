// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Tests.E2E.Cdc;

internal interface ICdcApiScenarios : IAsyncDisposable
{
    Task<CdcScenarioIdentity> AttachAsync(CancellationToken token);
    Task StudentCrudAsync(CancellationToken token);
    Task DescriptorCrudAsync(CancellationToken token);
    Task OverlapAsync(CancellationToken token);
    Task DeleteBeforeProjectionAsync(CancellationToken token);
    Task OnlineRebuildAsync(CancellationToken token);
    Task ExecutorRestartAsync(CancellationToken token);
    Task UnavailableEvidenceAsync(CancellationToken token);
    Task TerminalLossAsync(CancellationToken token);
}

internal sealed partial class CdcApiScenarios : ICdcApiScenarios
{
    private readonly CdcAttachedContext _context = new();

    public async Task<CdcScenarioIdentity> AttachAsync(CancellationToken token)
    {
        await _context.InitializeAsync(token);
        return CdcScenarioIdentity.FromBinding(_context.Request.Binding);
    }

    // Each later story task replaces its method with the real scenario. Never report placeholders as passed.
    public Task StudentCrudAsync(CancellationToken token) => throw new NotImplementedException();

    public Task DescriptorCrudAsync(CancellationToken token) => throw new NotImplementedException();

    public Task OverlapAsync(CancellationToken token) => throw new NotImplementedException();

    public Task DeleteBeforeProjectionAsync(CancellationToken token) => throw new NotImplementedException();

    public Task OnlineRebuildAsync(CancellationToken token) => throw new NotImplementedException();

    public Task ExecutorRestartAsync(CancellationToken token) => throw new NotImplementedException();

    public Task UnavailableEvidenceAsync(CancellationToken token) => throw new NotImplementedException();

    public Task TerminalLossAsync(CancellationToken token) => throw new NotImplementedException();

    public ValueTask DisposeAsync() => _context.DisposeAsync();
}
