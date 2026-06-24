// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Plans;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Plans.Tests.Unit;

[TestFixture]
public class Given_PageReconstitutionContext_With_A_Multi_Document_Page
{
    private PageReconstitutionContext _context = null!;
    private DbTableName _rootTable = default;
    private DbTableName _addressTable = default;
    private DbTableName _periodTable = default;

    [SetUp]
    public void SetUp()
    {
        var pageData = PageReconstitutionContextTestData.CreateHappyPathPage();

        _context = PageReconstitutionContext.Build(pageData.ReadPlan, pageData.HydratedPage);
        _rootTable = pageData.RootTable;
        _addressTable = pageData.AddressTable;
        _periodTable = pageData.PeriodTable;
    }

    [Test]
    public void It_should_preserve_document_metadata_order_even_when_root_rows_are_hydrated_in_a_different_order()
    {
        _context.DocumentsInOrder.Select(document => document.DocumentId).Should().Equal(101L, 202L);
    }

    [Test]
    public void It_should_build_a_single_descriptor_lookup_for_the_page()
    {
        _context.DescriptorUrisById.Should().HaveCount(2);
        _context
            .GetDescriptorUriOrThrow(601L)
            .Should()
            .Be("uri://ed-fi.org/SchoolCategoryDescriptor#Alternative");
        _context
            .GetDescriptorUriOrThrow(602L)
            .Should()
            .Be("uri://ed-fi.org/SchoolCategoryDescriptor#Charter");
    }

    [Test]
    public void It_should_attach_child_rows_to_their_immediate_parents()
    {
        var firstDocument = _context.DocumentsInOrder[0];
        var firstAddress = _context.GetRowOrThrow(_addressTable, new ScopeKey([101L]));
        var nestedPeriod = _context.GetRowOrThrow(_periodTable, new ScopeKey([101L]));

        firstDocument.RootRow.GetImmediateChildren(_addressTable).Should().HaveCount(2);
        firstAddress.Parent.Should().BeSameAs(firstDocument.RootRow);
        nestedPeriod.Parent.Should().BeSameAs(firstAddress);
    }

    [Test]
    public void It_should_order_root_collection_children_by_ordinal_for_each_parent()
    {
        var firstDocument = _context.DocumentsInOrder[0];
        var secondDocument = _context.DocumentsInOrder[1];

        firstDocument
            .RootRow.GetImmediateChildren(_addressTable)
            .Select(child => child.Row[3])
            .Should()
            .Equal("East City", "North City");

        secondDocument
            .RootRow.GetImmediateChildren(_addressTable)
            .Select(child => child.Row[3])
            .Should()
            .Equal("South City");
    }

    [Test]
    public void It_should_order_nested_collection_children_by_ordinal_for_each_parent()
    {
        var eastCityAddress = _context.GetRowOrThrow(_addressTable, new ScopeKey([102L]));

        eastCityAddress
            .GetImmediateChildren(_periodTable)
            .Select(child => child.Row[4])
            .Should()
            .Equal("2023-09-01", "2024-01-05");
    }

    [Test]
    public void It_should_use_table_qualified_physical_identity_lookups()
    {
        var rootRow = _context.GetRowOrThrow(_rootTable, new ScopeKey([101L]));
        var addressRow = _context.GetRowOrThrow(_addressTable, new ScopeKey([101L]));
        var periodRow = _context.GetRowOrThrow(_periodTable, new ScopeKey([101L]));

        rootRow.Table.Should().Be(_rootTable);
        addressRow.Table.Should().Be(_addressTable);
        periodRow.Table.Should().Be(_periodTable);
        rootRow.Should().NotBeSameAs(addressRow);
        addressRow.Should().NotBeSameAs(periodRow);
    }

    [Test]
    public void It_should_fail_when_a_document_lookup_is_missing()
    {
        Action act = () => _context.GetDocumentOrThrow(303L);

        act.Should()
            .Throw<KeyNotFoundException>()
            .WithMessage("Page reconstitution context for 'Ed-Fi.School' does not contain DocumentId 303.");
    }

    [Test]
    public void It_should_fail_when_a_physical_row_lookup_is_missing()
    {
        Action act = () =>
            _context.GetRowOrThrow(_addressTable, new ScopeKey(["missing-address", "alternate"]));

        act.Should()
            .Throw<KeyNotFoundException>()
            .WithMessage(
                $"Page reconstitution context for 'Ed-Fi.School' does not contain table '{_addressTable}' row [\"missing-address\", \"alternate\"]."
            );
    }

    [Test]
    public void It_should_fail_when_a_descriptor_lookup_is_missing()
    {
        Action act = () => _context.GetDescriptorUriOrThrow(999L);

        act.Should()
            .Throw<KeyNotFoundException>()
            .WithMessage(
                "Page reconstitution context for 'Ed-Fi.School' does not contain descriptor ID 999."
            );
    }
}

[TestFixture]
public class Given_PageReconstitutionContext_With_Conflicting_Descriptor_Uri_Rows
{
    private Exception _exception = null!;

    [SetUp]
    public void SetUp()
    {
        var pageData = PageReconstitutionContextTestData.CreatePageWithConflictingDescriptorRows();

        _exception = Assert.Throws<InvalidOperationException>(() =>
            PageReconstitutionContext.Build(pageData.ReadPlan, pageData.HydratedPage)
        )!;
    }

    [Test]
    public void It_should_fail_fast()
    {
        _exception
            .Message.Should()
            .Be(
                "Cannot build page reconstitution context: descriptor hydration returned conflicting URIs for descriptor ID 601."
            );
    }
}

[TestFixture]
public class Given_PageReconstitutionContext_With_A_Table_That_Does_Not_Define_A_Root_Scope_Locator
{
    private Exception _exception = null!;

