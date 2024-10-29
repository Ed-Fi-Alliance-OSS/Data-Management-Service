// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.Repositories;

public interface IClientRepository
{
    public Task<bool> CreateClientAsync(string clientId, string clientSecret, string displayName);

    public Task<IEnumerable<string>> GetAllClientsAsync();

    public Task<bool> DeleteClientAsync(string clientId);
}
