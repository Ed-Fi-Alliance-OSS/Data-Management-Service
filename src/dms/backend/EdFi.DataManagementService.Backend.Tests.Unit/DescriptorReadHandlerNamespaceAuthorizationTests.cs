// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.External.Security;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using DescriptorQueryCapability = EdFi.DataManagementService.Backend.External.Plans.DescriptorQueryCapability;
using DescriptorQuerySupport = EdFi.DataManagementService.Backend.External.Plans.DescriptorQuerySupport;
using ResourceReadPlan = EdFi.DataManagementService.Backend.External.Plans.ResourceReadPlan;
using ResourceWritePlan = EdFi.DataManagementService.Backend.External.Plans.ResourceWritePlan;
using SupportedDescriptorQueryField = EdFi.DataManagementService.Backend.External.Plans.SupportedDescriptorQueryField;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
[Parallelizable]
public class Given_Descriptor_Read_Handler_Namespace_Authorization
{
    private static readonly QualifiedResourceName _descriptorResource = new("Ed-Fi", "SchoolTypeDescriptor");
    private static readonly DocumentUuid _documentUuid = new(
        Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb")
    );

    [Test]
    public void It_carries_authorization_strategy_evaluators_and_a_relational_authorization_context_on_the_get_by_id_request()
    {
        var evaluators = new[] { NamespaceStrategy() };
        var context = new RelationalAuthorizationContext([], ["uri://ed-fi.org/"]);

        var request = new DescriptorGetByIdRequest(
            CreateMappingSet(SqlDialect.Pgsql),
            _descriptorResource,
            _documentUuid,
            RelationalGetRequestReadMode.ExternalResponse,
            evaluators,
            readableProfileProjectionContext: null,
            new TraceId("descriptor-get-contract"),
            context
        );

        request.AuthorizationStrategyEvaluators.Should().BeSameAs(evaluators);
        request.RelationalAuthorizationContext.NamespacePrefixes.Should().ContainSingle();
        request.RelationalAuthorizationContext.NamespacePrefixes[0].Should().Be("uri://ed-fi.org/");
    }

    [Test]
    public void It_defaults_the_get_by_id_request_relational_authorization_context_to_empty_prefixes()
    {
        var request = new DescriptorGetByIdRequest(
            CreateMappingSet(SqlDialect.Pgsql),
            _descriptorResource,
            _documentUuid,
            RelationalGetRequestReadMode.ExternalResponse,
            [],
            readableProfileProjectionContext: null,
            new TraceId("descriptor-get-contract-default")
        );

        request.AuthorizationStrategyEvaluators.Should().BeEmpty();
        request.RelationalAuthorizationContext.NamespacePrefixes.Should().BeEmpty();
    }

    [Test]
    public void It_carries_authorization_strategy_evaluators_and_a_relational_authorization_context_on_the_query_request()
    {
        var evaluators = new[] { NamespaceStrategy() };
        var context = new RelationalAuthorizationContext([], ["uri://ed-fi.org/"]);

        var request = new DescriptorQueryRequest(
            CreateMappingSet(SqlDialect.Pgsql),
            _descriptorResource,
            [],
            new PaginationParameters(Limit: 25, Offset: 0, TotalCount: false, MaximumPageSize: 500),
            evaluators,
            readableProfileProjectionContext: null,
            new TraceId("descriptor-query-contract"),
            context
        );

        request.AuthorizationStrategyEvaluators.Should().BeSameAs(evaluators);
        request.RelationalAuthorizationContext.NamespacePrefixes.Should().ContainSingle();
        request.RelationalAuthorizationContext.NamespacePrefixes[0].Should().Be("uri://ed-fi.org/");
    }

    [Test]
    public void It_defaults_the_query_request_relational_authorization_context_to_empty_prefixes()
    {
        var request = new DescriptorQueryRequest(
            CreateMappingSet(SqlDialect.Pgsql),
            _descriptorResource,
            [],
            new PaginationParameters(Limit: 25, Offset: 0, TotalCount: false, MaximumPageSize: 500),
            [],
            readableProfileProjectionContext: null,
            new TraceId("descriptor-query-contract-default")
        );

        request.AuthorizationStrategyEvaluators.Should().BeEmpty();
        request.RelationalAuthorizationContext.NamespacePrefixes.Should().BeEmpty();
    }

