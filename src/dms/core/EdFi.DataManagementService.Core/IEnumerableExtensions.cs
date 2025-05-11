// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core
{
    internal static class IEnumerableExtensions
    {
        /// <summary>
        /// Removes null items from an IEnumerable.
        /// Returns an IEnumerable of the corresponding non-nullable type.
        /// </summary>
        public static IEnumerable<T> WhereNotNull<T>(this IEnumerable<T?> items)
            where T : class => items.Where(item => item != null)!;

        /// <summary>
        /// Removes null items from an IEnumerable.
        /// Returns an IEnumerable of the corresponding non-nullable type.
        /// </summary>
        public static IEnumerable<T> WhereNotNull<T>(this IEnumerable<T?> items)
            where T : struct => items.Where(item => item != null).Select(item => item.GetValueOrDefault());
    }
}