    [SetUp]
    public void SetUp()
    {
        var pageData = PageReconstitutionContextTestData.CreateHappyPathPage();
        var compiledPlan = PageReconstitutionContextTestData.CreateCompiledPlanWithoutRootScopeLocator(
            pageData.ReadPlan
        );

        _exception = Assert.Throws<InvalidOperationException>(() =>
            PageReconstitutionContext.Build(compiledPlan, pageData.HydratedPage)
        )!;
    }

    [Test]
    public void It_should_fail_fast()
    {
        _exception
            .Message.Should()
            .Be(
                "Cannot build page reconstitution context: table 'edfi.School' does not define a root scope locator."
            );
    }
}

[TestFixture]
public class Given_PageReconstitutionContext_With_A_Table_That_Does_Not_Define_A_Physical_Row_Identity
{
    private Exception _exception = null!;

    [SetUp]
    public void SetUp()
    {
        var pageData = PageReconstitutionContextTestData.CreateHappyPathPage();
        var compiledPlan = PageReconstitutionContextTestData.CreateCompiledPlanWithoutPhysicalRowIdentity(
            pageData.ReadPlan
        );

        _exception = Assert.Throws<InvalidOperationException>(() =>
            PageReconstitutionContext.Build(compiledPlan, pageData.HydratedPage)
        )!;
    }

    [Test]
    public void It_should_fail_fast()
    {
        _exception
            .Message.Should()
            .Be(
                "Cannot build page reconstitution context: table 'edfi.School' does not define a physical row identity."
            );
    }
}

[TestFixture]
public class Given_PageReconstitutionContext_With_A_Table_That_Does_Not_Define_An_Immediate_Parent_Locator
{
    private Exception _exception = null!;

    [SetUp]
    public void SetUp()
    {
        var pageData = PageReconstitutionContextTestData.CreateHappyPathPage();
        var compiledPlan =
            PageReconstitutionContextTestData.CreateCompiledPlanWithoutImmediateParentScopeLocator(
                pageData.ReadPlan
            );

        _exception = Assert.Throws<InvalidOperationException>(() =>
            PageReconstitutionContext.Build(compiledPlan, pageData.HydratedPage)
        )!;
    }

    [Test]
    public void It_should_fail_fast()
    {
        _exception
            .Message.Should()
            .Be(
                "Cannot build page reconstitution context: table 'edfi.SchoolAddress' does not define a immediate parent locator."
            );
    }
}

[TestFixture]
public class Given_PageReconstitutionContext_With_A_Child_Table_Before_Its_Parent
{
    private Exception _exception = null!;

    [SetUp]
    public void SetUp()
    {
        var pageData = PageReconstitutionContextTestData.CreateHappyPathPage();
        var compiledPlan = PageReconstitutionContextTestData.CreateCompiledPlanWithAddressBeforeRoot(
            pageData.ReadPlan
        );

        _exception = Assert.Throws<InvalidOperationException>(() =>
            PageReconstitutionContext.Build(compiledPlan, pageData.HydratedPage)
        )!;
    }

    [Test]
    public void It_should_fail_fast()
    {
        _exception
            .Message.Should()
            .Be(
                "Cannot build page reconstitution context for 'Ed-Fi.School': parent table 'edfi.School' was not available before child table 'edfi.SchoolAddress'."
            );
    }
}

[TestFixture]
public class Given_PageReconstitutionContext_With_An_Empty_Child_Table_Before_Its_Parent
{
    private Exception _exception = null!;

    [SetUp]
    public void SetUp()
    {
        var pageData = PageReconstitutionContextTestData.CreatePageWithoutAddressRows();
        var compiledPlan = PageReconstitutionContextTestData.CreateCompiledPlanWithAddressBeforeRoot(
            pageData.ReadPlan
        );

        _exception = Assert.Throws<InvalidOperationException>(() =>
            PageReconstitutionContext.Build(compiledPlan, pageData.HydratedPage)
        )!;
    }

    [Test]
    public void It_should_fail_fast()
    {
        _exception
            .Message.Should()
            .Be(
                "Cannot build page reconstitution context for 'Ed-Fi.School': parent table 'edfi.School' was not available when ordering child table 'edfi.SchoolAddress'."
            );
    }
}

[TestFixture]
public class Given_RowNode_With_An_Already_Attached_Child_Row
{
    private Exception _exception = null!;

    [SetUp]
    public void SetUp()
    {
        var pageData = PageReconstitutionContextTestData.CreateHappyPathPage();
        var compiledPlan = CompiledReconstitutionPlanCache.GetOrBuild(pageData.ReadPlan);
        var rootTablePlan = compiledPlan.GetTablePlanOrThrow(pageData.RootTable);
        var addressTablePlan = compiledPlan.GetTablePlanOrThrow(pageData.AddressTable);
        var firstParent = new RowNode(rootTablePlan, [101L, "First School"], new ScopeKey([101L]), 101L);
        var secondParent = new RowNode(rootTablePlan, [202L, "Second School"], new ScopeKey([202L]), 202L);
        var child = new RowNode(addressTablePlan, [101L, 101L, 1, "North City"], new ScopeKey([101L]), 101L);

        firstParent.AttachChild(child);

        _exception = Assert.Throws<InvalidOperationException>(() => secondParent.AttachChild(child))!;
    }

    [Test]
    public void It_should_fail_fast()
    {
        _exception
            .Message.Should()
            .Be(
                "Cannot attach row from table 'edfi.SchoolAddress' more than once. Physical row identity: [101]."
            );
    }
}

[TestFixture]
public class Given_PageReconstitutionContext_With_Duplicate_Document_Link_Lookup_Rows
{
    private const long LookupDocumentId = 901L;
    private const short ResourceKeyId = 7;
    private static readonly Guid _documentUuid = Guid.Parse("11112222-3333-4444-5555-666677778888");