    [TestCase(AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly)]
    [TestCase(AuthorizationStrategyNameConstants.OwnershipBased)]
    public async Task It_fails_closed_for_descriptor_get_by_id_with_an_unsupported_strategy_without_executing_sql(
        string authorizationStrategyName
    )
    {
        var commandExecutor = new InMemoryRelationalCommandExecutor([]);
        var sut = CreateSut(commandExecutor);

        var result = await sut.HandleGetByIdAsync(
            CreateGetByIdRequest(
                namespacePrefixes: ["uri://ed-fi.org/"],
                authorizationStrategy: new AuthorizationStrategyEvaluator(
                    authorizationStrategyName,
                    [],
                    FilterOperator.And
                )
            )
        );

        result.Should().BeOfType<GetResult.GetFailureNotImplemented>();
        result
            .As<GetResult.GetFailureNotImplemented>()
            .FailureMessage.Should()
            .Contain(authorizationStrategyName);
        commandExecutor.Commands.Should().BeEmpty();
    }

    [Test]
    public async Task It_returns_security_configuration_for_descriptor_get_by_id_with_an_unknown_strategy_without_executing_sql()
    {
        const string unknownStrategyName = "UnknownDescriptorStrategy";
        var commandExecutor = new InMemoryRelationalCommandExecutor([]);
        var sut = CreateSut(commandExecutor);

        var result = await sut.HandleGetByIdAsync(
            CreateGetByIdRequest(
                namespacePrefixes: ["uri://ed-fi.org/"],
                authorizationStrategy: new AuthorizationStrategyEvaluator(
                    unknownStrategyName,
                    [],
                    FilterOperator.And
                )
            )
        );

        var failure = result.Should().BeOfType<GetResult.GetFailureSecurityConfiguration>().Subject;
        failure
            .Errors.Should()
            .Equal(
                SecurityConfigurationFailureMessages.UnknownAuthorizationStrategies([unknownStrategyName])
            );
        commandExecutor.Commands.Should().BeEmpty();
    }

    [Test]
    public async Task It_returns_namespace_403_for_descriptor_get_by_id_without_executing_sql_when_the_client_has_no_prefixes()
    {
        var commandExecutor = new InMemoryRelationalCommandExecutor([]);
        var sut = CreateSut(commandExecutor);

        var result = await sut.HandleGetByIdAsync(
            CreateGetByIdRequest(namespacePrefixes: [], authorizationStrategy: NamespaceStrategy())
        );

        result
            .Should()
            .BeOfType<GetResult.GetFailureNamespaceNotAuthorized>()
            .Which.NamespaceFailure.FailureKind.Should()
            .Be(NamespaceAuthorizationFailureKind.NoPrefixesConfigured);
        commandExecutor.Commands.Should().BeEmpty();
    }

    [Test]
    public async Task It_returns_security_configuration_500_for_descriptor_get_by_id_when_a_namespace_prefix_is_empty()
    {
        // An empty namespace prefix cannot be parameterized into a LIKE predicate. Rather than escaping as
        // an uncontrolled 500 from the parameterization factory, it fails closed as a controlled
        // security-configuration result before any SQL roundtrip.
        var commandExecutor = new InMemoryRelationalCommandExecutor([]);
        var sut = CreateSut(commandExecutor);

        var result = await sut.HandleGetByIdAsync(
            CreateGetByIdRequest(namespacePrefixes: [""], authorizationStrategy: NamespaceStrategy())
        );

        var failure = result.Should().BeOfType<GetResult.GetFailureSecurityConfiguration>().Subject;
        failure
            .Errors.Should()
            .ContainSingle()
            .Which.Should()
            .Be(NamespaceAuthorizationSecurityConfigurationMessages.InvalidNamespacePrefix);
        failure
            .Diagnostics.Should()
            .ContainSingle()
            .Which.ProviderOrPlannerFailureKind.Should()
            .Be(AuthorizationSecurityConfigurationDiagnostics.NamespaceInvalidNamespacePrefix);
        commandExecutor.Commands.Should().BeEmpty();
    }

