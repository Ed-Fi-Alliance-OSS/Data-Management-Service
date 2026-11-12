// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

/// <summary>
/// A SQL Server descriptor filter wider than its <c>dms.Descriptor</c> column is answered as an invalid
/// filter value, by both the query and the partition operation, without issuing a command.
/// </summary>
public partial class Given_DescriptorReadHandler
{
    private static readonly string _overWideCodeValue = new('x', DescriptorStringColumnLengths.CodeValue + 1);

    private static WriteValidationFailure OverWideCodeValueFailure() =>
        new(new JsonPath("$.codeValue"), $"The value '{_overWideCodeValue}' is not valid for codeValue.");

    [Test]
    public async Task It_rejects_a_sql_server_query_filter_wider_than_its_column_without_a_command()
    {
        var commandExecutor = new DescriptorReadCommandExecutor([]);
        var sut = CreateHandler(commandExecutor);

        var result = await sut.HandleQueryAsync(
            CreateQueryRequest(
                SqlDialect.Mssql,
                queryElements: [CreateQueryElement("codeValue", "$.codeValue", _overWideCodeValue, "string")]
            )
        );

        result
            .Should()
            .BeOfType<QueryResult.QueryFailureValidation>()
            .Which.ValidationFailures.Should()
            .Equal(OverWideCodeValueFailure());
        commandExecutor.Commands.Should().BeEmpty();
    }

    [Test]
    public async Task It_rejects_a_sql_server_partition_filter_wider_than_its_column_without_a_command()
    {
        var commandExecutor = new DescriptorReadCommandExecutor([]);
        var sut = CreateHandler(commandExecutor);

        var result = await sut.HandlePartitionsAsync(
            CreatePartitionRequest(
                SqlDialect.Mssql,
                queryElements: [CreateQueryElement("codeValue", "$.codeValue", _overWideCodeValue, "string")]
            )
        );

        result
            .Should()
            .BeOfType<PartitionResult.PartitionFailureValidation>()
            .Which.ValidationFailures.Should()
            .Equal(OverWideCodeValueFailure());
        commandExecutor.Commands.Should().BeEmpty();
    }
}
