// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;
using FluentValidation;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.ContractTest.Provider.Tests
{

    public class FakeClientRepository : IClientRepository
    {
        public Task<ClientCreateResult> CreateClientAsync(string clientId, string clientSecret, string displayName)
        {
            // Return a successful creation result with a new UUID
            return Task.FromResult<ClientCreateResult>(new ClientCreateResult.Success(Guid.NewGuid()));

            // Or to simulate a failure:
            // return Task.FromResult<ClientCreateResult>(new ClientCreateResult.FailureUnknown("Failed to create client"));
        }

        public Task<IEnumerable<string>> GetAllClientsAsync()
        {
            return Task.FromResult<IEnumerable<string>>(new List<string> { "client1", "client2", "client3" });
        }

        public Task<ClientDeleteResult> DeleteClientAsync(string clientUuid)
        {
            // Assume similar pattern for ClientDeleteResult:
            return Task.FromResult<ClientDeleteResult>(new ClientDeleteResult.Success());
            // Or in case of failure:
            // return Task.FromResult<ClientDeleteResult>(new ClientDeleteResult.FailureUnknown("Failed to delete client"));
        }

        public Task<ClientResetResult> ResetCredentialsAsync(string clientUuid)
        {
            // Assume similar pattern for ClientResetResult:
            return Task.FromResult<ClientResetResult>(new ClientResetResult.Success("new-client-secret"));
            // Or in case of failure:
            // return Task.FromResult<ClientResetResult>(new ClientResetResult.FailureUnknown("Failed to reset credentials"));
        }
    }

}
