// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.Mssql.OpenIddict.Repositories;
using FluentAssertions;

namespace EdFi.DmsConfigurationService.Backend.Mssql.Tests.Integration;

public class OpenIddictDataRepositoryTests : DatabaseTest
{
    [TestFixture]
    public class Given_An_Inserted_Openiddict_Application : OpenIddictDataRepositoryTests
    {
        private OpenIddictDataRepository _repository = null!;
        private readonly Guid _applicationId = Guid.NewGuid();
        private readonly Guid _scopeId = Guid.NewGuid();
        private readonly Guid _roleId = Guid.NewGuid();

        [SetUp]
        public async Task Setup()
        {
            _repository = new OpenIddictDataRepository(MssqlTestConfiguration.DatabaseOptions);

            await _repository.ExecuteInTransactionAsync(
                async (connection, transaction) =>
                {
                    await _repository.InsertApplicationAsync(
                        _applicationId,
                        "integration-test-client",
                        "hashed-secret",
                        "Integration Test Client",
                        ["token", "authorization"],
                        ["require_pkce"],
                        "confidential",
                        """[{"claim.name":"namespacePrefixes","claim.value":"uri://ed-fi.org","jsonType.label":"String"}]""",
                        connection,
                        transaction
                    );
                    await _repository.InsertScopeAsync(
                        _scopeId,
                        "edfi_admin_api/full_access",
                        connection,
                        transaction
                    );
                    await _repository.InsertApplicationScopeAsync(
                        _applicationId,
                        _scopeId,
                        connection,
                        transaction
                    );
                    await _repository.InsertRoleAsync(_roleId, "cms-client", connection, transaction);
                    await _repository.InsertClientRoleAsync(_applicationId, _roleId, connection, transaction);
                }
            );
        }

        [Test]
        public async Task It_round_trips_json_array_columns_as_arrays()
        {
            var application = await _repository.GetApplicationByClientIdAsync("integration-test-client");

            application.Should().NotBeNull();
            application!.Permissions.Should().BeEquivalentTo("token", "authorization");
            application.Requirements.Should().BeEquivalentTo("require_pkce");
            application.ProtocolMappers.Should().Contain("namespacePrefixes");
        }

        [Test]
        public async Task It_returns_linked_scopes_and_roles()
        {
            var application = await _repository.GetApplicationByClientIdAsync("integration-test-client");
            var roles = await _repository.GetClientRolesAsync(_applicationId);

            application!.Scopes.Should().BeEquivalentTo("edfi_admin_api/full_access");
            roles.Should().BeEquivalentTo("cms-client");
        }

        [Test]
        public async Task It_defaults_is_approved_to_true_without_api_clients()
        {
            var application = await _repository.GetApplicationByClientIdAsync("integration-test-client");

            application!.IsApproved.Should().BeTrue();
        }
    }
}
