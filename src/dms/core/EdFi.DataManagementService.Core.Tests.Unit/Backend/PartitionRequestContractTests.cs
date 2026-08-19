// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Reflection;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Core.Backend;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Model;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Backend;

/// <summary>
/// The partition request seam. A partitions request selects identifiers and hydrates nothing, so the
/// contract has nowhere to carry a page, a projection, or token text: the backend receives typed counts
/// and sizes, and Core alone turns the returned ranges into tokens.
/// </summary>
[TestFixture]
public class PartitionRequestContractTests
{
    private static readonly Type[] _partitionRequestContracts =
    [
        typeof(IPartitionRequest),
        typeof(RelationalPartitionRequest),
    ];

    private static readonly Type[] _pagingContracts =
    [
        typeof(CollectionPaging),
        typeof(CursorRange),
        typeof(PageSize),
        typeof(PaginationParameters),
    ];

    [TestFixture]
    [Parallelizable]
    public class Given_The_Partition_Request_Contracts : PartitionRequestContractTests
    {
        /// <summary>
        /// The properties of a contract and of every interface it extends or implements. An interface's
        /// own GetProperties omits the members of the interfaces it extends, and FlattenHierarchy does
        /// not change that, so they are walked explicitly. The negative assertions depend on it: a
        /// paging or projection property inherited from a base interface would otherwise go unseen.
        /// </summary>
        private static IEnumerable<PropertyInfo> PropertiesOf(Type contract) =>
            contract
                .GetInterfaces()
                .Append(contract)
                .SelectMany(type =>
                    type.GetProperties(
                        BindingFlags.Instance
                            | BindingFlags.Public
                            | BindingFlags.NonPublic
                            | BindingFlags.FlattenHierarchy
                    )
                );

        [TestCaseSource(nameof(_partitionRequestContracts))]
        public void It_carries_the_resolved_mapping_set(Type contract)
        {
            typeof(IRequestWithMappingSet).IsAssignableFrom(contract).Should().BeTrue();
            PropertiesOf(contract)
                .Should()
                .Contain(property =>
                    property.Name == nameof(IRequestWithMappingSet.MappingSet)
                    && property.PropertyType == typeof(MappingSet)
                );
        }

        [TestCaseSource(nameof(_partitionRequestContracts))]
        public void It_carries_the_requested_count_and_minimum_size_as_typed_numbers(Type contract)
        {
            PropertiesOf(contract)
                .Should()
                .Contain(property =>
                    property.Name == nameof(IPartitionRequest.RequestedPartitionCount)
                    && property.PropertyType == typeof(int)
                )
                .And.Contain(property =>
                    property.Name == nameof(IPartitionRequest.MinimumPartitionSize)
                    && property.PropertyType == typeof(long)
                );
        }

        [TestCaseSource(nameof(_partitionRequestContracts))]
        public void It_carries_the_filters_and_authorization_inputs(Type contract)
        {
            PropertiesOf(contract)
                .Should()
                .Contain(property =>
                    property.Name == nameof(IPartitionRequest.QueryElements)
                    && property.PropertyType == typeof(QueryElement[])
                )
                .And.Contain(property =>
                    property.Name == nameof(IPartitionRequest.AuthorizationStrategyEvaluators)
                    && property.PropertyType == typeof(AuthorizationStrategyEvaluator[])
                )
                .And.Contain(property =>
                    property.Name == nameof(IPartitionRequest.AuthorizationContext)
                    && property.PropertyType == typeof(RelationalAuthorizationContext)
                )
                .And.Contain(property =>
                    property.Name == nameof(IPartitionRequest.ChangeVersionRange)
                    && property.PropertyType == typeof(ChangeVersionRange)
                );
        }

        [TestCaseSource(nameof(_partitionRequestContracts))]
        public void It_exposes_no_paging_contract(Type contract)
        {
            PropertiesOf(contract)
                .Should()
                .NotContain(property =>
                    _pagingContracts.Any(pagingContract =>
                        pagingContract.IsAssignableFrom(property.PropertyType)
                    )
                );
        }

        [TestCaseSource(nameof(_partitionRequestContracts))]
        public void It_exposes_no_token_text(Type contract)
        {
            PropertiesOf(contract)
                .Should()
                .NotContain(property => property.Name.Contains("Token", StringComparison.OrdinalIgnoreCase));
        }

