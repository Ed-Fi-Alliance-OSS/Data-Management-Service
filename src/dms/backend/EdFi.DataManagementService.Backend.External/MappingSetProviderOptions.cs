// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Backend.External;

/// <summary>
/// Configuration options for mapping set selection behavior.
/// Bound to the "MappingPacks" appsettings section.
/// Defaults are compile-only mode (packs disabled, runtime compilation allowed).
/// </summary>
public sealed class MappingSetProviderOptions
{
    /// <summary>
    /// Whether mapping pack loading is enabled. When false, pack loading is skipped
    /// and runtime compilation is used directly.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Whether mapping packs are required. When true and a pack is missing or invalid,
    /// requests for that database fail fast without attempting runtime compilation.
    /// Only meaningful when <see cref="Enabled"/> is true.
    /// </summary>
    public bool Required { get; set; }

    /// <summary>
    /// Root filesystem path for mapping pack files. Only used when <see cref="Enabled"/>
    /// is true.
    /// </summary>
    public string? RootPath { get; set; }

    /// <summary>
    /// Whether runtime compilation is allowed as a fallback when packs are enabled but
    /// a matching pack is not found. Ignored when <see cref="Enabled"/> is false
    /// (compilation is always used in that case).
    /// </summary>
    public bool AllowRuntimeCompileFallback { get; set; } = true;

    /// <summary>
    /// How many seconds a faulted cache entry stays before eviction. Zero (the default)
    /// means immediate eviction so the next request retries. Set to a positive value
    /// to prevent retry storms on permanently-failing keys.
    /// </summary>
    public int FailureCooldownSeconds { get; set; }

    /// <summary>
    /// Caching strategy for compiled mapping sets. Currently only <see cref="MappingSetCacheMode.InMemory"/>
    /// is supported. Reserved for future Redis/database-resident cache modes.
    /// Has no runtime effect until a second cache implementation is added.
    /// </summary>
    public MappingSetCacheMode CacheMode { get; set; } = MappingSetCacheMode.InMemory;
}

/// <summary>
/// Caching strategy for compiled mapping sets.
/// </summary>
public enum MappingSetCacheMode
{
    /// <summary>
    /// In-process ConcurrentDictionary-based cache (default).
    /// </summary>
    InMemory,
}
