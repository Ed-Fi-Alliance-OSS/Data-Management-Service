// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using EdFi.DmsConfigurationService.DataModel.Model.ApiClient;
using FluentAssertions;
using FluentValidation.Results;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.Model.ApiClient;

[TestFixture]
public class Given_an_api_client_insert_command_with_an_empty_data_store_id_list
{
    private ValidationResult _result = new();

    [SetUp]
    public void SetUp()
    {
        _result = new ApiClientInsertCommand.Validator().Validate(
            new ApiClientInsertCommand
            {
                ApplicationId = 1,
                Name = "Client With No Data Store",
                IsApproved = true,
                DataStoreIds = [],
            }
        );
    }

    [Test]
    public void It_passes_validation()
    {
        _result.IsValid.Should().BeTrue();
    }

    [Test]
    public void It_reports_no_data_store_ids_failure()
    {
        _result
            .Errors.Should()
            .NotContain(failure => failure.PropertyName == nameof(ApiClientInsertCommand.DataStoreIds));
    }
}

[TestFixture]
public class Given_an_api_client_insert_command_with_an_omitted_data_store_id_list
{
    private ValidationResult _result = new();

    [SetUp]
    public void SetUp()
    {
        // DataStoreIds is intentionally not assigned, exercising the command's own empty default.
        _result = new ApiClientInsertCommand.Validator().Validate(
            new ApiClientInsertCommand
            {
                ApplicationId = 1,
                Name = "Client With No Data Store",
                IsApproved = true,
            }
        );
    }

    [Test]
    public void It_passes_validation()
    {
        _result.IsValid.Should().BeTrue();
    }

    [Test]
    public void It_defaults_the_data_store_id_list_to_empty()
    {
        new ApiClientInsertCommand
        {
            ApplicationId = 1,
            Name = "Client With No Data Store",
            IsApproved = true,
        }
            .DataStoreIds.Should()
            .BeEmpty();
    }
}

[TestFixture]
public class Given_an_api_client_insert_command_with_a_null_data_store_id_list
{
    private ValidationResult _result = new();

    [SetUp]
    public void SetUp()
    {
        // An explicit JSON null binds the non-nullable property to null, so validation is the
        // only thing standing between the caller and an array dereference in the endpoint.
        _result = new ApiClientInsertCommand.Validator().Validate(
            new ApiClientInsertCommand
            {
                ApplicationId = 1,
                Name = "Client With Null Data Stores",
                IsApproved = true,
                DataStoreIds = null!,
            }
        );
    }

    [Test]
    public void It_fails_validation()
    {
        _result.IsValid.Should().BeFalse();
    }

    [Test]
    public void It_reports_a_data_store_ids_failure()
    {
        _result
            .Errors.Should()
            .Contain(failure => failure.PropertyName == nameof(ApiClientInsertCommand.DataStoreIds));
    }
}

[TestFixture]
public class Given_an_api_client_insert_command_with_data_store_ids
{
    private ValidationResult _result = new();

    [SetUp]
    public void SetUp()
    {
        _result = new ApiClientInsertCommand.Validator().Validate(
            new ApiClientInsertCommand
            {
                ApplicationId = 1,
                Name = "Client With Data Stores",
                IsApproved = true,
                DataStoreIds = [1, 2],
            }
        );
    }

    [Test]
    public void It_passes_validation()
    {
        _result.IsValid.Should().BeTrue();
    }
}

[TestFixture]
public class Given_an_api_client_insert_command_with_invalid_application_id_and_name
{
    private ValidationResult _result = new();

    [SetUp]
    public void SetUp()
    {
        _result = new ApiClientInsertCommand.Validator().Validate(
            new ApiClientInsertCommand
            {
                ApplicationId = 0,
                Name = "",
                IsApproved = true,
                DataStoreIds = [],
            }
        );
    }

    [Test]
    public void It_fails_validation()
    {
        _result.IsValid.Should().BeFalse();
    }

