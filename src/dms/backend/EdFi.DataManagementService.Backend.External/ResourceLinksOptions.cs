// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Backend.External;

/// <summary>
/// Runtime read-path configuration for document-reference link injection. Bound from the
/// <c>DataManagement:ResourceLinks</c> configuration section. Flips are restart-only — the
/// read path does not subscribe to <c>IOptionsMonitor</c>.
/// </summary>
public sealed class ResourceLinksOptions
{
    /// <summary>
    /// When <see langword="true"/> (the default) the reconstitution reference-writer emits a
    /// <c>link</c> object alongside each fully-defined document reference. When
    /// <see langword="false"/> the response-side strip pass removes any <c>link</c> objects
    /// from the projected document before serialization.
    /// </summary>
    public bool Enabled { get; set; } = true;
}
