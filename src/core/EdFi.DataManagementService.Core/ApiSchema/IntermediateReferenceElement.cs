// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Core.ApiSchema
{
    /// <summary>
    /// In extracting DocumentReferences, there is an intermediate step where document values are resolved
    /// from a JsonPath. JsonPaths return arrays of values when the path includes "[*]"
    /// This is the case for collections of document references.
    ///
    /// This means that each collection path resolves to an array of values made up of a slice of
    /// *each* document reference in the collection. IntermediateReferenceElement holds these resolved
    /// document values for a JsonPath.
    ///
    /// For example, given a document with a collection of ClassPeriod references:
    ///
    /// classPeriods: [
    ///   {
    ///     classPeriodReference: {
    ///       schoolId: '1',
    ///       classPeriodName: 'classPeriod1',
    ///     },
    ///   },
    ///   {
    ///     classPeriodReference: {
    ///       schoolId: '2',
    ///       classPeriodName: 'classPeriod2',
    ///     },
    ///   },
    /// ]
    ///
    /// With ReferenceJsonPaths of "$.classPeriods[*].classPeriodReference.schoolId" and
    /// "$.classPeriods[*].classPeriodReference.classPeriodName", and IdentityJsonPaths
    /// of "$.schoolId" and "$.classPeriodName"
    /// the IntermediateReferenceElement array would be:
    ///
    /// [
    ///   "$.schoolId": ['1', '2'],
    ///   "$.classPeriodName": ['classPeriod1', 'classPeriod2']
    /// ]
    ///
    /// The array contains information for two DocumentReferences, but as "slices"
    /// in an intermediate orientation.
    ////
    /// </summary>
    internal record IntermediateReferenceElement(
        /// <summary>
        /// The JsonPath to the identity value in the document being referenced
        /// </summary>
        JsonPath IdentityJsonPath,
        /// <summary>
        /// A slice of resolved document reference values across the collection of references
        /// </summary>
        string[] ValueSlice
    );
}