    [Test]
    public async Task It_returns_namespace_403_for_descriptor_get_by_id_when_the_stored_namespace_does_not_match_a_prefix()
    {
        var commandExecutor = new InMemoryRelationalCommandExecutor([
            new InMemoryRelationalCommandExecution([
                InMemoryRelationalResultSet.Create(
                    CreateDescriptorRow(ns: "uri://other.org/SchoolTypeDescriptor")
                ),
            ]),
        ]);
        var sut = CreateSut(commandExecutor);

        var result = await sut.HandleGetByIdAsync(
            CreateGetByIdRequest(
                namespacePrefixes: ["uri://ed-fi.org/"],
                authorizationStrategy: NamespaceStrategy()
            )
        );

        var failure = result.Should().BeOfType<GetResult.GetFailureNamespaceNotAuthorized>().Which;
        failure.NamespaceFailure.FailureKind.Should().Be(NamespaceAuthorizationFailureKind.NamespaceMismatch);
        failure.NamespaceFailure.ValueSource.Should().Be(NamespaceAuthorizationFailureValueSource.Stored);
    }

    [Test]
    public async Task It_bypasses_namespace_authorization_for_descriptor_get_by_id_stored_document_reads()
    {
        // StoredDocument reads are internal read-modify-write fetches that bypass per-record
        // authorization, mirroring the generic single-record path. A stored namespace that an
        // external response would reject with a 403 must still be returned for a StoredDocument read.
        var commandExecutor = new InMemoryRelationalCommandExecutor([
            new InMemoryRelationalCommandExecution([
                InMemoryRelationalResultSet.Create(
                    CreateDescriptorRow(ns: "uri://other.org/SchoolTypeDescriptor")
                ),
            ]),
        ]);
        var sut = CreateSut(commandExecutor);

        var result = await sut.HandleGetByIdAsync(
            CreateGetByIdRequest(
                namespacePrefixes: ["uri://ed-fi.org/"],
                authorizationStrategy: NamespaceStrategy(),
                readMode: RelationalGetRequestReadMode.StoredDocument
            )
        );

        result.Should().BeOfType<GetResult.GetSuccess>();
    }

    [Test]
    public async Task It_returns_get_success_when_the_stored_descriptor_namespace_matches_a_configured_prefix()
    {
        var commandExecutor = new InMemoryRelationalCommandExecutor([
            new InMemoryRelationalCommandExecution([
                InMemoryRelationalResultSet.Create(
                    CreateDescriptorRow(ns: "uri://ed-fi.org/SchoolTypeDescriptor")
                ),
            ]),
        ]);
        var sut = CreateSut(commandExecutor);

        var result = await sut.HandleGetByIdAsync(
            CreateGetByIdRequest(
                namespacePrefixes: ["uri://ed-fi.org/"],
                authorizationStrategy: NamespaceStrategy()
            )
        );

        result.Should().BeOfType<GetResult.GetSuccess>();
    }

    [Test]
    public async Task It_rejects_a_case_differing_stored_descriptor_namespace_on_pgsql_to_match_the_case_sensitive_like()
    {
        // PostgreSQL LIKE is case-sensitive under the deterministic default collation the Namespace
        // column uses, so the descriptor GET-many and write paths (SQL LIKE) reject a stored namespace
        // that only case-differs from a configured prefix. The single-record GET-by-id in-memory check
        // must reject the same value so a descriptor cannot be read by id that the other paths exclude.
        var commandExecutor = new InMemoryRelationalCommandExecutor([
            new InMemoryRelationalCommandExecution([
                InMemoryRelationalResultSet.Create(
                    CreateDescriptorRow(ns: "uri://ED-FI.ORG/SchoolTypeDescriptor")
                ),
            ]),
        ]);
        var sut = CreateSut(commandExecutor);

        var result = await sut.HandleGetByIdAsync(
            CreateGetByIdRequest(
                namespacePrefixes: ["uri://ed-fi.org/"],
                authorizationStrategy: NamespaceStrategy(),
                dialect: SqlDialect.Pgsql
            )
        );

        var failure = result.Should().BeOfType<GetResult.GetFailureNamespaceNotAuthorized>().Which;
        failure.NamespaceFailure.FailureKind.Should().Be(NamespaceAuthorizationFailureKind.NamespaceMismatch);
        failure.NamespaceFailure.ValueSource.Should().Be(NamespaceAuthorizationFailureValueSource.Stored);
    }

