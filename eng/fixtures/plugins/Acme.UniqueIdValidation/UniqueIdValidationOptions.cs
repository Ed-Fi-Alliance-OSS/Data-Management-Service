// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

// Ordinary implementer code, naming no Ed-Fi type and needing no package. It is embedded on the
// how-to page beside the validator and the plugin below, because both of those depend on it and a
// reader copying either would otherwise be shown a type that does not exist.

// embed-region: options
namespace Acme.UniqueIdValidation;

public sealed class UniqueIdValidationOptions
{
    /// <summary>
    /// The absolute base address of the external unique id system, for example
    /// "https://identity.example.org/uniqueids/". A trailing slash is not required; the plugin
    /// normalizes it when it builds the HttpClient from this value.
    /// </summary>
    public Uri? BaseAddress { get; set; }

    /// <summary>
    /// How long the validator's own HttpClient waits before giving up on a request.
    /// This is the only timeout the write path enforces; DMS itself imposes none.
    /// </summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(5);
}
// embed-region-end: options
