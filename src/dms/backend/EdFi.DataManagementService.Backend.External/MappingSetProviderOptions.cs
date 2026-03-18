// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Backend.External;

/// <summary>
/// Configuration options for mapping set selection behavior.
/// Defaults are compile-only mode (packs disabled, runtime compilation allowed).
/// DMS-978 will add the full configuration surface (appsettings binding,
/// environment variables, validation).
/// </summary>
public sealed class MappingSetProviderOptions
{
    /// <summary>
    /// Whether mapping pack loading is enabled. When false, pack loading is skipped
    /// and runtime compilation is used directly.
    /// </summary>
    public bool PacksEnabled { get; set; }

    /// <summary>
    /// Whether mapping packs are required. When true and a pack is missing or invalid,
    /// requests for that database fail fast without attempting runtime compilation.
    /// Only meaningful when <see cref="PacksEnabled"/> is true.
    /// </summary>
    public bool PacksRequired { get; set; }

    /// <summary>
    /// Root filesystem path for mapping pack files. Only used when <see cref="PacksEnabled"/>
    /// is true.
    /// </summary>
    public string? PackRootPath { get; set; }

    /// <summary>
    /// Whether runtime compilation is allowed as a fallback when packs are enabled but
    /// a matching pack is not found. Ignored when <see cref="PacksEnabled"/> is false
    /// (compilation is always used in that case).
    /// </summary>
    public bool AllowRuntimeCompileFallback { get; set; } = true;
}