    private PageReconstitutionContext _context = null!;

    [SetUp]
    public void SetUp()
    {
        var pageData = PageReconstitutionContextTestData.CreatePageWithDocumentLinkLookup([
            new DocumentReferenceLookupRow(LookupDocumentId, _documentUuid, ResourceKeyId),
            new DocumentReferenceLookupRow(LookupDocumentId, _documentUuid, ResourceKeyId),
        ]);

        _context = PageReconstitutionContext.Build(pageData.ReadPlan, pageData.HydratedPage);
    }

    [Test]
    public void It_should_deduplicate_identical_lookup_rows()
    {
        _context.DocumentLinkLookupById.Should().ContainSingle();
        _context
            .DocumentLinkLookupById[LookupDocumentId]
            .Should()
            .Be(new DocumentLinkLookupEntry(_documentUuid, ResourceKeyId));
    }
}

[TestFixture]
public class Given_PageReconstitutionContext_With_Conflicting_Document_Link_Lookup_Rows
{
    private const long LookupDocumentId = 901L;
    private const short ResourceKeyId = 7;
    private static readonly Guid _documentUuid = Guid.Parse("11112222-3333-4444-5555-666677778888");
    private static readonly Guid _conflictingDocumentUuid = Guid.Parse(
        "99998888-7777-6666-5555-444433332222"
    );

    [TestCase(true, false, TestName = "different_DocumentUuid")]
    [TestCase(false, true, TestName = "different_ResourceKeyId")]
    public void It_should_fail_fast_when_duplicate_DocumentId_resolves_to_different_link_target(
        bool changeDocumentUuid,
        bool changeResourceKeyId
    )
    {
        var pageData = PageReconstitutionContextTestData.CreatePageWithDocumentLinkLookup([
            new DocumentReferenceLookupRow(LookupDocumentId, _documentUuid, ResourceKeyId),
            new DocumentReferenceLookupRow(
                LookupDocumentId,
                changeDocumentUuid ? _conflictingDocumentUuid : _documentUuid,
                changeResourceKeyId ? (short)(ResourceKeyId + 1) : ResourceKeyId
            ),
        ]);

        Action act = () => PageReconstitutionContext.Build(pageData.ReadPlan, pageData.HydratedPage);

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage(
                "Cannot build page reconstitution context: document-reference lookup returned conflicting rows for DocumentId 901."
            );
    }
}

[TestFixture]
public class Given_PageReconstitutionContext_With_Duplicate_Root_Rows
{
    private Exception _exception = null!;

    [SetUp]
    public void SetUp()
    {
        var pageData = PageReconstitutionContextTestData.CreatePageWithDuplicateRootRows();

        _exception = Assert.Throws<InvalidOperationException>(() =>
            PageReconstitutionContext.Build(pageData.ReadPlan, pageData.HydratedPage)
        )!;
    }

    [Test]
    public void It_should_fail_fast()
    {
        _exception
            .Message.Should()
            .Be(
                "Cannot build page reconstitution context for 'Ed-Fi.School': duplicate root row for DocumentId 101 in table 'edfi.School'."
            );
    }
}

[TestFixture]
public class Given_PageReconstitutionContext_With_Duplicate_Document_Metadata_Rows
{
    private Exception _exception = null!;

    [SetUp]
    public void SetUp()
    {
        var pageData = PageReconstitutionContextTestData.CreatePageWithDuplicateDocumentMetadataRows();

        _exception = Assert.Throws<InvalidOperationException>(() =>
            PageReconstitutionContext.Build(pageData.ReadPlan, pageData.HydratedPage)
        )!;
    }

    [Test]
    public void It_should_fail_fast()
    {
        _exception
            .Message.Should()
            .Be(
                "Cannot build page reconstitution context for 'Ed-Fi.School': duplicate document metadata row for DocumentId 101."
            );
    }
}

[TestFixture]
public class Given_PageReconstitutionContext_With_Duplicate_Physical_Row_Identity
{
    private Exception _exception = null!;

    [SetUp]
    public void SetUp()
    {
        var pageData = PageReconstitutionContextTestData.CreatePageWithDuplicateChildPhysicalIdentity();

        _exception = Assert.Throws<InvalidOperationException>(() =>
            PageReconstitutionContext.Build(pageData.ReadPlan, pageData.HydratedPage)
        )!;
    }

    [Test]
    public void It_should_fail_fast()
    {
        _exception
            .Message.Should()
            .Be(
                "Cannot build page reconstitution context for 'Ed-Fi.School': table 'edfi.SchoolAddress' contains duplicate physical row identity [101]."
            );
    }
}

[TestFixture]
public class Given_PageReconstitutionContext_With_An_Orphaned_Child_Row
{
    private Exception _exception = null!;

    [SetUp]
    public void SetUp()
    {
        var pageData = PageReconstitutionContextTestData.CreatePageWithOrphanedChildRow();

        _exception = Assert.Throws<InvalidOperationException>(() =>
            PageReconstitutionContext.Build(pageData.ReadPlan, pageData.HydratedPage)
        )!;
    }

    [Test]
    public void It_should_fail_fast()
    {
        _exception
            .Message.Should()
            .Be(
                "Cannot build page reconstitution context for 'Ed-Fi.School': orphaned row in table 'edfi.SchoolAddressPeriod' with immediate parent table 'edfi.SchoolAddress' and locator [999]."
            );
    }
}

[TestFixture]
public class Given_PageReconstitutionContext_With_A_Child_Row_Whose_Root_Document_Does_Not_Match_Its_Parent
{
    private Exception _exception = null!;

