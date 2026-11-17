SET NOCOUNT ON;
IF DB_ID('dms1521_xtp_probe') IS NOT NULL BEGIN ALTER DATABASE dms1521_xtp_probe SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE dms1521_xtp_probe; END;
CREATE DATABASE dms1521_xtp_probe;
GO
USE dms1521_xtp_probe;
GO
PRINT '--- version'; SELECT CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(32)) AS version, CAST(SERVERPROPERTY('ProductUpdateLevel') AS nvarchar(32)) AS cu, recovery_model_desc FROM sys.databases WHERE name = DB_NAME();
-- the runner's provisioning (KeysetForms.MemoryOptimizedProvisioningSql)
DECLARE @database sysname = DB_NAME();
DECLARE @path nvarchar(4000) = CAST(SERVERPROPERTY('InstanceDefaultDataPath') AS nvarchar(4000)) + @database + N'_dms1521_mod';
DECLARE @addFilegroup nvarchar(max) = N'ALTER DATABASE ' + QUOTENAME(@database) + N' ADD FILEGROUP [dms1521_mod] CONTAINS MEMORY_OPTIMIZED_DATA;';
DECLARE @addFile nvarchar(max) = N'ALTER DATABASE ' + QUOTENAME(@database) + N' ADD FILE (NAME = N''dms1521_mod'', FILENAME = N''' + @path + N''') TO FILEGROUP [dms1521_mod];';
EXEC (@addFilegroup); EXEC (@addFile);
EXEC (N'CREATE TYPE dbo.dms1521_PageKeysetMem AS TABLE ([DocumentId] bigint NOT NULL PRIMARY KEY NONCLUSTERED, [Ordinal] int NULL) WITH (MEMORY_OPTIMIZED = ON);');
GO
DECLARE @page dbo.dms1521_PageKeysetMem; INSERT INTO @page VALUES (1, 0), (2, 1); SELECT COUNT(*) AS rows_in_table_variable FROM @page;
GO
PRINT '--- after provisioning'; SELECT deployment_state, deployment_state_desc FROM sys.dm_db_xtp_undeploy_status;
GO
PRINT '--- REMOVE FILE while the type exists (expected to fail)';
ALTER DATABASE dms1521_xtp_probe REMOVE FILE dms1521_mod;
GO
DROP TYPE dbo.dms1521_PageKeysetMem;
GO
PRINT '--- REMOVE FILE after DROP TYPE';
ALTER DATABASE dms1521_xtp_probe REMOVE FILE dms1521_mod;
GO
SELECT deployment_state, deployment_state_desc, undeploy_lsn, start_of_log_lsn FROM sys.dm_db_xtp_undeploy_status;
GO
DECLARE @i int = 0;
WHILE @i < 10 AND EXISTS (SELECT 1 FROM sys.dm_db_xtp_undeploy_status WHERE deployment_state <> 0)
BEGIN CHECKPOINT; WAITFOR DELAY '00:00:02'; SET @i += 1; END;
SELECT @i AS checkpoints_issued, deployment_state, deployment_state_desc FROM sys.dm_db_xtp_undeploy_status;
GO
PRINT '--- REMOVE FILEGROUP';
ALTER DATABASE dms1521_xtp_probe REMOVE FILEGROUP dms1521_mod;
GO
SELECT name, type_desc FROM sys.filegroups; SELECT COUNT(*) AS memory_optimized_files FROM sys.database_files WHERE type_desc = 'FILESTREAM';
GO
USE master; DROP DATABASE dms1521_xtp_probe;
GO
