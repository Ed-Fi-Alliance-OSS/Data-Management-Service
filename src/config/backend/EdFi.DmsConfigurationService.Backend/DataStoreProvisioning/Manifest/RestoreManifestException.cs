// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.DataStoreProvisioning.Manifest;

/// <summary>
/// A restore manifest is not a document of the version-1 contract: it is empty, not JSON, not
/// well-formed Unicode, or violates a shape rule, including an inventory that is not canonical or whose
/// recomputed hash differs from the recorded one. It is a verdict about the package, which the catalog
/// reports as <c>ManifestInvalid</c>. The message names the failed rule and the field or entry
/// position only; it never echoes a value from the document.
/// </summary>
public sealed class RestoreManifestException(string message) : Exception(message);
