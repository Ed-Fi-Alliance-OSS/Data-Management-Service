// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core.Security.Model;

/// <summary>
/// Represents the authorization metadata response, including claims and authorizations.
/// Example AuthorizationMetadataResponse:
/// {
///  "claims": [
///  {
///    "name": "http://ed-fi.org/ods/identity/claims/ed-fi/absenceEventCategoryDescriptor",
///    "authorizationId": 1
///  }],
///  "authorizations": [
///   {
///    "id": 1,
///    "actions": [
///      {
///        "name": "Read",
///        "authorizationStrategies": [
///         {
///           "name": "NoFurtherAuthorizationRequired"
///         }]
///      }]
///   }]
/// }
/// </summary>
public record AuthorizationMetadataResponse(
    List<AuthorizationMetadataResponse.Claim> Claims,
    List<AuthorizationMetadataResponse.Authorization> Authorizations
)
{
    /// <summary>
    /// Represents a claim with its associated authorization Id.
    /// </summary>
    public record Claim(string Name, int AuthorizationId);

    /// <summary>
    /// Represents an authorization with its associated actions.
    /// </summary>
    public record Authorization(int Id, Action[] Actions);

    /// <summary>
    /// Represents an action with its associated authorization strategies.
    /// </summary>
    public record Action(string Name, AuthorizationStrategy[] AuthorizationStrategies);
}
