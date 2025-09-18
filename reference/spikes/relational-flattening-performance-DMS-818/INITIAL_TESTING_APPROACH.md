# Sample ETL Transform

## Overview

This repository documents the step-by-step evolution of the `sp_iMart_Transform_DIM_STUDENT_edfi` stored procedure from its original SQL Server implementation to a PostgreSQL solution exploring relational flattening design patterns. The project serves as a proof-of-concept examining the use of surrogate keys and compatibility views, providing reference material for potential future implementations on larger scale systems.

## Original Procedure: `sp_iMart_Transform_DIM_STUDENT_edfi.sql`

### Purpose
The original SQL Server stored procedure `sp_iMart_Transform_DIM_STUDENT_edfi` is a comprehensive ETL (Extract, Transform, Load) function designed for Ed-Fi educational data warehouse operations. This procedure:

- **Processes student dimension data** from 12+ core Ed-Fi tables including student demographics, enrollments, programs, and characteristics
- **Handles complex business logic** for race/ethnicity categorization, program participation indicators, and academic status determination
- **Supports batch processing** via school year parameters (`@Batch_Period_List`) and agency filtering (`@SAID`)
- **Generates 65+ output columns** covering student identifiers, demographics, academic information, and various indicator flags
- **Implements advanced processing** including Master Person Index (MPI) simulation, enrollment priority logic, and characteristic aggregation

### Key Characteristics
- **Size**: 1,500+ lines of T-SQL code
- **Complexity**: Uses 14 temporary tables for multi-stage data processing
- **Performance**: Originally designed for SQL Server with natural key composite joins
- **Business Logic**: Includes sophisticated race categorization, program indicators, and enrollment status determination
- **Production Use**: Real-world ETL script from educational data warehouse operations

## Step 1: PostgreSQL Conversion - `sp_iMart_Transform_DIM_STUDENT_edfi_Postgres.sql`

### Conversion Process
The first major step involved adapting the SQL Server procedure for PostgreSQL compatibility while maintaining the same business logic and output structure.

#### Key Changes Made:
1. **Function Syntax Conversion**
   - Changed from SQL Server `CREATE PROCEDURE` to PostgreSQL `CREATE OR REPLACE FUNCTION`
   - Implemented `RETURNS TABLE` with explicit column definitions (65 columns)
   - Added `LANGUAGE plpgsql` specification

2. **T-SQL to PL/pgSQL Adaptations**
   - Replaced SQL Server table variables with PostgreSQL temporary tables
   - Converted `DECLARE @variable TABLE` patterns to `CREATE TEMP TABLE`
   - Updated XML parameter parsing for batch periods and agency lists
   - Modified string manipulation functions (`PATINDEX` → `POSITION`, etc.)

3. **Database Schema Adjustments**
   - Updated table and column references for PostgreSQL naming conventions
   - Adjusted data type mappings (e.g., `varchar(max)` → `text`)
   - Modified date/time handling functions
   - Updated descriptor namespace references for Ed-Fi 3.x compatibility

4. **PostgreSQL Adaptations**
   - Added systematic temporary table cleanup at function start
   - Implemented indexing on temporary tables
   - Added performance metrics collection (execution time, temp table count)
   - Modified memory usage through controlled temp table lifecycles

#### Major Sections Removed Due to Missing Northridge Schema Tables:

1. **Custom Data Layer (CDL) References**
   - **Removed**: `cdl.building`, `cdl.school`, and `cdl.serviceSchool` table joins (lines 225-227, 1462-1463)
   - **Purpose**: These provided building/school mapping and service school relationships
   - **Impact**: District-specific building codes and service school mappings unavailable
   - **Workaround**: Simulated using standard Ed-Fi school characteristics where possible

2. **Delaware-Specific Ed-Fi Extensions**
   - **Removed**: `edfi_de.StudentSchoolAssociationExtension` table references (lines 202-205)
   - **Purpose**: Delaware-specific enrollment type descriptors and extended student data
   - **Impact**: Lost access to state-specific enrollment classifications
   - **Workaround**: Used standard Ed-Fi enrollment patterns

   - **Removed**: `edfi_de.StudentEducationOrganizationAssociationExtension` (lines 1379-1383)
   - **Purpose**: FERPA privacy flags (address, name, phone, photo opt-outs)
   - **Impact**: Privacy preference indicators not available
   - **Workaround**: Set default privacy flags

   - **Removed**: `edfi_de.StudentLanguageInstructionProgramAssociationLanguageImmersion` (lines 176-179)
   - **Purpose**: Language immersion program participation tracking
   - **Impact**: Language immersion indicators unavailable

