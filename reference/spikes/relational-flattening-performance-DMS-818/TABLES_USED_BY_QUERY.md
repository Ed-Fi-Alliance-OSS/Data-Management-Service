# Ed-Fi Tables Used in sp_iMart_Transform_DIM_STUDENT_edfi_Postgres Function

This document lists all Ed-Fi database tables referenced in the PostgreSQL function `sp_iMart_Transform_DIM_STUDENT_edfi_Postgres()` along with their primary key columns.

## Core Tables

### 1. **edfi.student**
**Purpose**: Main student entity containing basic student identifiers and demographics  
**Primary Key**: 
- `changeversion`
- `studentusi`

**Usage in Function**: Source of student demographic data (name, birth date, unique ID)

---

### 2. **edfi.studentschoolassociation** 
**Purpose**: Represents enrollment relationship between students and schools  
**Primary Key**:
- `changeversion` 
- `entrydate`
- `schoolid`
- `studentusi`

**Usage in Function**: Core enrollment data, entry dates, grade levels, withdrawal information, school year filtering

---

### 3. **edfi.studenteducationorganizationassociation**
**Purpose**: Associates students with education organizations (schools, districts)  
**Primary Key**:
- `changeversion`
- `educationorganizationid` 
- `studentusi`

**Usage in Function**: Links students to education organizations for demographic and program data

---

### 4. **edfi.school**
**Purpose**: School/education organization entity  
**Primary Key**:
- `schoolid`

**Usage in Function**: School information, LEA ID retrieval

---

## Descriptor Tables

### 5. **edfi.descriptor**
**Purpose**: Base table for all Ed-Fi descriptors (lookup values)  
**Primary Key**:
- `changeversion`
- `descriptorid`

**Usage in Function**: Lookup values for race, gender, grade levels, student characteristics

---

### 6. **edfi.exitwithdrawtypedescriptor**
**Purpose**: Descriptor for student withdrawal/exit types  
**Primary Key**:
- `exitwithdrawtypedescriptorid`

**Usage in Function**: Withdrawal reason codes and descriptions

---

## Association Tables (Student Demographics)

### 7. **edfi.studenteducationorganizationassociationrace**
**Purpose**: Student race/ethnicity associations  
**Primary Key**:
- `educationorganizationid`
- `racedescriptorid`
- `studentusi`

**Usage in Function**: Race/ethnicity classification (White, Black, Asian, etc.), federal race indicators

---

### 8. **edfi.studenteducationorganizationassociationstudentcharacteristic**
**Purpose**: Student demographic characteristics  
**Primary Key**:
- `educationorganizationid`
- `studentcharacteristicdescriptorid` 
- `studentusi`

**Usage in Function**: Special population indicators (homeless, migrant, military family, etc.)

---

### 9. **edfi.studenteducationorganizationassociationstudentindicator**
**Purpose**: Student indicator flags  
**Primary Key**:
- `educationorganizationid`
- `indicatorname`
- `studentusi`

**Usage in Function**: At-risk indicators and other student flags

---

## Program Association Tables

### 10. **edfi.studentprogramassociation**
**Purpose**: Associates students with educational programs  
**Primary Key**:
- `begindate`
- `educationorganizationid`
- `programeducationorganizationid`
- `programname`
- `programtypedescriptorid`
- `studentusi`

**Usage in Function**: General program participation, food service eligibility

---

### 11. **edfi.generalstudentprogramassociation**
**Purpose**: Base association for general student program participation  
**Primary Key**:
- `begindate`
- `changeversion`
- `educationorganizationid`
- `programeducationorganizationid`
- `programname`
- `programtypedescriptorid`
- `studentusi`

**Usage in Function**: Program participation processing

---

### 12. **edfi.studentspecialeducationprogramassociation**
**Purpose**: Special education program associations  
**Primary Key**:
- `begindate`
- `educationorganizationid`
- `programeducationorganizationid`
- `programname`
- `programtypedescriptorid`
- `studentusi`

**Usage in Function**: Special education/disability indicators

---

## Temporary Tables Created by Function

The function also creates 14 temporary tables for complex processing:

1. `temp_batch_periods` - Batch period filtering
2. `temp_student_base` - Student base data with MPI simulation
3. `temp_enrollment_type_descriptors` - Enrollment type processing  
4. `temp_calendar_complex` - Complex calendar and enrollment priority logic
5. `temp_race_complex` - Advanced race code calculation
6. `temp_gender` - Gender processing with sort order
7. `temp_characteristics` - Student characteristics aggregation
8. `temp_programs` - Program participation with overlap logic
9. `temp_food_service` - Food service eligibility processing
10. `temp_special_ed` - Special education indicators
11. `temp_grade` - Grade level processing
12. `temp_indicators` - Student indicators with at-risk calculation
13. `temp_language` - Language processing with priority logic
14. `temp_final` - Final assembly with correlations and complex field derivations

## Key Relationships

- **Students** are linked to **Schools** via `studentschoolassociation`
- **Students** are linked to **Education Organizations** via `studenteducationorganizationassociation`
- **Demographic data** (race, characteristics, indicators) is linked via education organization associations
- **Program participation** is tracked through various program association tables
- **Descriptors** provide lookup values for all coded fields

## Notes

- All tables use Ed-Fi standard schema structure
- Primary keys often include `changeversion` for data versioning
- Complex composite keys ensure proper relationship integrity
- The function implements enrollment priority logic using ROW_NUMBER() window functions across these relationships