// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.Repositories;

public interface IClientRepository
{
    public Task<ClientCreateResult> CreateClientAsync(
        string clientId,
        string clientSecret,
        string displayName
    );

    public Task<ClientClientsResult> GetAllClientsAsync();

    public Task<ClientDeleteResult> DeleteClientAsync(string clientUuid);

    public Task<ClientResetResult> ResetCredentialsAsync(string clientUuid);
}

public record ClientCreateResult
{
    public record Success(Guid ClientUuid) : ClientCreateResult;

    public record FailureUnknown(string FailureMessage) : ClientCreateResult();

    public record FailureKeycloak(string FailureMessage) : ClientCreateResult();
}

public record ClientDeleteResult
{
    public record Success() : ClientDeleteResult;

    public record FailureUnknown(string FailureMessage) : ClientDeleteResult();

    public record FailureKeycloak(string FailureMessage) : ClientDeleteResult();
}

public record ClientResetResult
{
    public record Success(string ClientSecret) : ClientResetResult;

    public record FailureUnknown(string FailureMessage) : ClientResetResult();

    public record FailureKeycloak(string FailureMessage) : ClientResetResult();
}

public record ClientClientsResult
{
    public record Success(IEnumerable<string> ClientList) : ClientClientsResult;
    public record FailureKeycloak(string FailureMessage) : ClientClientsResult;
    public record KeycloakUnreachable(string FailureMessage) : ClientClientsResult;
    public record InvalidRealm(string FailureMessage) : ClientClientsResult;
    public record BadCredentials(string FailureMessage) : ClientClientsResult;
    public record InsufficientPermissions(string FailureMessage) : ClientClientsResult;
}

