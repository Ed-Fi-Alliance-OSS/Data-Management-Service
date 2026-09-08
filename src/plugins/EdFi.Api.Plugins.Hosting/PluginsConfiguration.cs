// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics.CodeAnalysis;

namespace EdFi.Api.Plugins.Hosting;

/// <summary>
/// The validated result of binding the <c>Plugins</c> section: either an allowlist naming at least one
/// plugin together with the root they are read from, or a statement that no plugins were asked for.
/// </summary>
/// <remarks>
/// <para>
/// The two cases are separate because the shipped default is the second one and it must not depend on
/// the first one's inputs. A deployment that adopts no plugins boots exactly as it does today, whatever
/// <c>Plugins:Directory</c> happens to say, so an allowlist with no names never resolves, validates or
/// probes a root. Carrying a placeholder path for that case would make the absence of a root
/// indistinguishable from a root that happens to be wrong.
/// </para>
/// <para>
/// Every name in <see cref="AllowedNames"/> has been trimmed, checked for ambiguity against the rest of
/// the list, and checked against the single-path-segment rule, so a caller may compose a path from one
/// without validating it again. Nothing here has touched the filesystem.
/// </para>
/// </remarks>
internal sealed record PluginsConfiguration
{
    private readonly string? _resolvedRoot;

    private PluginsConfiguration(string? resolvedRoot, IReadOnlyList<string> allowedNames)
    {
        _resolvedRoot = resolvedRoot;
        AllowedNames = allowedNames;
    }

    /// <summary>No plugin was asked for, so there is no root and nothing to read.</summary>
    internal static PluginsConfiguration NoPluginsRequested { get; } = new(null, []);

    /// <summary>At least one plugin was asked for, from the given fully qualified root.</summary>
    internal static PluginsConfiguration Rooted(string resolvedRoot, IReadOnlyList<string> allowedNames) =>
        new(resolvedRoot, allowedNames);

    /// <summary>The allowlisted plugin names, in the order the operator wrote them.</summary>
    internal IReadOnlyList<string> AllowedNames { get; }

    /// <summary>
    /// Gets the plugin root, which exists only when the allowlist named at least one plugin.
    /// </summary>
    internal bool TryGetResolvedRoot([NotNullWhen(true)] out string? resolvedRoot)
    {
        resolvedRoot = _resolvedRoot;
        return resolvedRoot is not null;
    }
}
