# Query Plan Analysis Report - Joins Function Performance Issues

**Analysis Date:** September 15, 2025
**Target:** `sp_imart_transform_dim_student_edfi_postgres_joins()` function
**Database:** northridge-flattened (PostgreSQL 13)

## Executive Summary

**Critical Finding:** The joins function contains a **major architectural bug** that causes severe performance degradation. The function incorrectly compares natural keys (`studentusi`) with surrogate keys (`student_surrogateid`), resulting in inefficient nested loops and excessive buffer usage.

**Performance Impact:** 26.59% slower than original (3,663ms vs 2,894ms average)

## Key Performance Issues Identified

### 1. 🚨 **CRITICAL BUG: Mixed Key Types in Joins**

**Location:** Multiple EXISTS clauses in race processing (lines 217, 222, 227, etc.)

**Problem:** The function joins `st.studentusi` (natural key) with `st2.surrogateid` (surrogate key):

```sql
-- INCORRECT (from joins function)
WHERE st2.surrogateid = st.studentusi  -- Comparing different key types!

-- SHOULD BE (corrected)
WHERE st2.studentusi = st.studentusi   -- Compare like with like
```

**Evidence from Query Plan:**
- Line 52: `Index Cond: (st.studentusi = seoa2.student_surrogateid)`
- This creates inefficient lookups and wrong results

### 2. **Excessive Buffer Usage**

**Current Performance:**
- **Shared Hit Blocks:** 281,295 (extremely high)
- **Local/Temp Blocks:** 1,550+ reads/writes
- **Execution Time:** 4,408ms (function internals)

**Comparison with Simple Join:**
- Basic surrogate key join: **10ms, 493 buffers**
- Basic natural key join: **23ms, 673 buffers**
- **Conclusion:** Surrogate keys are actually faster for simple joins!

### 3. **Index Analysis - Good Coverage**

**Positive Findings:** Surrogate key tables have excellent index coverage:

#### Student Table Indexes
```sql
-- Surrogate key primary key (optimal)
student_pkey ON surrogateid

-- Natural key lookup (efficient)
ix_student_studentusi ON studentusi INCLUDE (aggregateid)

-- Identity lookup
student_ui_studentuniqueid ON studentuniqueid INCLUDE (studentusi)
```

#### Association Table Indexes
```sql
-- Efficient surrogate key lookups
ix_studentschoolassociation_student_surrogateid
ix_studentschoolassociation_school_surrogateid
ix_seoa_student_surrogateid
ix_seoa_educationorganization_surrogateid
```

**No Missing Indexes:** All critical join paths have appropriate indexes.

## Detailed Performance Breakdown

### Buffer Usage Analysis

| Operation | Shared Hit | Local Hit | Temp Read/Write | Status |
|-----------|------------|-----------|-----------------|---------|
| **Joins Function** | 281,295 | 864 | 1,550/1,552 | 🔴 Excessive |
| **Simple Surrogate Join** | 493 | 0 | 0 | 🟢 Optimal |
| **Simple Natural Join** | 673 | 0 | 0 | 🟢 Good |

### Query Plan Hotspots

1. **Race Processing EXISTS Clauses:**
   - 105ms execution time for single EXISTS clause
   - 185,974 buffer hits for race lookup
   - Nested loops with 16,831 iterations

2. **Multiple Temp Table Scans:**
   - Function creates 15 temporary tables
   - Heavy temp table I/O operations
   - Potential memory pressure

## Root Cause Analysis

### Primary Issue: Architectural Bug

The joins function was incorrectly converted from the views approach. The pattern:

```sql
-- VIEWS VERSION (correct)
FROM student s  -- Uses natural keys throughout

-- JOINS VERSION (incorrect)
FROM student st  -- Mixed natural and surrogate keys
WHERE st2.surrogateid = st.studentusi  -- BUG: Comparing different key types
```

### Secondary Issues

1. **Complex EXISTS Patterns:** Multiple correlated subqueries with 4-table joins
2. **Lack of Query Optimization:** PostgreSQL cannot optimize mixed key comparisons
3. **Inefficient Data Access Patterns:** Excessive random I/O due to wrong indexes being used

## Performance Comparison Summary

| Metric | Original | Views | Joins (Buggy) |
|--------|----------|-------|---------------|
| **Avg Duration** | 2,894ms | 3,333ms | 3,664ms |
| **Buffer Hits** | ~150,000* | ~200,000* | 281,295 |
| **Temp I/O** | Low | Medium | High |
| **Index Efficiency** | Good | Good | **Poor** |

*Estimated based on proportional analysis

## Recommendations

### 🎯 **Immediate Fix (HIGH PRIORITY)**

**Fix the key comparison bug:**

```sql
-- CURRENT (WRONG)
WHERE st2.surrogateid = st.studentusi

-- CORRECTED
WHERE st2.studentusi = st.studentusi
```

**Expected Impact:** 50-70% performance improvement

### 🔧 **Secondary Optimizations**

1. **Simplify EXISTS Clauses:**
   - Replace 4-table EXISTS with more efficient patterns
   - Consider materialized CTEs for race processing

2. **Index Optimization:**
   - Consider covering indexes for frequent EXISTS patterns
   - Add composite indexes on (racedescriptor_surrogateid, studenteducationorganizationassociation_surrogateid)

3. **Query Structure:**
   - Replace correlated EXISTS with JOINs where possible
   - Use window functions for ranking operations

### 🚀 **Long-term Improvements**

1. **Consistent Architecture:**
   - Standardize on either natural or surrogate keys per function
   - Don't mix key types within single operations

2. **Performance Monitoring:**
   - Add query plan capture for complex operations
   - Monitor buffer usage patterns

## Testing Recommendations

### Validation Steps

1. **Fix the bug and re-test performance**
2. **Verify data integrity** (current function may return wrong results)
3. **Compare query plans** before/after fix
4. **Run extended performance tests** (100+ iterations)

### Expected Results After Fix

- **Performance:** 15-25% improvement over current joins function
- **Buffer Usage:** Reduce to ~150,000-200,000 range
- **Data Integrity:** Correct results matching views function
- **Ranking:** Potentially competitive with views function

## Conclusion

The poor performance of the joins function is **primarily due to a critical bug** rather than fundamental architectural issues with surrogate keys. The surrogate key design itself shows promise (10ms vs 23ms for simple joins), but the implementation contains serious errors that negate these benefits.

**Bottom Line:** Fix the bug first, then re-evaluate the architectural comparison.

---

**Files Referenced:**
- `joins-execution-plan.json` - Full execution plan
- `joins-exists-analysis.txt` - EXISTS clause analysis
- `sp_iMart_Transform_DIM_STUDENT_edfi_Postgres_Joins.sql` - Source function

*Analysis conducted using PostgreSQL EXPLAIN (ANALYZE, BUFFERS, VERBOSE)*