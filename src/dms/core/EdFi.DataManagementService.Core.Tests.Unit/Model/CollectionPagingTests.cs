// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Reflection;
using EdFi.DataManagementService.Core.External.Model;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Model;

[TestFixture]
[Parallelizable]
public class CollectionPagingTests
{
    private static PaginationParameters TraditionalParameters(bool totalCount) =>
        new(Limit: 25, Offset: 0, TotalCount: totalCount, MaximumPageSize: 500);

    [TestFixture]
    [Parallelizable]
    public class Given_Traditional_Paging : CollectionPagingTests
    {
        private CollectionPaging.Traditional _paging = null!;

        [SetUp]
        public void Setup()
        {
            _paging = new CollectionPaging.Traditional(TraditionalParameters(totalCount: false));
        }

        [Test]
        public void It_carries_the_pagination_parameters_unchanged()
        {
            _paging.Parameters.Should().Be(TraditionalParameters(totalCount: false));
        }

        [Test]
        public void It_is_a_collection_paging_alternative()
        {
            _paging.Should().BeAssignableTo<CollectionPaging>();
        }

        [Test]
        public void It_compares_equal_to_the_same_traditional_paging()
        {
            _paging.Should().Be(new CollectionPaging.Traditional(TraditionalParameters(totalCount: false)));
        }

        [Test]
        public void It_does_not_include_a_total_count_when_none_was_requested()
        {
            _paging.IncludesTotalCount.Should().BeFalse();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Cursor_Paging : CollectionPagingTests
    {
        private CollectionPaging.Cursor _paging = null!;

        [SetUp]
        public void Setup()
        {
            _paging = new CollectionPaging.Cursor(new CursorRange(10, 2509), new PageSize(100));
        }

        [Test]
        public void It_carries_the_typed_cursor_range()
        {
            _paging.Range.Should().Be(new CursorRange(10, 2509));
        }

        [Test]
        public void It_carries_the_page_size()
        {
            _paging.PageSize.Should().Be(new PageSize(100));
        }

        [Test]
        public void It_is_a_collection_paging_alternative()
        {
            _paging.Should().BeAssignableTo<CollectionPaging>();
        }

        [Test]
        public void It_compares_equal_to_the_same_cursor_paging()
        {
            _paging.Should().Be(new CollectionPaging.Cursor(new CursorRange(10, 2509), new PageSize(100)));
        }

        [Test]
        public void It_never_includes_a_total_count()
        {
            _paging.IncludesTotalCount.Should().BeFalse();
        }
    }

    [Test]
    public void It_includes_a_total_count_only_for_a_traditional_request_that_asked_for_one()
    {
        CollectionPaging paging = new CollectionPaging.Traditional(TraditionalParameters(totalCount: true));

        paging.IncludesTotalCount.Should().BeTrue();
    }

    [Test]
    public void It_declares_no_public_constructor_on_the_choice_itself()
    {
        typeof(CollectionPaging)
            .GetConstructors(BindingFlags.Instance | BindingFlags.Public)
            .Should()
            .BeEmpty();
    }

    [Test]
    public void It_offers_only_nested_sealed_alternatives()
    {
        IEnumerable<Type> alternatives = typeof(CollectionPaging)
            .Assembly.GetTypes()
            .Where(type =>
                typeof(CollectionPaging).IsAssignableFrom(type) && type != typeof(CollectionPaging)
            );

        alternatives
            .Should()
            .HaveCount(2)
            .And.OnlyContain(type => type.IsSealed && type.DeclaringType == typeof(CollectionPaging));
    }
}