    [Test]
    public async Task It_accepts_a_case_differing_stored_descriptor_namespace_on_mssql_to_match_the_case_insensitive_like()
    {
        // SQL Server LIKE is case-insensitive under the default collation the Namespace column inherits,
        // so the descriptor GET-many and write paths accept a stored namespace that case-differs from a
        // configured prefix. The single-record GET-by-id in-memory check must accept the same value.
        var commandExecutor = new InMemoryRelationalCommandExecutor([
            new InMemoryRelationalCommandExecution([
                InMemoryRelationalResultSet.Create(
                    CreateDescriptorRow(ns: "uri://ED-FI.ORG/SchoolTypeDescriptor")
                ),
            ]),
        ]);
        var sut = CreateSut(commandExecutor);

        var result = await sut.HandleGetByIdAsync(
            CreateGetByIdRequest(
                namespacePrefixes: ["uri://ed-fi.org/"],
                authorizationStrategy: NamespaceStrategy(),
                dialect: SqlDialect.Mssql
            )
        );

        result.Should().BeOfType<GetResult.GetSuccess>();
    }

    [Test]
    public async Task It_returns_namespace_403_stored_uninitialized_for_descriptor_get_by_id_when_the_stored_namespace_is_empty()
    {
        // An existing descriptor row whose Namespace is empty is not "not found": it fails closed as a
        // 403 with StoredNamespaceUninitialized rather than a 404, so a caller cannot tell an
        // unauthorized uninitialized row apart from a missing one.
        var commandExecutor = new InMemoryRelationalCommandExecutor([
            new InMemoryRelationalCommandExecution([
                InMemoryRelationalResultSet.Create(CreateDescriptorRow(ns: "")),
            ]),
        ]);
        var sut = CreateSut(commandExecutor);

        var result = await sut.HandleGetByIdAsync(
            CreateGetByIdRequest(
                namespacePrefixes: ["uri://ed-fi.org/"],
                authorizationStrategy: NamespaceStrategy()
            )
        );

        var failure = result.Should().BeOfType<GetResult.GetFailureNamespaceNotAuthorized>().Which;
        failure
            .NamespaceFailure.FailureKind.Should()
            .Be(NamespaceAuthorizationFailureKind.StoredNamespaceUninitialized);
        failure.NamespaceFailure.ValueSource.Should().Be(NamespaceAuthorizationFailureValueSource.Stored);
    }

    [Test]
    public async Task It_returns_namespace_403_stored_uninitialized_for_descriptor_get_by_id_when_the_stored_namespace_is_null()
    {
        // A stored null Namespace under namespace authorization is the
        // StoredNamespaceUninitialized case. The row reader exposes the null and the handler
        // emits a 403 with that failure kind rather than letting the row-reader invariant mask it
        // as a 500. The empty-string sibling case (covered above) maps to the same outcome.
        var commandExecutor = new InMemoryRelationalCommandExecutor([
            new InMemoryRelationalCommandExecution([
                InMemoryRelationalResultSet.Create(CreateDescriptorRow(ns: null)),
            ]),
        ]);
        var sut = CreateSut(commandExecutor);

        var result = await sut.HandleGetByIdAsync(
            CreateGetByIdRequest(
                namespacePrefixes: ["uri://ed-fi.org/"],
                authorizationStrategy: NamespaceStrategy()
            )
        );

        var failure = result.Should().BeOfType<GetResult.GetFailureNamespaceNotAuthorized>().Which;
        failure
            .NamespaceFailure.FailureKind.Should()
            .Be(NamespaceAuthorizationFailureKind.StoredNamespaceUninitialized);
        failure.NamespaceFailure.ValueSource.Should().Be(NamespaceAuthorizationFailureValueSource.Stored);
    }

    [Test]
    public async Task It_returns_not_exists_when_the_target_row_is_missing_under_namespace_authorization()
    {
        var commandExecutor = new InMemoryRelationalCommandExecutor([
            new InMemoryRelationalCommandExecution([InMemoryRelationalResultSet.Create()]),
        ]);
        var sut = CreateSut(commandExecutor);

        var result = await sut.HandleGetByIdAsync(
            CreateGetByIdRequest(
                namespacePrefixes: ["uri://ed-fi.org/"],
                authorizationStrategy: NamespaceStrategy()
            )
        );

        result.Should().BeOfType<GetResult.GetFailureNotExists>();
    }

