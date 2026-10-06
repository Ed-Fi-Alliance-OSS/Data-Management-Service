// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text;
using System.Text.RegularExpressions;

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

    // Failure details stay private: do not echo them to NUnit/TRX. The exporter rejects this prefix.
    // The runner also uses this before attachment has created a journal (e.g. early cancellation).
    public static async Task WriteFailureAsync(
        string reportPath,
        string invocationId,
        string stage,
        Exception exception,
        CancellationToken token
    )
    {
        string path = reportPath + ".checkpoints";
        if (!File.Exists(path))
        {
            _ = new CdcScenarioDiagnostics(reportPath, invocationId);
        }
        // Bound private diagnostics too, and never append to a different invocation's journal.
        using (var reader = File.OpenText(path))
        {
            if (await reader.ReadLineAsync(token) != invocationId)
            {
                return;
            }
        }

        string message = exception.Message.TrimStart();
        int newline = message.IndexOfAny(['\r', '\n']);
        if (newline >= 0)
        {
            message = message[..newline];
        }
        message = new string(message.Where(character => !char.IsControl(character)).ToArray());
        // Preserve the diagnostic lead-in, but drop the entire suffix at quoted/structured data,
        // URLs or credential markers. This deliberately avoids parsing arbitrary response bodies
        // or attempting to redact individual values in malformed connection strings.
        var sensitive = Regex.Match(
            message,
            "[\"'{\\[<]|://|password|pwd|secret|token|authorization|bearer|connectionstring|user id|username|body|payload",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
        );
        if (sensitive.Success)
        {
            message = message[..sensitive.Index] + "[redacted]";
        }
        string line = $"failure:{stage}: {exception.GetType().Name}: {message}";
        line = line[..Math.Min(line.Length, 512)] + "\n";
        if (new FileInfo(path).Length + Encoding.UTF8.GetByteCount(line) <= 37 + MaximumCheckpoints * 513)
        {
            await File.AppendAllTextAsync(path, line, token);
        }
    }
}