    [SetUp]
    public void SetUp()
    {
        var pageData = PageReconstitutionContextTestData.CreatePageWithChildRootDocumentMismatch();

        _exception = Assert.Throws<InvalidOperationException>(() =>
            PageReconstitutionContext.Build(pageData.ReadPlan, pageData.HydratedPage)
        )!;
    }

    [Test]
    public void It_should_fail_fast()
    {
        _exception
            .Message.Should()
            .Be(
                "Cannot build page reconstitution context for 'Ed-Fi.School': row in table 'edfi.SchoolAddressPeriod' resolved to parent table 'edfi.SchoolAddress', but the child root document id 202 did not match parent root document id 101."
            );
    }
}

[TestFixture]
public class Given_PageReconstitutionContext_With_A_Metadata_Row_Without_A_Root_Row
{
    private Exception _exception = null!;

    [SetUp]
    public void SetUp()
    {
        var pageData = PageReconstitutionContextTestData.CreatePageWithMetadataRowWithoutRoot();

        _exception = Assert.Throws<InvalidOperationException>(() =>
            PageReconstitutionContext.Build(pageData.ReadPlan, pageData.HydratedPage)
        )!;
    }

    [Test]
    public void It_should_fail_fast()
    {
        _exception
            .Message.Should()
            .Be(
                "Cannot build page reconstitution context for 'Ed-Fi.School': document metadata row for DocumentId 202 has no matching root row."
            );
    }
}

[TestFixture]
public class Given_PageReconstitutionContext_With_Extra_Root_Rows_Not_In_Metadata
{
    private Exception _exception = null!;

    [SetUp]
    public void SetUp()
    {
        var pageData = PageReconstitutionContextTestData.CreatePageWithExtraRootRows();

        _exception = Assert.Throws<InvalidOperationException>(() =>
            PageReconstitutionContext.Build(pageData.ReadPlan, pageData.HydratedPage)
        )!;
    }

    [Test]
    public void It_should_fail_fast()
    {
        _exception
            .Message.Should()
            .Be(
                "Cannot build page reconstitution context for 'Ed-Fi.School': root rows were hydrated for document ids not present in page metadata: [202, 303]."
            );
    }
}

[TestFixture]
public class Given_PageReconstitutionContext_With_Duplicate_Hydrated_Table_Rows
{
    private Exception _exception = null!;
    private DbTableName _rootTable = default;

    [SetUp]
    public void SetUp()
    {
        var pageData = PageReconstitutionContextTestData.CreatePageWithDuplicateHydratedTableRows();
        _rootTable = pageData.RootTable;

        _exception = Assert.Throws<InvalidOperationException>(() =>
            PageReconstitutionContext.Build(pageData.ReadPlan, pageData.HydratedPage)
        )!;
    }

    [Test]
    public void It_should_fail_fast()
    {
        _exception
            .Message.Should()
            .Be(
                $"Cannot build page reconstitution context: duplicate hydrated row set for table '{_rootTable}'."
            );
    }
}

[TestFixture]
public class Given_PageReconstitutionContext_With_Unexpected_Hydrated_Tables
{
    private Exception _exception = null!;

    [SetUp]
    public void SetUp()
    {
        var pageData = PageReconstitutionContextTestData.CreatePageWithUnexpectedHydratedTables();

        _exception = Assert.Throws<InvalidOperationException>(() =>
            PageReconstitutionContext.Build(pageData.ReadPlan, pageData.HydratedPage)
        )!;
    }

    [Test]
    public void It_should_fail_fast()
    {
        _exception
            .Message.Should()
            .Be(
                "Cannot build page reconstitution context for 'Ed-Fi.School': hydrated rows contained unexpected tables [edfi.AlphaUnexpected, edfi.ZebraUnexpected]."
            );
    }
}

[TestFixture]
public class Given_PageReconstitutionContext_Without_A_Required_Hydrated_Table
{
    private Exception _exception = null!;

    [SetUp]
    public void SetUp()
    {
        var pageData = PageReconstitutionContextTestData.CreatePageWithoutRequiredHydratedTable();

        _exception = Assert.Throws<InvalidOperationException>(() =>
            PageReconstitutionContext.Build(pageData.ReadPlan, pageData.HydratedPage)
        )!;
    }

    [Test]
    public void It_should_fail_fast()
    {
        _exception
            .Message.Should()
            .Be(
                "Cannot build page reconstitution context for 'Ed-Fi.School': hydrated rows did not contain required table 'edfi.SchoolAddressPeriod'."
            );
    }
}

[TestFixture]
public class Given_PageReconstitutionContext_With_A_Root_Row_That_Does_Not_Resolve_To_A_DocumentId
{
    private Exception _exception = null!;

    [SetUp]
    public void SetUp()
    {
        var pageData = PageReconstitutionContextTestData.CreatePageWithInvalidRootDocumentId();

        _exception = Assert.Throws<InvalidOperationException>(() =>
            PageReconstitutionContext.Build(pageData.ReadPlan, pageData.HydratedPage)
        )!;
    }

    [Test]
    public void It_should_fail_fast()
    {
        _exception
            .Message.Should()
            .Be(
                "Cannot build page reconstitution context: table 'edfi.School' root scope locator [\"not-a-document-id\"] could not be resolved to a single DocumentId."
            );
    }
}

[TestFixture]
public class Given_PageReconstitutionContext_With_A_Collection_Row_Whose_Ordinal_Cannot_Be_Converted
{
    private Exception _exception = null!;

    [SetUp]
    public void SetUp()
    {
        var pageData = PageReconstitutionContextTestData.CreatePageWithInvalidAddressOrdinal();

        _exception = Assert.Throws<InvalidOperationException>(() =>
            PageReconstitutionContext.Build(pageData.ReadPlan, pageData.HydratedPage)
        )!;
    }

