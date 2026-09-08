// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.Api.Plugins.Hosting;

/// <summary>
/// The validated result of binding the <c>Plugins</c> section: a fully qualified plugin root and the
/// allowlist in the order it was written.
/// </summary>
/// <remarks>
/// Every name in <paramref name="AllowedNames"/> has already been trimmed, checked for ambiguity
/// against the rest of the list, and checked against the single-path-segment rule, so a caller may
/// compose a path from one without validating it again. Nothing here has touched the filesystem.
/// </remarks>
/// <param name="ResolvedRoot">The plugin root as a fully qualified path.</param>
/// <param name="AllowedNames">The allowlisted plugin names, in the order the operator wrote them.</param>
internal sealed record PluginsConfiguration(string ResolvedRoot, IReadOnlyList<string> AllowedNames);
