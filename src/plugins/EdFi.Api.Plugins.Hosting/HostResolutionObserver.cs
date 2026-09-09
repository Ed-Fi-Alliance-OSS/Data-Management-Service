// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.Api.Plugins.Hosting;

/// <summary>
/// Watches a finite, caller-supplied set of assembly names and records which of them a load context's
/// host-first override actually served.
/// </summary>
/// <remarks>
/// <para>
/// This exists because one claim about the loader cannot be observed from its results. For an assembly
/// a framework-dependent publish does not declare, the default context is where the assembly comes
/// from whether the override served it or declined and let the runtime fall back, and there is no
/// substitution row either, because there is no declared version for the host's to have differed from.
/// The only way to tell the two apart is to ask the override.
/// </para>
/// <para>
/// It is deliberately not a log of what a context was asked for. The caller names the assemblies it
/// wants to know about, nothing outside that set is retained, and no ordering or repeat count is kept.
/// A context created without one records nothing at all, which is what the public loader path does, so
/// an ordinary host run carries no observation state.
/// </para>
/// </remarks>
internal sealed class HostResolutionObserver
{
    private readonly HashSet<string> _watched;
    private readonly HashSet<string> _served = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    /// <summary>Watches exactly <paramref name="simpleNames"/> and nothing else.</summary>
    internal HostResolutionObserver(IReadOnlyCollection<string> simpleNames)
    {
        ArgumentNullException.ThrowIfNull(simpleNames);

        _watched = new HashSet<string>(simpleNames, StringComparer.Ordinal);
    }

    /// <summary>
    /// Notes that the override served <paramref name="simpleName"/> from the host, when that name is
    /// watched.
    /// </summary>
    internal void Record(string simpleName)
    {
        // Filtered here rather than at the call site, so the context stays free of any knowledge of
        // what is being watched and an unwatched name costs a set lookup and nothing else.
        lock (_gate)
        {
            if (_watched.Contains(simpleName))
            {
                _served.Add(simpleName);
            }
        }
    }

    /// <summary>
    /// Whether the override served <paramref name="simpleName"/>, which must be one of the watched
    /// names.
    /// </summary>
    /// <remarks>
    /// Asking about an unwatched name throws rather than answering <see langword="false"/>. A quiet
    /// false would let a caller believe it had observed the absence of a resolution that was simply
    /// never being watched for, which is the one wrong answer this type must not give.
    /// </remarks>
    internal bool HasServed(string simpleName)
    {
        lock (_gate)
        {
            if (!_watched.Contains(simpleName))
            {
                throw new InvalidOperationException(
                    $"'{simpleName}' is not among the watched names [{string.Join(", ", _watched)}], "
                        + "so nothing was recorded about it either way."
                );
            }

            return _served.Contains(simpleName);
        }
    }
}