    [Test]
    public void It_should_fail_fast()
    {
        _exception
            .Message.Should()
            .Be(
                "Cannot order hydrated child rows: table 'edfi.SchoolAddress' column 'Ordinal' at ordinal '2' contains 'first' (type: System.String) that cannot be converted to an ordinal."
            );

        _exception.InnerException.Should().BeOfType<FormatException>();
    }
}

file static class PageReconstitutionContextTestData
{
    private static readonly DbSchemaName _schema = new("edfi");
    private static readonly QualifiedResourceName _resource = new("Ed-Fi", "School");

    public static PageData CreatePageWithDocumentLinkLookup(
        IReadOnlyList<DocumentReferenceLookupRow> lookupRows
    )
    {
        var pageData = CreateHappyPathPage();

        return pageData with
        {
            HydratedPage = pageData.HydratedPage with
            {
                DocumentReferenceLookup = new HydratedDocumentReferenceLookup(lookupRows),
            },
        };
    }

    public static PageData CreatePageWithConflictingDescriptorRows()
    {
        var pageData = CreateHappyPathPage();

        return pageData with
        {
            HydratedPage = pageData.HydratedPage with
            {
                DescriptorRowsInPlanOrder =
                [
                    new HydratedDescriptorRows([
                        new DescriptorUriRow(601L, "uri://ed-fi.org/SchoolCategoryDescriptor#Alternative"),
                    ]),
                    new HydratedDescriptorRows([
                        new DescriptorUriRow(601L, "uri://ed-fi.org/SchoolCategoryDescriptor#Conflict"),
                    ]),
                ],
            },
        };
    }

    public static CompiledReconstitutionPlan CreateCompiledPlanWithoutRootScopeLocator(
        ResourceReadPlan readPlan
    )
    {
        var compiledPlan = CompiledReconstitutionPlanCache.GetOrBuild(readPlan);
        var tablePlans = compiledPlan.TablePlansInDependencyOrder.ToArray();
        tablePlans[0] = tablePlans[0] with { RootScopeLocatorOrdinals = [] };

        return new CompiledReconstitutionPlan(compiledPlan.ReadPlan, tablePlans, compiledPlan.PropertyOrder);
    }

    public static CompiledReconstitutionPlan CreateCompiledPlanWithoutPhysicalRowIdentity(
        ResourceReadPlan readPlan
    )
    {
        var compiledPlan = CompiledReconstitutionPlanCache.GetOrBuild(readPlan);
        var tablePlans = compiledPlan.TablePlansInDependencyOrder.ToArray();
        tablePlans[0] = tablePlans[0] with { PhysicalRowIdentityOrdinals = [] };

        return new CompiledReconstitutionPlan(compiledPlan.ReadPlan, tablePlans, compiledPlan.PropertyOrder);
    }

    public static CompiledReconstitutionPlan CreateCompiledPlanWithoutImmediateParentScopeLocator(
        ResourceReadPlan readPlan
    )
    {
        var compiledPlan = CompiledReconstitutionPlanCache.GetOrBuild(readPlan);
        var tablePlans = compiledPlan.TablePlansInDependencyOrder.ToArray();
        tablePlans[1] = tablePlans[1] with { ImmediateParentScopeLocatorOrdinals = [] };

        return new CompiledReconstitutionPlan(compiledPlan.ReadPlan, tablePlans, compiledPlan.PropertyOrder);
    }

    public static CompiledReconstitutionPlan CreateCompiledPlanWithAddressBeforeRoot(
        ResourceReadPlan readPlan
    )
    {
        var compiledPlan = CompiledReconstitutionPlanCache.GetOrBuild(readPlan);
        var tablePlans = compiledPlan.TablePlansInDependencyOrder.ToArray();

        return new CompiledReconstitutionPlan(
            compiledPlan.ReadPlan,
            [tablePlans[1], tablePlans[0], tablePlans[2]],
            compiledPlan.PropertyOrder
        );
    }

    public static PageData CreateHappyPathPage()
    {
        var readPlan = CreateReadPlan();

        return new PageData(
            readPlan,
            CreateHydratedPage(
                documentMetadata:
                [
                    CreateDocumentMetadataRow(101L, "aaaaaaaa-1111-1111-1111-111111111111"),
                    CreateDocumentMetadataRow(202L, "bbbbbbbb-2222-2222-2222-222222222222"),
                ],
                rootRows:
                [
                    [202, "Second School"],
                    [101, "First School"],
                ],
                addressRows:
                [
                    [101, 101, 2, "North City"],
                    [202, 202L, 1, "South City"],
                    [102, 101L, 1, "East City"],
                ],
                periodRows:
                [
                    [101, 101, 101, 2, "2024-08-15"],
                    [201, 202, 202, 1, "2025-01-10"],
                    [102, 102, 101L, 1, "2024-01-05"],
                    [103, 102, 101L, 0, "2023-09-01"],
                ],
                descriptorRowsInPlanOrder:
                [
                    new HydratedDescriptorRows([
                        new DescriptorUriRow(601L, "uri://ed-fi.org/SchoolCategoryDescriptor#Alternative"),
                    ]),
                    new HydratedDescriptorRows([
                        new DescriptorUriRow(601L, "uri://ed-fi.org/SchoolCategoryDescriptor#Alternative"),
                        new DescriptorUriRow(602L, "uri://ed-fi.org/SchoolCategoryDescriptor#Charter"),
                    ]),
                ]
            ),
            readPlan.Model.Root.Table,
            readPlan.Model.TablesInDependencyOrder[1].Table,
            readPlan.Model.TablesInDependencyOrder[2].Table
        );
    }

    public static PageData CreatePageWithoutAddressRows()
    {
        var readPlan = CreateReadPlan();

        return new PageData(
            readPlan,
            CreateHydratedPage(
                documentMetadata: [CreateDocumentMetadataRow(101L, "aaaaaaaa-1111-1111-1111-111111111111")],
                rootRows:
                [
                    [101, "First School"],
                ],
                addressRows: [],
                periodRows: []
            ),
            readPlan.Model.Root.Table,
            readPlan.Model.TablesInDependencyOrder[1].Table,
            readPlan.Model.TablesInDependencyOrder[2].Table
        );
    }

    public static PageData CreatePageWithDuplicateRootRows()
    {
        var readPlan = CreateReadPlan();

        return new PageData(
            readPlan,
            CreateHydratedPage(
                documentMetadata: [CreateDocumentMetadataRow(101L, "aaaaaaaa-1111-1111-1111-111111111111")],
                rootRows:
                [
                    [101, "First School"],
                    [101L, "Duplicate Root"],
                ],
                addressRows: [],
                periodRows: []
            ),
            readPlan.Model.Root.Table,
            readPlan.Model.TablesInDependencyOrder[1].Table,
            readPlan.Model.TablesInDependencyOrder[2].Table
        );
    }

    public static PageData CreatePageWithDuplicateDocumentMetadataRows()
    {
        var readPlan = CreateReadPlan();

        return new PageData(
            readPlan,
            CreateHydratedPage(
                documentMetadata:
                [
                    CreateDocumentMetadataRow(101L, "aaaaaaaa-1111-1111-1111-111111111111"),
                    CreateDocumentMetadataRow(101L, "bbbbbbbb-2222-2222-2222-222222222222"),
                ],
                rootRows:
                [
                    [101, "First School"],
                ],
                addressRows: [],
                periodRows: []
            ),
            readPlan.Model.Root.Table,
            readPlan.Model.TablesInDependencyOrder[1].Table,
            readPlan.Model.TablesInDependencyOrder[2].Table
        );
    }

    public static PageData CreatePageWithDuplicateChildPhysicalIdentity()
    {
        var readPlan = CreateReadPlan();

        return new PageData(
            readPlan,
            CreateHydratedPage(
                documentMetadata: [CreateDocumentMetadataRow(101L, "aaaaaaaa-1111-1111-1111-111111111111")],
                rootRows:
                [
                    [101, "First School"],
                ],
                addressRows:
                [
                    [101, 101, 1, "North City"],
                    [101L, 101L, 2, "Duplicate Address Identity"],
                ],
                periodRows: []
            ),
            readPlan.Model.Root.Table,
            readPlan.Model.TablesInDependencyOrder[1].Table,
            readPlan.Model.TablesInDependencyOrder[2].Table
        );
    }

    public static PageData CreatePageWithOrphanedChildRow()
    {
        var readPlan = CreateReadPlan();

        return new PageData(
            readPlan,
            CreateHydratedPage(
                documentMetadata: [CreateDocumentMetadataRow(101L, "aaaaaaaa-1111-1111-1111-111111111111")],
                rootRows:
                [
                    [101, "First School"],
                ],
                addressRows:
                [
                    [101, 101, 1, "North City"],
                ],
                periodRows:
                [
                    [201, 999, 101, 1, "2024-08-15"],
                ]
            ),
            readPlan.Model.Root.Table,
            readPlan.Model.TablesInDependencyOrder[1].Table,
            readPlan.Model.TablesInDependencyOrder[2].Table
        );
    }

    public static PageData CreatePageWithChildRootDocumentMismatch()
    {
        var readPlan = CreateReadPlan();

        return new PageData(
            readPlan,
            CreateHydratedPage(
                documentMetadata:
                [
                    CreateDocumentMetadataRow(101L, "aaaaaaaa-1111-1111-1111-111111111111"),
                    CreateDocumentMetadataRow(202L, "bbbbbbbb-2222-2222-2222-222222222222"),
                ],
                rootRows:
                [
                    [101, "First School"],
                    [202, "Second School"],
                ],
                addressRows:
                [
                    [101, 101, 1, "North City"],
                ],
                periodRows:
                [
                    [201, 101, 202, 1, "2024-08-15"],
                ]
            ),
            readPlan.Model.Root.Table,
            readPlan.Model.TablesInDependencyOrder[1].Table,
            readPlan.Model.TablesInDependencyOrder[2].Table
        );
    }

    public static PageData CreatePageWithMetadataRowWithoutRoot()
    {
        var readPlan = CreateReadPlan();

        return new PageData(
            readPlan,
            CreateHydratedPage(
                documentMetadata:
                [
                    CreateDocumentMetadataRow(101L, "aaaaaaaa-1111-1111-1111-111111111111"),
                    CreateDocumentMetadataRow(202L, "bbbbbbbb-2222-2222-2222-222222222222"),
                ],
                rootRows:
                [
                    [101, "First School"],
                ],
                addressRows: [],
                periodRows: []
            ),
            readPlan.Model.Root.Table,
            readPlan.Model.TablesInDependencyOrder[1].Table,
            readPlan.Model.TablesInDependencyOrder[2].Table
        );
    }

    public static PageData CreatePageWithExtraRootRows()
    {
        var readPlan = CreateReadPlan();

        return new PageData(
            readPlan,
            CreateHydratedPage(
                documentMetadata: [CreateDocumentMetadataRow(101L, "aaaaaaaa-1111-1111-1111-111111111111")],
                rootRows:
                [
                    [101, "First School"],
                    [303, "Extra Root"],
                    [202, "Second Extra Root"],
                ],
                addressRows: [],
                periodRows: []
            ),
            readPlan.Model.Root.Table,
            readPlan.Model.TablesInDependencyOrder[1].Table,
            readPlan.Model.TablesInDependencyOrder[2].Table
        );
    }

    public static PageData CreatePageWithDuplicateHydratedTableRows()
    {
        var pageData = CreateHappyPathPage();
        var rootRows = pageData.HydratedPage.TableRowsInDependencyOrder[0];

        return pageData with
        {
            HydratedPage = pageData.HydratedPage with
            {
                TableRowsInDependencyOrder =
                [
                    rootRows,
                    rootRows,
                    pageData.HydratedPage.TableRowsInDependencyOrder[1],
                    pageData.HydratedPage.TableRowsInDependencyOrder[2],
                ],
            },
        };
    }

    public static PageData CreatePageWithUnexpectedHydratedTables()
    {
        var pageData = CreateHappyPathPage();

        return pageData with
        {
            HydratedPage = pageData.HydratedPage with
            {
                TableRowsInDependencyOrder =
                [
                    .. pageData.HydratedPage.TableRowsInDependencyOrder,
                    new HydratedTableRows(CreateUnexpectedTable("ZebraUnexpected"), []),
                    new HydratedTableRows(CreateUnexpectedTable("AlphaUnexpected"), []),
                ],
            },
        };
    }

    public static PageData CreatePageWithoutRequiredHydratedTable()
    {
        var pageData = CreateHappyPathPage();

        return pageData with
        {
            HydratedPage = pageData.HydratedPage with
            {
                TableRowsInDependencyOrder =
                [
                    pageData.HydratedPage.TableRowsInDependencyOrder[0],
                    pageData.HydratedPage.TableRowsInDependencyOrder[1],
                ],
            },
        };
    }

    public static PageData CreatePageWithInvalidRootDocumentId()
    {
        var readPlan = CreateReadPlan();

        return new PageData(
            readPlan,
            CreateHydratedPage(
                documentMetadata: [CreateDocumentMetadataRow(101L, "aaaaaaaa-1111-1111-1111-111111111111")],
                rootRows:
                [
                    ["not-a-document-id", "First School"],
                ],
                addressRows: [],
                periodRows: []
            ),
            readPlan.Model.Root.Table,
            readPlan.Model.TablesInDependencyOrder[1].Table,
            readPlan.Model.TablesInDependencyOrder[2].Table
        );
    }

    public static PageData CreatePageWithInvalidAddressOrdinal()
    {
        var readPlan = CreateReadPlan();

        return new PageData(
            readPlan,
            CreateHydratedPage(
                documentMetadata: [CreateDocumentMetadataRow(101L, "aaaaaaaa-1111-1111-1111-111111111111")],
                rootRows:
                [
                    [101, "First School"],
                ],
                addressRows:
                [
                    [101, 101, "first", "North City"],
                    [102, 101, 2, "East City"],
                ],
                periodRows: []
            ),
            readPlan.Model.Root.Table,
            readPlan.Model.TablesInDependencyOrder[1].Table,
            readPlan.Model.TablesInDependencyOrder[2].Table
        );
    }

    private static DbTableModel CreateUnexpectedTable(string tableName)
    {
        var table = new DbTableName(_schema, tableName);

        return new DbTableModel(
            Table: table,
            JsonScope: CreatePath($"$.{tableName}"),
            Key: new TableKey(
                ConstraintName: $"PK_{tableName}",
                Columns: [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)]
            ),
            Columns: [CreateColumn("DocumentId", ColumnKind.ParentKeyPart, ScalarKind.Int64, false)],
            Constraints: []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                TableKind: DbTableKind.Root,
                PhysicalRowIdentityColumns: [new DbColumnName("DocumentId")],
                RootScopeLocatorColumns: [new DbColumnName("DocumentId")],
                ImmediateParentScopeLocatorColumns: [],
                SemanticIdentityBindings: []
            ),
        };
    }

    private static ResourceReadPlan CreateReadPlan()
    {
        var rootTable = new DbTableModel(
            Table: new DbTableName(_schema, "School"),
            JsonScope: CreatePath("$"),
            Key: new TableKey(
                ConstraintName: "PK_School",
                Columns: [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)]
            ),
            Columns:
            [
                CreateColumn("DocumentId", ColumnKind.ParentKeyPart, ScalarKind.Int64, false),
                CreateColumn(
                    "NameOfInstitution",
                    ColumnKind.Scalar,
                    ScalarKind.String,
                    false,
                    CreatePath("$.nameOfInstitution", new JsonPathSegment.Property("nameOfInstitution"))
                ),
            ],
            Constraints: []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                TableKind: DbTableKind.Root,
                PhysicalRowIdentityColumns: [new DbColumnName("DocumentId")],
                RootScopeLocatorColumns: [new DbColumnName("DocumentId")],
                ImmediateParentScopeLocatorColumns: [],
                SemanticIdentityBindings: []
            ),
        };

        var addressTable = new DbTableModel(
            Table: new DbTableName(_schema, "SchoolAddress"),
            JsonScope: CreatePath(
                "$.addresses[*]",
                new JsonPathSegment.Property("addresses"),
                new JsonPathSegment.AnyArrayElement()
            ),
            Key: new TableKey(
                ConstraintName: "PK_SchoolAddress",
                Columns: [new DbKeyColumn(new DbColumnName("CollectionItemId"), ColumnKind.CollectionKey)]
            ),
            Columns:
            [
                CreateColumn("CollectionItemId", ColumnKind.CollectionKey, ScalarKind.Int64, false),
                CreateColumn("School_DocumentId", ColumnKind.ParentKeyPart, ScalarKind.Int64, false),
                CreateColumn("Ordinal", ColumnKind.Ordinal, ScalarKind.Int32, false),
                CreateColumn(
                    "City",
                    ColumnKind.Scalar,
                    ScalarKind.String,
                    false,
                    CreatePath(
                        "$.addresses[*].city",
                        new JsonPathSegment.Property("addresses"),
                        new JsonPathSegment.AnyArrayElement(),
                        new JsonPathSegment.Property("city")
                    )
                ),
            ],
            Constraints: []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                TableKind: DbTableKind.Collection,
                PhysicalRowIdentityColumns: [new DbColumnName("CollectionItemId")],
                RootScopeLocatorColumns: [new DbColumnName("School_DocumentId")],
                ImmediateParentScopeLocatorColumns: [new DbColumnName("School_DocumentId")],
                SemanticIdentityBindings: []
            ),
        };

        var periodTable = new DbTableModel(
            Table: new DbTableName(_schema, "SchoolAddressPeriod"),
            JsonScope: CreatePath(
                "$.addresses[*].periods[*]",
                new JsonPathSegment.Property("addresses"),
                new JsonPathSegment.AnyArrayElement(),
                new JsonPathSegment.Property("periods"),
                new JsonPathSegment.AnyArrayElement()
            ),
            Key: new TableKey(
                ConstraintName: "PK_SchoolAddressPeriod",
                Columns: [new DbKeyColumn(new DbColumnName("CollectionItemId"), ColumnKind.CollectionKey)]
            ),
            Columns:
            [
                CreateColumn("CollectionItemId", ColumnKind.CollectionKey, ScalarKind.Int64, false),
                CreateColumn("ParentCollectionItemId", ColumnKind.ParentKeyPart, ScalarKind.Int64, false),
                CreateColumn("School_DocumentId", ColumnKind.ParentKeyPart, ScalarKind.Int64, false),
                CreateColumn("Ordinal", ColumnKind.Ordinal, ScalarKind.Int32, false),
                CreateColumn(
                    "BeginDate",
                    ColumnKind.Scalar,
                    ScalarKind.String,
                    false,
                    CreatePath(
                        "$.addresses[*].periods[*].beginDate",
                        new JsonPathSegment.Property("addresses"),
                        new JsonPathSegment.AnyArrayElement(),
                        new JsonPathSegment.Property("periods"),
                        new JsonPathSegment.AnyArrayElement(),
                        new JsonPathSegment.Property("beginDate")
                    )
                ),
            ],
            Constraints: []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                TableKind: DbTableKind.Collection,
                PhysicalRowIdentityColumns: [new DbColumnName("CollectionItemId")],
                RootScopeLocatorColumns: [new DbColumnName("School_DocumentId")],
                ImmediateParentScopeLocatorColumns: [new DbColumnName("ParentCollectionItemId")],
                SemanticIdentityBindings: []
            ),
        };

        var model = new RelationalResourceModel(
            Resource: _resource,
            PhysicalSchema: _schema,
            StorageKind: ResourceStorageKind.RelationalTables,
            Root: rootTable,
            TablesInDependencyOrder: [rootTable, addressTable, periodTable],
            DocumentReferenceBindings: [],
            DescriptorEdgeSources: []
        );

        return new ResourceReadPlan(
            Model: model,
            KeysetTable: KeysetTableConventions.GetKeysetTableContract(SqlDialect.Pgsql),
            TablePlansInDependencyOrder:
            [
                new TableReadPlan(rootTable, "SELECT 1"),
                new TableReadPlan(addressTable, "SELECT 1"),
                new TableReadPlan(periodTable, "SELECT 1"),
            ],
            ReferenceIdentityProjectionPlansInDependencyOrder: [],
            DescriptorProjectionPlansInOrder: []
        );
    }

    private static HydratedPage CreateHydratedPage(
        IReadOnlyList<DocumentMetadataRow> documentMetadata,
        IReadOnlyList<object?[]> rootRows,
        IReadOnlyList<object?[]> addressRows,
        IReadOnlyList<object?[]> periodRows,
        IReadOnlyList<HydratedDescriptorRows>? descriptorRowsInPlanOrder = null
    )
    {
        var readPlan = CreateReadPlan();

        return new HydratedPage(
            TotalCount: null,
            DocumentMetadata: documentMetadata,
            TableRowsInDependencyOrder:
            [
                new HydratedTableRows(readPlan.Model.Root, rootRows),
                new HydratedTableRows(readPlan.Model.TablesInDependencyOrder[1], addressRows),
                new HydratedTableRows(readPlan.Model.TablesInDependencyOrder[2], periodRows),
            ],
            DescriptorRowsInPlanOrder: descriptorRowsInPlanOrder ?? []
        );
    }

    private static DocumentMetadataRow CreateDocumentMetadataRow(long documentId, string documentUuid) =>
        new(
            DocumentId: documentId,
            DocumentUuid: Guid.Parse(documentUuid),
            ContentVersion: 1L,
            IdentityVersion: 1L,
            ContentLastModifiedAt: new DateTimeOffset(2026, 4, 19, 12, 0, 0, TimeSpan.Zero),
            IdentityLastModifiedAt: new DateTimeOffset(2026, 4, 19, 12, 0, 0, TimeSpan.Zero)
        );

    private static DbColumnModel CreateColumn(
        string name,
        ColumnKind kind,
        ScalarKind scalarKind,
        bool isNullable,
        JsonPathExpression? sourceJsonPath = null
    ) =>
        new(
            ColumnName: new DbColumnName(name),
            Kind: kind,
            ScalarType: scalarKind switch
            {
                ScalarKind.String => new RelationalScalarType(scalarKind, MaxLength: 255),
                _ => new RelationalScalarType(scalarKind),
            },
            IsNullable: isNullable,
            SourceJsonPath: sourceJsonPath,
            TargetResource: null
        );

    private static JsonPathExpression CreatePath(string canonical, params JsonPathSegment[] segments) =>
        new(canonical, segments);

    internal sealed record PageData(
        ResourceReadPlan ReadPlan,
        HydratedPage HydratedPage,
        DbTableName RootTable,
        DbTableName AddressTable,
        DbTableName PeriodTable
    );
}