3. **Custom Function Dependencies**
   - **Removed**: `dbo.tvf_Edfi_StudentRaceCode(0)` function call (lines 97-99)
   - **Purpose**: Custom race code processing and categorization logic
   - **Impact**: Advanced race/ethnicity categorization logic lost
   - **Workaround**: Implemented simplified race processing using standard Ed-Fi patterns

4. **Temporary Table Infrastructure**
   - **Removed**: `dbo.tempStudentLanguageInstructionProgramAssociationLanguageImmersion_trnsfrmDimStudent` (lines 176-179)
   - **Removed**: `dbo.tempGeneralStudentProgramAssociation_ExitDate_trnsfrmDimStudent` (lines 187-192)
   - **Purpose**: Optimized temporary storage for complex program association queries
   - **Impact**: Lost performance optimizations for program participation lookups

5. **Assessment and Program Features**
   - **Removed**: `PASSED_ENDOFPATHWAY_ASSESSMENT_IND` logic (noted as removed in 08/16/2023 comment)
   - **Purpose**: Career and Technical Education (CTE) pathway assessment results
   - **Impact**: CTE assessment tracking unavailable

6. **Database-Specific Infrastructure**
   - **Removed**: References to `iMartStage` database and `dbo` schema objects
   - **Impact**: Lost connection to data warehouse staging infrastructure

#### Trimming Process Impact:
- **Removed ~200 lines** of Delaware/district-specific customizations
- **Simplified complex business logic** that depended on missing schemas
- **Maintained core Ed-Fi functionality** using standard schema patterns
- **Maintained substantial portions of original business rules** through standard Ed-Fi table substitutions
- **Added simulation logic** where critical functionality could be approximated

## Step 2: Relational Flattening Implementation - `RelationalFlattening/northridge-relational-flattening-postgres.sql`

### Flattening Strategy
The relational flattening approach introduces surrogate keys while preserving the original Ed-Fi three-table design (Document, Reference, Alias) as the source of truth.

#### Key Design Principles:
1. **Surrogate Key Addition**
   - Added `SurrogateId BIGSERIAL PRIMARY KEY` to all core tables
   - Preserved original natural keys with unique constraints
   - Maintained data integrity through cascading relationships

2. **Document Reference Integration**
   - Added `Document_Id` and `Document_PartitionKey` columns to link flattened tables to source documents
   - Enables traceability back to original JSON documents
   - Supports hybrid architecture where both representations coexist

3. **Foreign Key Restructuring**
   - Replaced composite natural key foreign keys with single-column surrogate key references
   - Example: `Student_SurrogateId BIGINT` instead of complex composite keys
   - Intended to simplify join operations

#### Tables Modified:
- **Core Entities**: `edfi.student`, `edfi.school`, `edfi.descriptor`
- **Association Tables**: `edfi.studentschoolassociation`, `edfi.studenteducationorganizationassociation`
- **Characteristic Tables**: Race, student characteristics, program associations
- **Descriptor Tables**: All Ed-Fi descriptor tables for lookups

#### Design Characteristics:
- **Join Structure**: Single 8-byte BIGINT joins vs composite key joins
- **Index Structure**: Simplified indexes on surrogate keys vs complex composite indexes
- **Join Operations**: Single-column joins instead of multi-column joins
- **Query Pattern**: Modified to work with surrogate key relationships

## Step 3: Direct Joins Implementation - `sp_iMart_Transform_DIM_STUDENT_edfi_Postgres_Joins.sql`

### Direct Surrogate Key Approach
This version demonstrates using surrogate keys directly in the function logic without compatibility views.

#### Implementation Strategy:
1. **Direct Surrogate Key Usage**
   - Modified all JOIN operations to use `SurrogateId` columns instead of natural keys
   - Updated temporary table structures to store and use surrogate keys
   - Maintained business logic while leveraging optimized join performance

2. **Query Adaptation**
   - Rewrote complex multi-table joins to utilize surrogate key relationships
   - Updated WHERE clauses and filtering logic for surrogate key patterns
   - Maintained original business rules and data transformations

3. **Implementation Approach**
   - Utilized single-column integer joins
   - Modified temporary table operations
   - Restructured index usage patterns

#### Characteristics:
- **Direct Implementation**: Direct use of surrogate key joins
- **No Abstraction Layer**: No view layer abstraction
- **Full Control**: Complete visibility into join operations

