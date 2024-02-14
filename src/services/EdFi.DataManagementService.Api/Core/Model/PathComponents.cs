// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Api.ApiSchema.Model;

namespace EdFi.DataManagementService.Api.Core.Model;

/// <summary>
/// The important parts of the request URL in object form
/// </summary>

public record PathComponents(
  /// <summary>
  /// Project namespace, all lowercased
  /// </summary>
  ProjectNamespace ProjectNamespace,

  /// <summary>
  /// Endpoint name, which is always decapitalized and plural
  /// </summary>
  EndpointName EndpointName,

  /// <summary>
  /// The optional resource identifier, which is a document uuid
  /// </summary>
  DocumentUuid? DocumentUuid
);
