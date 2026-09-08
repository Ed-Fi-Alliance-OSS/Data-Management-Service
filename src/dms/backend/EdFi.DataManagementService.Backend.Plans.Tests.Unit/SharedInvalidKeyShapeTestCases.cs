// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Plans.Tests.Unit;

internal abstract class SharedInvalidKeyShapeTestCases : WritePlanCompilerTestBase
{
    public static IEnumerable<TestCaseData> ForReadPlanCompiler()
    {
        return CreateCases("read plan");
    }

    public static IEnumerable<TestCaseData> ForWritePlanCompiler()
    {
        return CreateCases("write plan");
    }

    private static IEnumerable<TestCaseData> CreateCases(string planKind)
    {
        yield return CreateCase(
            CreateRootOnlyModelWithMissingDocumentIdParentKeyPart,
            $"Cannot compile {planKind} for 'edfi.Student': expected exactly one ParentKeyPart document-id key column ('DocumentId' or '*_DocumentId'), but found 0. Key columns: [SchoolYear:ParentKeyPart].",
            "It_should_fail_fast_when_key_does_not_include_exactly_one_document_id_parent_key_part"
        );

        yield return CreateCase(
            CreateSingleTableModelWithDocumentIdNotFirstInKeyOrder,
            $"Cannot compile {planKind} for 'edfi.StudentAddress': expected document-id ParentKeyPart key column ('DocumentId' or '*_DocumentId') to be first in key order, but found 'ParentAddressOrdinal:ParentKeyPart'. Key columns: [ParentAddressOrdinal:ParentKeyPart, DocumentId:ParentKeyPart, Ordinal:Ordinal].",
            "It_should_fail_fast_when_document_id_parent_key_part_is_not_first_in_key_order"
        );

        yield return CreateCase(
            CreateSingleTableModelWithMultipleOrdinalKeyColumns,
            $"Cannot compile {planKind} for 'edfi.StudentAddress': expected at most one Ordinal key column, but found 2. Key columns: [DocumentId:ParentKeyPart, ParentAddressOrdinal:ParentKeyPart, Ordinal:Ordinal, Ordinal:Ordinal].",
            "It_should_fail_fast_when_key_contains_multiple_ordinal_columns"
        );

        yield return CreateCase(
            CreateSingleTableModelWithOrdinalNotLastInKeyOrder,
            $"Cannot compile {planKind} for 'edfi.StudentAddress': expected Ordinal key column to be last in key order. Key columns: [DocumentId:ParentKeyPart, Ordinal:Ordinal, ParentAddressOrdinal:ParentKeyPart].",
            "It_should_fail_fast_when_ordinal_key_column_is_not_last_in_key_order"
        );
    }

    private static TestCaseData CreateCase(
        Func<RelationalResourceModel> createModel,
        string expectedMessage,
        string testName
    )
    {
        return new TestCaseData(createModel, expectedMessage).SetName(testName);
    }
}