    [TestCase(AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly)]
    [TestCase(AuthorizationStrategyNameConstants.OwnershipBased)]
    public async Task It_fails_closed_for_descriptor_query_with_an_unsupported_strategy_without_executing_sql(
        string authorizationStrategyName
    )
    {
        var commandExecutor = new InMemoryRelationalCommandExecutor([]);
        var sut = CreateSut(commandExecutor);

        var result = await sut.HandleQueryAsync(
            CreateQueryRequest(
                namespacePrefixes: ["uri://ed-fi.org/"],
                authorizationStrategy: new AuthorizationStrategyEvaluator(
                    authorizationStrategyName,
                    [],
                    FilterOperator.And
                )
            )
        );

        result.Should().BeOfType<QueryResult.QueryFailureNotImplemented>();
        result
            .As<QueryResult.QueryFailureNotImplemented>()
            .FailureMessage.Should()
            .Contain(authorizationStrategyName);
        commandExecutor.Commands.Should().BeEmpty();
    }

    [Test]
    public async Task It_returns_security_configuration_for_descriptor_query_with_an_unknown_strategy_without_executing_sql()
    {
        const string unknownStrategyName = "UnknownDescriptorStrategy";
        var commandExecutor = new InMemoryRelationalCommandExecutor([]);
        var sut = CreateSut(commandExecutor);

        var result = await sut.HandleQueryAsync(
            CreateQueryRequest(
                namespacePrefixes: ["uri://ed-fi.org/"],
                authorizationStrategy: new AuthorizationStrategyEvaluator(
                    unknownStrategyName,
                    [],
                    FilterOperator.And
                )
            )
        );

        var failure = result.Should().BeOfType<QueryResult.QueryFailureSecurityConfiguration>().Subject;
        failure
            .Errors.Should()
            .Equal(
                SecurityConfigurationFailureMessages.UnknownAuthorizationStrategies([unknownStrategyName])
            );
        commandExecutor.Commands.Should().BeEmpty();
    }

    [Test]
    public async Task It_returns_namespace_403_for_descriptor_query_without_executing_sql_when_the_client_has_no_prefixes()
    {
        var commandExecutor = new InMemoryRelationalCommandExecutor([]);
        var sut = CreateSut(commandExecutor);

        var result = await sut.HandleQueryAsync(
            CreateQueryRequest(namespacePrefixes: [], authorizationStrategy: NamespaceStrategy())
        );

        result
            .Should()
            .BeOfType<QueryResult.QueryFailureNamespaceNotAuthorized>()
            .Which.NamespaceFailure.FailureKind.Should()
            .Be(NamespaceAuthorizationFailureKind.NoPrefixesConfigured);
        commandExecutor.Commands.Should().BeEmpty();
    }

    [Test]
    public async Task It_emits_a_pgsql_descriptor_namespace_filter_and_binds_the_prefix_array_when_querying_with_namespace_authorization()
    {
        var commandExecutor = new InMemoryRelationalCommandExecutor([
            new InMemoryRelationalCommandExecution([
                InMemoryRelationalResultSet.Create(
                    CreateDescriptorRow(ns: "uri://ed-fi.org/SchoolTypeDescriptor")
                ),
            ]),
        ]);
        var sut = CreateSut(commandExecutor);

        var result = await sut.HandleQueryAsync(
            CreateQueryRequest(
                namespacePrefixes: ["uri://ed-fi.org/", "uri://gbisd.edu/"],
                authorizationStrategy: NamespaceStrategy(),
                dialect: SqlDialect.Pgsql
            )
        );

        result.Should().BeOfType<QueryResult.QuerySuccess>();
        commandExecutor.Commands.Should().ContainSingle();
        var command = commandExecutor.Commands[0];
        command
            .CommandText.Should()
            .Contain("INNER JOIN \"dms\".\"Descriptor\" d ON d.\"DocumentId\" = r.\"DocumentId\"");
        command
            .CommandText.Should()
            .Contain("(d.\"Namespace\" IS NOT NULL AND d.\"Namespace\" LIKE ANY(@namespacePrefixes))");
        var namespaceParameter = command.Parameters.Single(static p => p.Name == "@namespacePrefixes");
        namespaceParameter
            .Value.Should()
            .BeAssignableTo<IReadOnlyList<string>>()
            .Which.Should()
            .Equal("uri://ed-fi.org/%", "uri://gbisd.edu/%");
    }

