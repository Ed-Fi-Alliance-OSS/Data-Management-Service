// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Dapper;
using EdFi.DmsConfigurationService.Backend.Postgresql.Repositories;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.Backend.Services;
using EdFi.DmsConfigurationService.DataModel.Model.ApiClient;
using EdFi.DmsConfigurationService.DataModel.Model.Application;
using EdFi.DmsConfigurationService.DataModel.Model.Vendor;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.Tests.Integration;

/// <summary>
/// Seeds one vendor owning two applications and three clients, so the state read can be checked
/// for completeness, for the documented ordering, and for exclusion of another vendor's clients.
/// The extra client is added to the LOWER-numbered application last, so it takes the highest row
/// id: ordering by application id then row id therefore differs from ordering by row id alone,
/// and an implementation that dropped the application from the sort would be caught.
/// </summary>
public abstract class VendorConsistencyOperationTestBase : DatabaseTest
{
    protected readonly IVendorRepository _vendorRepository = new VendorRepository(
        Configuration.DatabaseOptions,
        NullLogger<VendorRepository>.Instance,
        new TestAuditContext(),
        new TenantContextProvider()
    );

    protected readonly IApplicationRepository _applicationRepository = new ApplicationRepository(
        Configuration.DatabaseOptions,
        NullLogger<ApplicationRepository>.Instance,
        new TestAuditContext(),
        new TenantContextProvider()
    );

    protected readonly IApiClientRepository _apiClientRepository = new ApiClientRepository(
        Configuration.DatabaseOptions,
        NullLogger<ApiClientRepository>.Instance,
        new TestAuditContext(),
        new TenantContextProvider()
    );

    protected int _vendorId;
    protected int _lowerApplicationId;
    protected int _higherApplicationId;
    protected string _lowerApplicationClientId = null!;
    protected Guid _lowerApplicationClientUuid;
    protected int _lowerApplicationClientRowId;
    protected string _higherApplicationClientId = null!;
    protected Guid _higherApplicationClientUuid;
    protected int _higherApplicationClientRowId;
    protected string _additionalClientId = null!;
    protected Guid _additionalClientUuid;
    protected int _additionalClientRowId;

    protected int _otherVendorId;
    protected string _otherVendorClientId = null!;

    [SetUp]
    public async Task SeedVendorAggregateAsync()
    {
        _lowerApplicationClientId = Guid.NewGuid().ToString();
        _lowerApplicationClientUuid = Guid.NewGuid();
        _higherApplicationClientId = Guid.NewGuid().ToString();
        _higherApplicationClientUuid = Guid.NewGuid();
        _additionalClientId = Guid.NewGuid().ToString();
        _additionalClientUuid = Guid.NewGuid();
        _otherVendorClientId = Guid.NewGuid().ToString();

        _vendorId = await InsertVendorAsync("Vendor Consistency Company", "PrefixB,PrefixA");

        _lowerApplicationId = await InsertApplicationAsync(
            _vendorId,
            "Lower Application",
            _lowerApplicationClientId,
            _lowerApplicationClientUuid
        );
        _higherApplicationId = await InsertApplicationAsync(
            _vendorId,
            "Higher Application",
            _higherApplicationClientId,
            _higherApplicationClientUuid
        );

        // Added last, so it holds the highest ApiClient row id while belonging to the
        // lowest-numbered application: ordering by row id alone would place it last.
        var additionalClientResult = await _apiClientRepository.InsertApiClient(
            new ApiClientInsertCommand
            {
                ApplicationId = _lowerApplicationId,
                Name = "Additional Client",
                IsApproved = true,
                DataStoreIds = [],
            },
            new() { ClientId = _additionalClientId, ClientUuid = _additionalClientUuid }
        );
        additionalClientResult.Should().BeOfType<ApiClientInsertResult.Success>();
        _additionalClientRowId = ((ApiClientInsertResult.Success)additionalClientResult).Id;

        _lowerApplicationClientRowId = await ReadClientRowIdAsync(_lowerApplicationClientId);
        _higherApplicationClientRowId = await ReadClientRowIdAsync(_higherApplicationClientId);

        _otherVendorId = await InsertVendorAsync("Other Consistency Company", "OtherPrefix");
        await InsertApplicationAsync(
            _otherVendorId,
            "Other Vendor Application",
            _otherVendorClientId,
            Guid.NewGuid()
        );
    }