        [TestCaseSource(nameof(_partitionRequestContracts))]
        public void It_exposes_no_response_projection_inputs(Type contract)
        {
            PropertiesOf(contract)
                .Should()
                .NotContain(property =>
                    property.PropertyType == typeof(ReadableProfileProjectionContext)
                    || property.PropertyType == typeof(ResponseContentCoding)
                );
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_The_Partition_Query_Handler_Contract : PartitionRequestContractTests
    {
        [Test]
        public void It_declares_one_partition_entry_point()
        {
            typeof(IPartitionQueryHandler)
                .GetMethods()
                .Select(method => method.Name)
                .Should()
                .Equal(nameof(IPartitionQueryHandler.QueryPartitions));
        }

        [Test]
        public void It_takes_a_typed_partition_request_and_returns_a_typed_partition_result()
        {
            MethodInfo queryPartitions = typeof(IPartitionQueryHandler).GetMethods()[0];

            queryPartitions.ReturnType.Should().Be<Task<PartitionResult>>();
            queryPartitions
                .GetParameters()
                .Select(parameter => parameter.ParameterType)
                .Should()
                .Equal(typeof(IPartitionRequest), typeof(CancellationToken));
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Relational_Partition_Request : PartitionRequestContractTests
    {
        /// <summary>
        /// An empty mapping set. These assertions never read it; the request contract requires one,
        /// and supplying a real instance keeps a null-forgiving literal out of the fixture.
        /// </summary>
        private static MappingSet CreateEmptyMappingSet()
        {
            EffectiveSchemaInfo effectiveSchema = new(
                ApiSchemaFormatVersion: "1.0.0",
                RelationalMappingVersion: "rmv-test",
                EffectiveSchemaHash: new string('0', 64),
                ResourceKeyCount: 0,
                ResourceKeySeedHash: [.. Enumerable.Range(1, 32).Select(value => (byte)value)],
                SchemaComponentsInEndpointOrder:
                [
                    new SchemaComponentInfo("ed-fi", "Ed-Fi", "5.0.0", false, "project-hash"),
                ],
                ResourceKeysInIdOrder: []
            );

            return new MappingSet(
                Key: new MappingSetKey(
                    effectiveSchema.EffectiveSchemaHash,
                    SqlDialect.Pgsql,
                    effectiveSchema.RelationalMappingVersion
                ),
                Model: new DerivedRelationalModelSet(
                    EffectiveSchema: effectiveSchema,
                    Dialect: SqlDialect.Pgsql,
                    ProjectSchemasInEndpointOrder:
                    [
                        new ProjectSchemaInfo("ed-fi", "Ed-Fi", "5.0.0", false, new DbSchemaName("edfi")),
                    ],
                    ConcreteResourcesInNameOrder: [],
                    AbstractIdentityTablesInNameOrder: [],
                    AbstractUnionViewsInNameOrder: [],
                    IndexesInCreateOrder: [],
                    TriggersInCreateOrder: []
                ),
                WritePlansByResource: new Dictionary<QualifiedResourceName, ResourceWritePlan>(),
                ReadPlansByResource: new Dictionary<QualifiedResourceName, ResourceReadPlan>(),
                ResourceKeyIdByResource: new Dictionary<QualifiedResourceName, short>(),
                ResourceKeyById: new Dictionary<short, ResourceKeyEntry>(),
                SecurableElementColumnPathsByResource: new Dictionary<
                    QualifiedResourceName,
                    IReadOnlyList<ResolvedSecurableElementPath>
                >()
            );
        }

        private static RelationalPartitionRequest Create(ChangeVersionRange? changeVersionRange) =>
            new(
                ResourceInfo: No.ResourceInfo,
                AuthorizationContext: new RelationalAuthorizationContext([]),
                MappingSet: CreateEmptyMappingSet(),
                QueryElements: [],
                AuthorizationStrategyEvaluators: [],
                RequestedPartitionCount: 10,
                MinimumPartitionSize: 2500,
                TraceId: new TraceId("trace"),
                ChangeVersionRange: changeVersionRange
            );

        [Test]
        public void It_is_a_partition_request()
        {
            Create(null).Should().BeAssignableTo<IPartitionRequest>();
        }

        [Test]
        public void It_normalizes_an_absent_change_version_window_to_none()
        {
            ((IPartitionRequest)Create(null)).ChangeVersionRange.Should().Be(ChangeVersionRange.None);
        }

        [Test]
        public void It_carries_a_supplied_change_version_window()
        {
            ((IPartitionRequest)Create(new ChangeVersionRange(5, 9)))
                .ChangeVersionRange.Should()
                .Be(new ChangeVersionRange(5, 9));
        }

        [Test]
        public void It_defaults_the_tenant_key_to_the_non_tenant_target()
        {
            Create(null).TenantKey.Should().BeEmpty();
        }

        [Test]
        public void It_carries_the_requested_count_and_minimum_size()
        {
            RelationalPartitionRequest request = Create(null);

            request.RequestedPartitionCount.Should().Be(10);
            request.MinimumPartitionSize.Should().Be(2500);
        }
    }
}