#### Trade-offs:
- **Code Maintenance**: Requires updating existing queries to use surrogate keys
- **Migration Effort**: Existing applications need modification to adopt surrogate key patterns
- **Complexity**: Direct surrogate key management in application logic

## Step 4: Compatibility Views - `RelationalFlattening/create-all-views.sql`

### View-Based Abstraction Layer
The views provide a compatibility layer that preserves natural key interfaces while utilizing surrogate key structures underneath.

#### View Design Pattern:
```sql
CREATE VIEW edfi.vw_studentschoolassociation AS
SELECT
    -- All original flattened table columns
    ssa.surrogateId,
    ssa.entrydate,
    ssa.schoolyear,
    -- ... other columns

    -- Retrieved natural key columns via surrogate key joins
    s.studentusi,           -- From student table via Student_SurrogateId
    sch.schoolid           -- From school table via School_SurrogateId
FROM edfi.studentschoolassociation ssa
    INNER JOIN edfi.student s ON ssa.student_surrogateid = s.surrogateid
    INNER JOIN edfi.school sch ON ssa.school_surrogateid = sch.surrogateid;
```

#### View Coverage:
- **Primary Associations**: `vw_studentschoolassociation`, `vw_studenteducationorganizationassociation`
- **Characteristic Views**: `vw_studenteducationorganizationassociationstudentcharacteristic`
- **Race/Ethnicity Views**: `vw_studenteducationorganizationassociationrace`
- **Program Views**: `vw_studentprogramassociation`, `vw_generalstudentprogramassociation`

## Step 5: Views-Based Function - `sp_iMart_Transform_DIM_STUDENT_edfi_Postgres_Views.sql`

### Views-Based Implementation
The views-based function explores a compatibility approach balancing interface preservation with structural changes.

#### Key Features:
1. **Seamless Migration Path**
   - Uses compatibility views that expose natural key columns
   - Requires minimal changes to existing query logic
   - Maintains identical output format and business logic

2. **Structural Approach**
   - Utilizes surrogate key joins internally within views
   - Allows PostgreSQL optimizer to process predicates through view layer
   - Attempts to balance performance with interface compatibility

3. **Maintainability**
   - Original query patterns remain largely unchanged
   - Views abstract the complexity of surrogate key management
   - Easy to understand and maintain for developers familiar with original schema

#### Implementation Approach:
- **View Substitution**: Replaced direct table references with corresponding compatibility views
- **Logic Preservation**: Maintained all original business rules and data transformations
- **Interface Compatibility**: Ensured identical column names and data types in output

## Performance Comparison Results

Performance tests conducted on PostgreSQL 13 with Northridge test dataset (21,628 students):

| Approach | Runs | Rows Returned | Average Time (ms) | % Change from Original |
|----------|------|---------------|-------------------|------------------------|
| **Original (Natural Keys)** | 10 | 21,642 | 3,067.42 | **Baseline** |
| **Views-Based (Surrogate Keys)** | 10 | 21,642 | 3,779.59 | **+23.2%** (slower) |
| **Direct Joins (Surrogate Keys)** | 10 | 21,642 | 4,272.58 | **+39.3%** (slower) |

### Key Findings:
- natural key approach outperformed surrogate key implementations
- **Views-based approach** showed 23% performance degradation vs original
- **Direct joins approach** showed 39% performance degradation vs original
- **All approaches** returned identical row counts, confirming data integrity

### Further Analysis: Why Direct Joins Underperformed Views

The counterintuitive result that views-based surrogate keys outperformed direct joins may be explained by PostgreSQL's query optimization behavior:

**Views-Based Advantages:**
- **Predicate Pushdown**: PostgreSQL can analyze the complete query pattern through views and push filtering conditions down to the most selective base tables
- **Join Reordering**: The query planner has flexibility to reorder joins based on cost estimates and table statistics
- **Optimization Flexibility**: Views allow the optimizer to consider multiple execution plans and choose optimal join algorithms

**Direct Joins Limitations:**
- **Fixed Join Order**: Hand-coded join sequences may not align with PostgreSQL's cost-based optimization
- **Reduced Planner Options**: Explicit join order limits the optimizer's ability to find better execution paths
- **Statistics Utilization**: Views may provide better cardinality estimates, leading to more accurate execution plans

This suggests that modern query optimizers can sometimes outperform manual optimization attempts, particularly when working through well-designed abstraction layers that preserve optimization opportunities.