    [Test]
    public void It_reports_an_application_id_failure()
    {
        _result
            .Errors.Should()
            .Contain(failure => failure.PropertyName == nameof(ApiClientInsertCommand.ApplicationId));
    }

    [Test]
    public void It_reports_a_name_failure()
    {
        _result
            .Errors.Should()
            .Contain(failure => failure.PropertyName == nameof(ApiClientInsertCommand.Name));
    }

    [Test]
    public void It_reports_no_data_store_ids_failure()
    {
        _result
            .Errors.Should()
            .NotContain(failure => failure.PropertyName == nameof(ApiClientInsertCommand.DataStoreIds));
    }
}

[TestFixture]
public class Given_an_api_client_insert_command_with_a_name_over_the_maximum_length
{
    private ValidationResult _result = new();

    [SetUp]
    public void SetUp()
    {
        _result = new ApiClientInsertCommand.Validator().Validate(
            new ApiClientInsertCommand
            {
                ApplicationId = 1,
                Name = new string('a', 51),
                IsApproved = true,
                DataStoreIds = [],
            }
        );
    }

    [Test]
    public void It_fails_validation()
    {
        _result.IsValid.Should().BeFalse();
    }

    [Test]
    public void It_reports_a_name_failure()
    {
        _result
            .Errors.Should()
            .Contain(failure => failure.PropertyName == nameof(ApiClientInsertCommand.Name));
    }
}

[TestFixture]
public class Given_an_api_client_insert_command_carrying_ownership_fields
{
    private ValidationResult _result = new();

    [SetUp]
    public void SetUp()
    {
        _result = new ApiClientInsertCommand.Validator().Validate(
            new ApiClientInsertCommand
            {
                ApplicationId = 1,
                Name = "Client With Ownership Fields",
                IsApproved = true,
                DataStoreIds = [],
                AdditionalProperties = new()
                {
                    ["creatorOwnershipTokenId"] = JsonSerializer.SerializeToElement(1),
                    ["ownershipTokenIds"] = JsonSerializer.SerializeToElement(new[] { 1 }),
                },
            }
        );
    }

    [Test]
    public void It_fails_validation()
    {
        _result.IsValid.Should().BeFalse();
    }

    [Test]
    public void It_reports_a_creator_ownership_token_failure()
    {
        _result.Errors.Should().Contain(failure => failure.PropertyName == "CreatorOwnershipTokenId");
    }

    [Test]
    public void It_reports_an_ownership_token_ids_failure()
    {
        _result.Errors.Should().Contain(failure => failure.PropertyName == "OwnershipTokenIds");
    }
}

[TestFixture]
public class Given_an_api_client_update_command_with_an_empty_data_store_id_list
{
    private ValidationResult _result = new();

    [SetUp]
    public void SetUp()
    {
        _result = new ApiClientUpdateCommand.Validator().Validate(
            new ApiClientUpdateCommand
            {
                Id = 1,
                ApplicationId = 1,
                Name = "Client With No Data Store",
                IsApproved = true,
                DataStoreIds = [],
            }
        );
    }

    [Test]
    public void It_passes_validation()
    {
        _result.IsValid.Should().BeTrue();
    }

    [Test]
    public void It_reports_no_data_store_ids_failure()
    {
        _result
            .Errors.Should()
            .NotContain(failure => failure.PropertyName == nameof(ApiClientUpdateCommand.DataStoreIds));
    }
}

[TestFixture]
public class Given_an_api_client_update_command_with_an_omitted_data_store_id_list
{
    private ValidationResult _result = new();

    [SetUp]
    public void SetUp()
    {
        // An omitted list is a full replacement with no assignments, so it must validate.
        _result = new ApiClientUpdateCommand.Validator().Validate(
            new ApiClientUpdateCommand
            {
                Id = 1,
                ApplicationId = 1,
                Name = "Client With No Data Store",
                IsApproved = true,
            }
        );
    }

    [Test]
    public void It_passes_validation()
    {
        _result.IsValid.Should().BeTrue();
    }