    [Test]
    public async Task It_emits_a_mssql_descriptor_namespace_filter_and_binds_scalar_prefix_parameters_when_querying_with_namespace_authorization()
    {
        var commandExecutor = new InMemoryRelationalCommandExecutor([
            new InMemoryRelationalCommandExecution([
                InMemoryRelationalResultSet.Create(
                    CreateDescriptorRow(ns: "uri://ed-fi.org/SchoolTypeDescriptor")
                ),
            ]),
        ]);
        var sut = CreateSut(commandExecutor);

        var result = await sut.HandleQueryAsync(
            CreateQueryRequest(
                namespacePrefixes: ["uri://ed-fi.org/", "uri://gbisd.edu/"],
                authorizationStrategy: NamespaceStrategy(),
                dialect: SqlDialect.Mssql
            )
        );

        result.Should().BeOfType<QueryResult.QuerySuccess>();
        commandExecutor.Commands.Should().ContainSingle();
        var command = commandExecutor.Commands[0];
        command
            .CommandText.Should()
            .Contain("INNER JOIN [dms].[Descriptor] d ON d.[DocumentId] = r.[DocumentId]");
        command
            .CommandText.Should()
            .Contain(
                "(d.[Namespace] IS NOT NULL AND ("
                    + "d.[Namespace] LIKE @namespacePrefixes_0 ESCAPE '\\' "
                    + "OR d.[Namespace] LIKE @namespacePrefixes_1 ESCAPE '\\'"
                    + "))"
            );
        command
            .Parameters.Single(static p => p.Name == "@namespacePrefixes_0")
            .Value.Should()
            .Be("uri://ed-fi.org/%");
        command
            .Parameters.Single(static p => p.Name == "@namespacePrefixes_1")
            .Value.Should()
            .Be("uri://gbisd.edu/%");
    }

    [Test]
    public async Task It_emits_the_descriptor_namespace_filter_in_the_total_count_sql_too()
    {
        var commandExecutor = new InMemoryRelationalCommandExecutor([
            new InMemoryRelationalCommandExecution([
                InMemoryRelationalResultSet.Create(new Dictionary<string, object?> { ["count"] = 1L }),
                InMemoryRelationalResultSet.Create(
                    CreateDescriptorRow(ns: "uri://ed-fi.org/SchoolTypeDescriptor")
                ),
            ]),
        ]);
        var sut = CreateSut(commandExecutor);

        var result = await sut.HandleQueryAsync(
            CreateQueryRequest(
                namespacePrefixes: ["uri://ed-fi.org/"],
                authorizationStrategy: NamespaceStrategy(),
                totalCount: true
            )
        );

        result.Should().BeOfType<QueryResult.QuerySuccess>();
        var command = commandExecutor.Commands.Single();
        command.CommandText.Should().Contain("SELECT COUNT(1)");
        // The namespace filter shape appears twice: once in COUNT, once in the page subquery.
        var namespacePredicateOccurrences = CountOccurrences(
            command.CommandText,
            "d.\"Namespace\" IS NOT NULL AND d.\"Namespace\" LIKE ANY(@namespacePrefixes)"
        );
        namespacePredicateOccurrences.Should().Be(2);
    }

    [Test]
    public async Task It_returns_rows_unchanged_from_the_executor_so_filtering_remains_in_sql_and_not_in_c_sharp()
    {
        // The handler must not post-filter rows in C#: it relies on the SQL predicate to scope the
        // page. Returning a row that the SQL predicate would have excluded proves the handler does
        // no second-pass namespace check after fetch (a regression here would re-introduce the
        // pagination-breaking C# filter the spec rules out).
        var commandExecutor = new InMemoryRelationalCommandExecutor([
            new InMemoryRelationalCommandExecution([
                InMemoryRelationalResultSet.Create(
                    CreateDescriptorRow(ns: "uri://other.org/SchoolTypeDescriptor")
                ),
            ]),
        ]);
        var sut = CreateSut(commandExecutor);

        var result = await sut.HandleQueryAsync(
            CreateQueryRequest(
                namespacePrefixes: ["uri://ed-fi.org/"],
                authorizationStrategy: NamespaceStrategy()
            )
        );

        var success = result.Should().BeOfType<QueryResult.QuerySuccess>().Which;
        success.EdfiDocs.Should().HaveCount(1);
    }

    [Test]
    public async Task It_returns_security_configuration_500_for_descriptor_query_without_executing_sql_when_mssql_prefix_cap_is_exceeded()
    {
        var commandExecutor = new InMemoryRelationalCommandExecutor([]);
        var sut = CreateSut(commandExecutor);
        var prefixes = Enumerable
            .Range(0, NamespacePrefixLimitExceededException.MssqlScalarParameterLimit)
            .Select(static index => $"uri://prefix-{index}/")
            .ToArray();

        var result = await sut.HandleQueryAsync(
            CreateQueryRequest(
                namespacePrefixes: prefixes,
                authorizationStrategy: NamespaceStrategy(),
                dialect: SqlDialect.Mssql
            )
        );

        result.Should().BeOfType<QueryResult.QueryFailureSecurityConfiguration>();
        commandExecutor.Commands.Should().BeEmpty();
    }

