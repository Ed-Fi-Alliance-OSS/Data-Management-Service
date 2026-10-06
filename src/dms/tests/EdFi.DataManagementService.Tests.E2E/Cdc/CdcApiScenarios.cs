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
    private CdcScenarioDiagnostics _diagnostics = null!;

    private Task WriteDiagnosticAsync(string checkpoint) => _diagnostics.WriteAsync(checkpoint);

    public async Task<CdcScenarioIdentity> AttachAsync(CancellationToken token)
    {
        _diagnostics = new(
            Environment.GetEnvironmentVariable("CDC_API_E2E_REPORT_PATH") ?? "",
            Environment.GetEnvironmentVariable("CDC_API_E2E_INVOCATION_ID") ?? ""
        );
        await _context.InitializeAsync(token);
        await WriteDiagnosticAsync($"Attachment:effectiveSchema={_context.EffectiveSchemaHash}");
        await WriteDiagnosticAsync($"Attachment:runtime={Environment.Version}");
        await WriteDiagnosticAsync($"Attachment:pageSize={_context.ConfiguredPageSize}");
        return CdcScenarioIdentity.FromBinding(_context.Request.Binding);
    }

    public ValueTask DisposeAsync() => _context.DisposeAsync();
}