    [Test]
    public void It_defaults_the_data_store_id_list_to_empty()
    {
        new ApiClientUpdateCommand
        {
            Id = 1,
            ApplicationId = 1,
            Name = "Client With No Data Store",
            IsApproved = true,
        }
            .DataStoreIds.Should()
            .BeEmpty();
    }
}

[TestFixture]
public class Given_an_api_client_update_command_with_a_null_data_store_id_list
{
    private ValidationResult _result = new();

    [SetUp]
    public void SetUp()
    {
        _result = new ApiClientUpdateCommand.Validator().Validate(
            new ApiClientUpdateCommand
            {
                Id = 1,
                ApplicationId = 1,
                Name = "Client With Null Data Stores",
                IsApproved = true,
                DataStoreIds = null!,
            }
        );
    }

    [Test]
    public void It_fails_validation()
    {
        _result.IsValid.Should().BeFalse();
    }

    [Test]
    public void It_reports_a_data_store_ids_failure()
    {
        _result
            .Errors.Should()
            .Contain(failure => failure.PropertyName == nameof(ApiClientUpdateCommand.DataStoreIds));
    }
}

[TestFixture]
public class Given_an_api_client_update_command_with_data_store_ids
{
    private ValidationResult _result = new();

    [SetUp]
    public void SetUp()
    {
        _result = new ApiClientUpdateCommand.Validator().Validate(
            new ApiClientUpdateCommand
            {
                Id = 1,
                ApplicationId = 1,
                Name = "Client With Data Stores",
                IsApproved = true,
                DataStoreIds = [1, 2],
            }
        );
    }

    [Test]
    public void It_passes_validation()
    {
        _result.IsValid.Should().BeTrue();
    }
}

[TestFixture]
public class Given_an_api_client_update_command_with_an_invalid_id
{
    private ValidationResult _result = new();

    [SetUp]
    public void SetUp()
    {
        _result = new ApiClientUpdateCommand.Validator().Validate(
            new ApiClientUpdateCommand
            {
                Id = 0,
                ApplicationId = 1,
                Name = "Client With No Data Store",
                IsApproved = true,
                DataStoreIds = [],
            }
        );
    }

    [Test]
    public void It_fails_validation()
    {
        _result.IsValid.Should().BeFalse();
    }

    [Test]
    public void It_reports_an_id_failure()
    {
        _result.Errors.Should().Contain(failure => failure.PropertyName == nameof(ApiClientUpdateCommand.Id));
    }

    [Test]
    public void It_reports_no_data_store_ids_failure()
    {
        _result
            .Errors.Should()
            .NotContain(failure => failure.PropertyName == nameof(ApiClientUpdateCommand.DataStoreIds));
    }
}

[TestFixture]
public class Given_an_api_client_update_command_carrying_ownership_fields
{
    private ValidationResult _result = new();

    [SetUp]
    public void SetUp()
    {
        _result = new ApiClientUpdateCommand.Validator().Validate(
            new ApiClientUpdateCommand
            {
                Id = 1,
                ApplicationId = 1,
                Name = "Client With Ownership Fields",
                IsApproved = true,
                DataStoreIds = [],
                AdditionalProperties = new()
                {
                    ["CreatorOwnershipTokenId"] = JsonSerializer.SerializeToElement(1),
                    ["OwnershipTokenIds"] = JsonSerializer.SerializeToElement(new[] { 1 }),
                },
            }
        );
    }

    [Test]
    public void It_fails_validation()
    {
        _result.IsValid.Should().BeFalse();
    }

    [Test]
    public void It_reports_a_creator_ownership_token_failure()
    {
        _result.Errors.Should().Contain(failure => failure.PropertyName == "CreatorOwnershipTokenId");
    }

    [Test]
    public void It_reports_an_ownership_token_ids_failure()
    {
        _result.Errors.Should().Contain(failure => failure.PropertyName == "OwnershipTokenIds");
    }
}