    [Test]
    public async Task It_returns_an_empty_query_page_when_the_sql_predicate_returns_no_rows()
    {
        var commandExecutor = new InMemoryRelationalCommandExecutor([
            new InMemoryRelationalCommandExecution([InMemoryRelationalResultSet.Create()]),
        ]);
        var sut = CreateSut(commandExecutor);

        var result = await sut.HandleQueryAsync(
            CreateQueryRequest(
                namespacePrefixes: ["uri://ed-fi.org/"],
                authorizationStrategy: NamespaceStrategy()
            )
        );

        var success = result.Should().BeOfType<QueryResult.QuerySuccess>().Which;
        success.EdfiDocs.Should().BeEmpty();
        commandExecutor.Commands.Should().ContainSingle();
    }

    private static int CountOccurrences(string source, string token)
    {
        var count = 0;
        var index = source.IndexOf(token, StringComparison.Ordinal);
        while (index >= 0)
        {
            count++;
            index = source.IndexOf(token, index + token.Length, StringComparison.Ordinal);
        }
        return count;
    }

    private static DescriptorGetByIdRequest CreateGetByIdRequest(
        IReadOnlyList<string> namespacePrefixes,
        AuthorizationStrategyEvaluator authorizationStrategy,
        SqlDialect dialect = SqlDialect.Pgsql,
        RelationalGetRequestReadMode readMode = RelationalGetRequestReadMode.ExternalResponse
    ) =>
        new(
            CreateMappingSet(dialect),
            _descriptorResource,
            _documentUuid,
            readMode,
            [authorizationStrategy],
            readableProfileProjectionContext: null,
            new TraceId("descriptor-get-namespace"),
            new RelationalAuthorizationContext([], namespacePrefixes)
        );

    private static DescriptorQueryRequest CreateQueryRequest(
        IReadOnlyList<string> namespacePrefixes,
        AuthorizationStrategyEvaluator authorizationStrategy,
        SqlDialect dialect = SqlDialect.Pgsql,
        bool totalCount = false
    ) =>
        new(
            CreateMappingSet(dialect),
            _descriptorResource,
            [],
            new PaginationParameters(Limit: 25, Offset: 0, TotalCount: totalCount, MaximumPageSize: 500),
            [authorizationStrategy],
            readableProfileProjectionContext: null,
            new TraceId("descriptor-query-namespace"),
            new RelationalAuthorizationContext([], namespacePrefixes)
        );

    private static DescriptorReadHandler CreateSut(InMemoryRelationalCommandExecutor commandExecutor) =>
        new(
            commandExecutor,
            A.Fake<Core.Profile.IReadableProfileProjector>(),
            new EdFi.DataManagementService.Backend.Etag.EtagComposer(),
            NullLogger<DescriptorReadHandler>.Instance
        );

    private static IReadOnlyDictionary<string, object?> CreateDescriptorRow(
        string? ns = "uri://ed-fi.org/SchoolTypeDescriptor",
        long documentId = 101L,
        string? codeValue = "Charter",
        string? shortDescription = "Charter",
        string? description = "Charter school"
    ) =>
        new Dictionary<string, object?>
        {
            ["DocumentId"] = documentId,
            ["DocumentUuid"] = _documentUuid.Value,
            ["ContentVersion"] = 42L,
            ["ContentLastModifiedAt"] = new DateTimeOffset(2026, 5, 5, 14, 30, 45, TimeSpan.Zero),
            ["ResourceKeyId"] = (short)1,
            ["Namespace"] = ns,
            ["CodeValue"] = codeValue,
            ["ShortDescription"] = shortDescription,
            ["Description"] = description,
            ["EffectiveBeginDate"] = (DateOnly?)null,
            ["EffectiveEndDate"] = (DateOnly?)null,
            ["Discriminator"] = "SchoolTypeDescriptor",
        };

    private static AuthorizationStrategyEvaluator NamespaceStrategy() =>
        new(AuthorizationStrategyNameConstants.NamespaceBased, [], FilterOperator.Or);

