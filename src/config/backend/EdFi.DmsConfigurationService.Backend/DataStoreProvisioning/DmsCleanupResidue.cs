// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;

/// <summary>
/// One resource cleanup could not remove. <paramref name="ResourceId"/> is the generated name of
/// the resource (an operation directory, a staged file name, a database name); it is never a secret, a
/// path containing credentials, or a value taken from a request.
/// </summary>
public sealed record DmsCleanupResidue(DmsResidueKind Kind, string ResourceId);
