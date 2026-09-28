// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using EdFi.DataManagementService.Core.DocumentCache.Cdc;

namespace EdFi.DataManagementService.Tests.E2E.Cdc;

internal enum CdcScenarioOutcome
{
    NotRun,
    Running,
    Passed,
    Failed,
}

internal enum CdcScenarioFailure
{
    None,
    Error,
    Cancelled,
    TimedOut,
    Unimplemented,
}

internal sealed record CdcScenarioIdentity(string Provider, string BindingId, long Generation)
{
    public static CdcScenarioIdentity Unavailable { get; } = new("", "", 0);

    public static CdcScenarioIdentity FromBinding(CdcBinding binding) =>
        new(
            binding.Provider == CdcProvider.Postgresql ? "Postgresql" : "Mssql",
            Convert.ToHexStringLower(
                SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(binding.ToCompleteBindingIdentity()))
            ),
            binding.Generation
        );
}

internal sealed class CdcScenarioResult(string id)
{
    public string Id { get; } = id;
    public CdcScenarioOutcome Outcome { get; set; } = CdcScenarioOutcome.NotRun;
    public CdcScenarioFailure Failure { get; set; } = CdcScenarioFailure.None;
}

// Version 1 contract and qualification rules are documented in README.md beside this writer.
// Only the fixture writes this file; never load/merge runner-owned fields or resume an old report.
internal sealed class CdcScenarioReport(string invocationId)
{
    public int Version { get; } = 1;
    public string InvocationId { get; } = invocationId;
    public CdcScenarioIdentity Identity { get; set; } = CdcScenarioIdentity.Unavailable;
    public CdcAttachmentBoundary AttachmentBoundary { get; set; } = CdcAttachmentBoundary.None;
    public CdcScenarioResult Attachment { get; } = new("Attachment");
    public CdcScenarioResult Disposal { get; } = new("Disposal");
    public CdcScenarioResult[] Scenarios { get; } =
    [
        new("CDC-E2E-01"),
        new("CDC-E2E-02"),
        new("CDC-E2E-03"),
        new("CDC-E2E-04"),
        new("CDC-E2E-05"),
        new("CDC-E2E-06"),
        new("CDC-E2E-07"),
        new("CDC-E2E-08"),
    ];
}

internal sealed class CdcScenarioReportWriter
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };
    private readonly string _path;
    private bool _created;

    public CdcScenarioReportWriter(string path, string invocationId)
    {
        if (!Guid.TryParseExact(invocationId, "D", out var id) || id == Guid.Empty)
        {
            throw new InvalidOperationException("CDC_API_REPORT_INVOCATION_REQUIRED");
        }
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            throw new InvalidOperationException("CDC_API_REPORT_ABSOLUTE_PATH_REQUIRED");
        }
        _path = path;
    }

    public async Task WriteAsync(CdcScenarioReport report, CancellationToken token)
    {
        string temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(
                temporary,
                JsonSerializer.SerializeToUtf8Bytes(report, Options),
                token
            );
            token.ThrowIfCancellationRequested();
            // Initial publication must fail if the runner supplied a previously used path.
            File.Move(temporary, _path, overwrite: _created);
            _created = true;
        }
        finally
        {
            File.Delete(temporary);
        }
    }
}
