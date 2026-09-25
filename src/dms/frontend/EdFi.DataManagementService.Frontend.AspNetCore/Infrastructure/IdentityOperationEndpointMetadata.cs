// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Frontend.AspNetCore.Infrastructure;

/// <summary>
/// Marker attached via <c>WithMetadata</c> to every mapped identity operation endpoint so
/// downstream concerns - the no-store response header middleware and the OpenAPI metadata and
/// Discovery wiring - can recognize an identity route without re-deriving it from the route
/// pattern.
/// </summary>
public sealed record IdentityOperationEndpointMetadata;
