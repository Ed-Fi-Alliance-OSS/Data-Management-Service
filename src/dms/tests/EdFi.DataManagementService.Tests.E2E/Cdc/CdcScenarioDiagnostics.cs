// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text;

namespace EdFi.DataManagementService.Tests.E2E.Cdc;

// Private checkpoint journal only; scenario.json remains the sole fixture outcome contract.
// Persist each completed checkpoint so a killed test process still leaves useful evidence.
internal sealed class CdcScenarioDiagnostics
{
    internal const int MaximumCheckpoints = 2048;
    private readonly string _path;
    private int _count;

    public CdcScenarioDiagnostics(string reportPath, string invocationId)
    {
        if (!Path.IsPathFullyQualified(reportPath) || !Guid.TryParseExact(invocationId, "D", out _))
        {
            throw new InvalidOperationException("CDC_API_DIAGNOSTICS_IDENTITY_REQUIRED");
        }
        _path = reportPath + ".checkpoints";
        using var file = new FileStream(_path, FileMode.CreateNew, FileAccess.Write);
        file.Write(Encoding.UTF8.GetBytes(invocationId + "\n"));
    }

    // Calls belong to the serialized scenario path; no production logger or body is attached.
    public async Task WriteAsync(string checkpoint)
    {
        if (checkpoint.Length > 512 || checkpoint.Contains('\n') || checkpoint.Contains('\r'))
        {
            throw new InvalidOperationException("CDC_API_DIAGNOSTICS_CHECKPOINT_INVALID");
        }
        if (_count >= MaximumCheckpoints)
        {
            throw new InvalidOperationException("CDC_API_DIAGNOSTICS_LIMIT");
        }
        await File.AppendAllTextAsync(_path, checkpoint + "\n");
        _count++;
        await TestContext.Out.WriteLineAsync(checkpoint);
    }
}
