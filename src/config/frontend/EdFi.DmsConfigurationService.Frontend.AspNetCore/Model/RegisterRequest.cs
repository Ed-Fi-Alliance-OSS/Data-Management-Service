// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.RegularExpressions;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;
using FluentValidation;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Model;

public class RegisterRequest
{
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }
    public string? DisplayName { get; set; }

    public class Validator : AbstractValidator<RegisterRequest>
    {
        private readonly IClientRepository _clientRepository;

        public Validator(IClientRepository clientRepository)
        {
            _clientRepository = clientRepository;

            RuleFor(m => m.ClientId).NotEmpty();

            RuleFor(m => m.ClientId)
                .MustAsync(BeUniqueClientAsync)
                .When(m => !string.IsNullOrEmpty(m.ClientId))
                .WithMessage(
                    "Client with the same Client Id already exists. Please provide different Client Id."
                );

            RuleFor(m => m.ClientSecret).NotEmpty();
            RuleFor(m => m.ClientSecret)
                .Matches(new Regex(@"^(?=.*[a-z])(?=.*[A-Z])(?=.*\d)(?=.*[^a-zA-Z\d]).{8,12}$"))
                .When(m => !string.IsNullOrEmpty(m.ClientSecret))
                .WithMessage(
                    "Client secret must contain at least one lowercase letter, one uppercase letter, one number, and one special character, and must be 8 to 12 characters long."
                );

            RuleFor(m => m.DisplayName).NotEmpty();
        }

        private async Task<bool> BeUniqueClientAsync(string? clientId, CancellationToken cancellationToken)
        {
            var clientResult = await _clientRepository.GetAllClientsAsync();

            return clientResult switch
            {
                ClientClientsResult.Success clientSuccess
                    => !clientSuccess.ClientList.Any(c =>
                        c.Equals(clientId, StringComparison.InvariantCultureIgnoreCase)
                    ),

                ClientClientsResult.FailureKeycloak failureKeycloak
                    => throw MapKeycloakErrorToException(failureKeycloak.FailureMessage),

                _ => throw new KeycloakException("Unexpected error occurred.", KeycloakFailureType.Unknown)
            };
        }

        private KeycloakException MapKeycloakErrorToException(KeycloakError keycloakError)
        {
            return keycloakError switch
            {
                KeycloakError.KeycloakUnreachable unreachableError
                    => new KeycloakException(
                        unreachableError.FailureMessage,
                        KeycloakFailureType.Unreachable
                    ),

                KeycloakError.InvalidRealm invalidRealmError
                    => new KeycloakException(
                        invalidRealmError.FailureMessage,
                        KeycloakFailureType.InvalidRealm
                    ),

                KeycloakError.BadCredentials badCredentialsError
                    => new KeycloakException(
                        badCredentialsError.FailureMessage,
                        KeycloakFailureType.BadCredentials
                    ),

                KeycloakError.InsufficientPermissions insufficientPermissionsError
                    => new KeycloakException(
                        insufficientPermissionsError.FailureMessage,
                        KeycloakFailureType.InsufficientPermissions
                    ),

                _ => new KeycloakException("An unknown Keycloak error occurred.", KeycloakFailureType.Unknown)
            };
        }
    }
}
