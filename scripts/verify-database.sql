-- OpsConnectDB Foundation Verification Script
SELECT s.name AS SchemaName, t.name AS TableName
FROM sys.tables t
INNER JOIN sys.schemas s ON t.schema_id = s.schema_id
ORDER BY s.name, t.name;

SELECT ActivityId, ActivityCode, ActivityName, IsActive FROM activity.ActivityMaster;
SELECT ConfigurationId, ActivityId, ReportingStartTime, ReportingEndTime, ReportingIntervalMinutes, TimeZoneId, IsReportingEnabled, StopWhenAllCompleted FROM admin.ActivityReportingConfiguration;
SELECT RoleId, RoleName, IsActive FROM admin.RoleMaster;
