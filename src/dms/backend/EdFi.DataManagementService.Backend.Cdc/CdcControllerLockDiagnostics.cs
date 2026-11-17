// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Backend.Cdc;

/// <summary>Reports actual controller-lock contention once per command on its diagnostic stream.</summary>
public static class CdcControllerLockDiagnostics
{
    public const string Contended = "CDC_CONTROLLER_LOCK_CONTENDED";
    private static readonly AsyncLocal<Scope> _current = new();

    /// <summary>The host supplies stderr; result output and private state paths are never written here.</summary>
    public static IDisposable ReportTo(TextWriter diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        var scope = new Scope(diagnostics, _current.Value!);
        _current.Value = scope;
        return scope;
    }

    internal static void ReportContention() => _current.Value?.Report();

    private sealed class Scope(TextWriter diagnostics, Scope previous) : IDisposable
    {
        private int _reported;
        private int _disposed;

        public void Report()
        {
            if (Volatile.Read(ref _disposed) != 0 || Interlocked.Exchange(ref _reported, 1) != 0)
            {
                return;
            }
            try
            {
                diagnostics.WriteLine(Contended);
                diagnostics.Flush();
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException)
            {
                // A closed diagnostic stream must not change lock acquisition or its failure.
            }
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _disposed, 1);
            if (ReferenceEquals(_current.Value, this))
            {
                _current.Value = previous;
            }
        }
    }
}