    private static MappingSet CreateMappingSet(SqlDialect dialect)
    {
        var resourceKey = new ResourceKeyEntry(1, _descriptorResource, "1.0.0", true);
        var descriptorSchema = new DbSchemaName("dms");
        var rootTable = new DbTableModel(
            new DbTableName(descriptorSchema, "Descriptor"),
            new JsonPathExpression("$", []),
            new TableKey(
                "PK_Descriptor",
                [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)]
            ),
            [
                new DbColumnModel(
                    new DbColumnName("DocumentId"),
                    ColumnKind.ParentKeyPart,
                    new RelationalScalarType(ScalarKind.Int64),
                    false,
                    null,
                    null,
                    new ColumnStorage.Stored()
                ),
                new DbColumnModel(
                    new DbColumnName("Namespace"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.String, MaxLength: 306),
                    false,
                    new JsonPathExpression("$.namespace", []),
                    null,
                    new ColumnStorage.Stored()
                ),
            ],
            []
        );
        var resourceModel = new RelationalResourceModel(
            Resource: resourceKey.Resource,
            PhysicalSchema: descriptorSchema,
            StorageKind: ResourceStorageKind.SharedDescriptorTable,
            Root: rootTable,
            TablesInDependencyOrder: [rootTable],
            DocumentReferenceBindings: [],
            DescriptorEdgeSources: []
        );
        var descriptorMetadata = new DescriptorMetadata(
            new DescriptorColumnContract(
                Namespace: new DbColumnName("Namespace"),
                CodeValue: new DbColumnName("CodeValue"),
                ShortDescription: new DbColumnName("ShortDescription"),
                Description: new DbColumnName("Description"),
                EffectiveBeginDate: new DbColumnName("EffectiveBeginDate"),
                EffectiveEndDate: new DbColumnName("EffectiveEndDate"),
                Discriminator: null
            ),
            DiscriminatorStrategy.ResourceKeyId
        );

        return new MappingSet(
            Key: new MappingSetKey("schema-hash", dialect, "v1"),
            Model: new DerivedRelationalModelSet(
                EffectiveSchema: new EffectiveSchemaInfo(
                    ApiSchemaFormatVersion: "1.0",
                    RelationalMappingVersion: "v1",
                    EffectiveSchemaHash: "schema-hash",
                    ResourceKeyCount: 1,
                    ResourceKeySeedHash: [1, 2, 3],
                    SchemaComponentsInEndpointOrder:
                    [
                        new SchemaComponentInfo("ed-fi", "Ed-Fi", "1.0.0", false, "component-hash"),
                    ],
                    ResourceKeysInIdOrder: [resourceKey]
                ),
                Dialect: dialect,
                ProjectSchemasInEndpointOrder:
                [
                    new ProjectSchemaInfo("ed-fi", "Ed-Fi", "1.0.0", false, descriptorSchema),
                ],
                ConcreteResourcesInNameOrder:
                [
                    new ConcreteResourceModel(
                        resourceKey,
                        ResourceStorageKind.SharedDescriptorTable,
                        resourceModel,
                        descriptorMetadata
                    ),
                ],
                AbstractIdentityTablesInNameOrder: [],
                AbstractUnionViewsInNameOrder: [],
                IndexesInCreateOrder: [],
                TriggersInCreateOrder: []
            ),
            WritePlansByResource: new Dictionary<QualifiedResourceName, ResourceWritePlan>(),
            ReadPlansByResource: new Dictionary<QualifiedResourceName, ResourceReadPlan>(),
            ResourceKeyIdByResource: new Dictionary<QualifiedResourceName, short>
            {
                [resourceKey.Resource] = resourceKey.ResourceKeyId,
            },
            ResourceKeyById: new Dictionary<short, ResourceKeyEntry>
            {
                [resourceKey.ResourceKeyId] = resourceKey,
            },
            SecurableElementColumnPathsByResource: new Dictionary<
                QualifiedResourceName,
                IReadOnlyList<ResolvedSecurableElementPath>
            >()
        )
        {
            DescriptorQueryCapabilitiesByResource = new Dictionary<
                QualifiedResourceName,
                DescriptorQueryCapability
            >
            {
                [resourceKey.Resource] = new DescriptorQueryCapability(
                    new DescriptorQuerySupport.Supported(),
                    new Dictionary<string, SupportedDescriptorQueryField>(StringComparer.OrdinalIgnoreCase)
                ),
            },
        };
    }
}