    protected async Task<int> InsertVendorAsync(string company, string namespacePrefixes)
    {
        var result = await _vendorRepository.InsertVendor(
            new VendorInsertCommand
            {
                Company = company,
                ContactEmailAddress = "test@test.com",
                ContactName = "Fake Name",
                NamespacePrefixes = namespacePrefixes,
            }
        );
        result.Should().BeOfType<VendorInsertResult.Success>();
        return ((VendorInsertResult.Success)result).Id;
    }

    protected async Task<int> InsertApplicationAsync(
        int vendorId,
        string applicationName,
        string clientId,
        Guid clientUuid
    )
    {
        var result = await _applicationRepository.InsertApplication(
            new ApplicationInsertCommand
            {
                ApplicationName = applicationName,
                VendorId = vendorId,
                ClaimSetName = "ConsistencyClaimSet",
                EducationOrganizationIds = [100],
                DataStoreIds = [],
                ProfileIds = [],
            },
            new() { ClientId = clientId, ClientUuid = clientUuid }
        );
        result.Should().BeOfType<ApplicationInsertResult.Success>();
        return ((ApplicationInsertResult.Success)result).Id;
    }

    protected async Task<int> ReadClientRowIdAsync(string clientId) =>
        await Connection!.ExecuteScalarAsync<int>(
            """SELECT "Id" FROM "dmscs"."ApiClient" WHERE "ClientId" = @ClientId;""",
            new { ClientId = clientId }
        );

    protected static VendorUpdateState StateOf(VendorUpdateStateResult result)
    {
        result.Should().BeOfType<VendorUpdateStateResult.Success>();
        return ((VendorUpdateStateResult.Success)result).State;
    }
}

[TestFixture]
public class Given_a_vendor_update_state_read : VendorConsistencyOperationTestBase
{
    private VendorUpdateStateResult _result = null!;

    [SetUp]
    public async Task Act() => _result = await _vendorRepository.GetVendorUpdateState(_vendorId);

    [Test]
    public void It_returns_the_vendor_scalars()
    {
        VendorUpdateState state = StateOf(_result);
        state.Company.Should().Be("Vendor Consistency Company");
        state.ContactName.Should().Be("Fake Name");
        state.ContactEmailAddress.Should().Be("test@test.com");
    }

    [Test]
    public void It_returns_the_stored_namespace_prefixes() =>
        StateOf(_result).NamespacePrefixes.Split(',').Should().BeEquivalentTo("PrefixA", "PrefixB");

    [Test]
    public void It_returns_every_client_the_vendor_owns() =>
        StateOf(_result)
            .Clients.Select(client => client.Id)
            .Should()
            .BeEquivalentTo([
                _lowerApplicationClientRowId,
                _additionalClientRowId,
                _higherApplicationClientRowId,
            ]);

    [Test]
    public void It_orders_the_clients_by_application_then_row_id() =>
        StateOf(_result)
            .Clients.Select(client => (client.ApplicationId, client.Id))
            .Should()
            .Equal(
                (_lowerApplicationId, _lowerApplicationClientRowId),
                (_lowerApplicationId, _additionalClientRowId),
                (_higherApplicationId, _higherApplicationClientRowId)
            );

    /// <summary>
    /// The seed deliberately gives the additional client the highest row id while it belongs to
    /// the lowest-numbered application, so ordering by row id alone produces a different
    /// sequence and cannot satisfy the assertion above by accident.
    /// </summary>
    [Test]
    public void It_does_not_merely_order_by_row_id() =>
        StateOf(_result).Clients.Select(client => client.Id).Should().NotBeInAscendingOrder();

    [Test]
    public void It_carries_each_clients_stable_identity()
    {
        VendorApiClient client = StateOf(_result)
            .Clients.Single(candidate => candidate.Id == _lowerApplicationClientRowId);
        client.ClientId.Should().Be(_lowerApplicationClientId);
        client.ClientUuid.Should().Be(_lowerApplicationClientUuid);
        client.ApplicationId.Should().Be(_lowerApplicationId);
    }

    [Test]
    public void It_excludes_another_vendors_clients() =>
        StateOf(_result).Clients.Should().NotContain(client => client.ClientId == _otherVendorClientId);
}

[TestFixture]
public class Given_a_vendor_update_state_read_for_a_vendor_with_no_clients
    : VendorConsistencyOperationTestBase
{
    private VendorUpdateStateResult _result = null!;
    private int _emptyVendorId;

    [SetUp]
    public async Task Act()
    {
        _emptyVendorId = await InsertVendorAsync("Empty Consistency Company", "EmptyPrefix");
        _result = await _vendorRepository.GetVendorUpdateState(_emptyVendorId);
    }

    [Test]
    public void It_returns_the_vendor() => StateOf(_result).Company.Should().Be("Empty Consistency Company");

    [Test]
    public void It_returns_an_empty_client_set() => StateOf(_result).Clients.Should().BeEmpty();
}

