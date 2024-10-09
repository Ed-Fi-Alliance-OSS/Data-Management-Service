// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.Keycloak;

public record KeycloakContext(string Url, string Realm, string ClientId, string ClientSecret, string RoleClaimType, string ServiceRole);
