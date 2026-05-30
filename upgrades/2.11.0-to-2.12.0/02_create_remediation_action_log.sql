/*
Copyright 2026 Darling Data, LLC
https://www.erikdarling.com/

Upgrade from 2.11.0 to 2.12.0
Creates config.remediation_action_log — the durable, append-only audit trail for
the approval-gated "Apply Fix" feature (B3). One row is written per apply/unapply
attempt (success, skip, error, or abort), so every privileged mutation the
Dashboard issues against a monitored server (today: sys.sp_query_store_force_plan
for a plan regression) is recorded with who/what/when/where/result.

Lives in the config schema alongside the other operational logs
(config.collection_log, config.installation_history). Idempotent: guarded by
OBJECT_ID so a re-run is a no-op.

B3 is coupled to this 2.12.0 schema: the Dashboard hard-blocks Apply on any
server where this table does not yet exist (no mutation is attempted), so an
un-upgraded server can never produce an applied-but-unlogged action.
*/

SET ANSI_NULLS ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET ARITHABORT ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET QUOTED_IDENTIFIER ON;
SET NUMERIC_ROUNDABORT OFF;
SET IMPLICIT_TRANSACTIONS OFF;
SET STATISTICS TIME, IO OFF;
GO

USE PerformanceMonitor;
GO

IF OBJECT_ID(N'config.remediation_action_log', N'U') IS NULL
BEGIN
    CREATE TABLE
        config.remediation_action_log
    (
        action_id bigint IDENTITY(1, 1) NOT NULL
            CONSTRAINT pk_remediation_action_log
            PRIMARY KEY CLUSTERED,
        applied_utc datetime2(7) NOT NULL
            CONSTRAINT df_remediation_action_log_applied_utc
            DEFAULT (SYSUTCDATETIME()),
        operator_identity nvarchar(256) NULL, /* app / OS identity that clicked Apply */
        executing_login sysname NULL,         /* SUSER_SNAME() actually used on the target */
        used_elevated_cred bit NOT NULL
            CONSTRAINT df_remediation_action_log_used_elevated_cred
            DEFAULT (0),                       /* always 0 in v1 (no in-app elevation); reserved */
        target_server nvarchar(256) NULL,
        target_database sysname NOT NULL,
        finding_fact_key varchar(64) NOT NULL, /* e.g. 'PLAN_REGRESSION' */
        query_id bigint NOT NULL,
        plan_id bigint NOT NULL,
        action varchar(16) NOT NULL,           /* 'force' | 'unforce' */
        generated_sql nvarchar(max) NULL,      /* the previewed statement, recorded only — never executed */
        result varchar(16) NOT NULL,           /* 'success' | 'skipped' | 'error' | 'aborted' */
        error_message nvarchar(max) NULL,
        source_alert_ref nvarchar(256) NULL    /* metric_name / story hash for traceback */
    );

    PRINT 'Created config.remediation_action_log';
END;
ELSE
BEGIN
    PRINT 'config.remediation_action_log already exists — no action taken';
END;
GO