[TestFixture]
public class Given_a_vendor_update_state_read_for_a_vendor_with_no_namespace_prefixes
    : VendorConsistencyOperationTestBase
{
    private VendorUpdateStateResult _result = null!;

    [SetUp]
    public async Task Act()
    {
        await Connection!.ExecuteAsync(
            """DELETE FROM "dmscs"."VendorNamespacePrefix" WHERE "VendorId" = @VendorId;""",
            new { VendorId = _vendorId }
        );
        _result = await _vendorRepository.GetVendorUpdateState(_vendorId);
    }

    [Test]
    public void It_returns_an_empty_prefix_string() => StateOf(_result).NamespacePrefixes.Should().BeEmpty();

    [Test]
    public void It_still_returns_the_clients() => StateOf(_result).Clients.Should().HaveCount(3);
}

[TestFixture]
public class Given_a_vendor_update_state_read_for_a_missing_vendor : VendorConsistencyOperationTestBase
{
    private VendorUpdateStateResult _result = null!;

    [SetUp]
    public async Task Act() => _result = await _vendorRepository.GetVendorUpdateState(999999);

    [Test]
    public void It_returns_not_exists() =>
        _result.Should().BeOfType<VendorUpdateStateResult.FailureNotExists>();
}

[TestFixture]
public class Given_a_vendor_update_state_read_during_an_uncommitted_update
    : VendorConsistencyOperationTestBase
{
    private bool _completedWhileWriterHeldLock;
    private VendorUpdateStateResult _result = null!;

    [SetUp]
    public async Task Act()
    {
        await using var writer = await Connection!.BeginTransactionAsync();
        await Connection.ExecuteAsync(
            """UPDATE "dmscs"."Vendor" SET "Company" = 'Changed In Flight' WHERE "Id" = @Id;""",
            new { Id = _vendorId },
            writer
        );

        Task<VendorUpdateStateResult> reading = _vendorRepository.GetVendorUpdateState(_vendorId);
        await Task.Delay(300);
        _completedWhileWriterHeldLock = reading.IsCompleted;

        await writer.CommitAsync();
        _result = await reading;
    }

    [Test]
    public void It_waits_for_the_in_flight_transaction() => _completedWhileWriterHeldLock.Should().BeFalse();

    [Test]
    public void It_reads_the_committed_state() => StateOf(_result).Company.Should().Be("Changed In Flight");
}

public abstract class ForeignTenantVendorOperationTestBase : VendorConsistencyOperationTestBase
{
    protected int _foreignVendorId;

    [SetUp]
    public async Task SeedForeignTenantVendorAsync()
    {
        long foreignTenantId = await Connection!.ExecuteScalarAsync<long>(
            """INSERT INTO "dmscs"."Tenant" ("Name") VALUES (@Name) RETURNING "Id";""",
            new { Name = $"foreign-tenant-{Guid.NewGuid():N}" }
        );

        var foreignProvider = new TenantContextProvider
        {
            Context = new TenantContext.Multitenant(foreignTenantId, "foreign-tenant"),
        };

        IVendorRepository foreignVendorRepository = new VendorRepository(
            Configuration.DatabaseOptions,
            NullLogger<VendorRepository>.Instance,
            new TestAuditContext(),
            foreignProvider
        );
        var vendorResult = await foreignVendorRepository.InsertVendor(
            new VendorInsertCommand
            {
                Company = "Foreign Tenant Company",
                ContactEmailAddress = "test@test.com",
                ContactName = "Fake Name",
                NamespacePrefixes = "ForeignPrefix",
            }
        );
        vendorResult.Should().BeOfType<VendorInsertResult.Success>();
        _foreignVendorId = ((VendorInsertResult.Success)vendorResult).Id;
    }
}

[TestFixture]
public class Given_a_vendor_update_state_read_against_a_foreign_tenant_vendor
    : ForeignTenantVendorOperationTestBase
{
    private VendorUpdateStateResult _result = null!;

    [SetUp]
    public async Task Act() => _result = await _vendorRepository.GetVendorUpdateState(_foreignVendorId);

    [Test]
    public void It_hides_the_foreign_vendor() =>
        _result.Should().BeOfType<VendorUpdateStateResult.FailureNotExists>();
}
