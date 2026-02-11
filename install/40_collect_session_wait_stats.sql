/*
Copyright 2026 Darling Data, LLC
https://www.erikdarling.com/

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

/*
Session Wait Stats Collector
Collects per-session wait statistics from sys.dm_exec_session_wait_stats
Allows correlation of waits to specific sessions/queries
Requires SQL Server 2016 SP1 or later
*/

IF OBJECT_ID(N'collect.session_wait_stats_collector', N'P') IS NULL
BEGIN
    EXECUTE(N'CREATE PROCEDURE collect.session_wait_stats_collector AS RETURN 138;');
END;
GO

ALTER PROCEDURE
    collect.session_wait_stats_collector
(
    @min_wait_time_ms integer = 100, /*Minimum wait time to capture (reduces noise)*/
    @debug bit = 0 /*Print debugging information*/
)
WITH RECOMPILE
AS
BEGIN
    SET NOCOUNT ON;
    SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;

    DECLARE
        @rows_collected bigint = 0,
        @start_time datetime2(7) = SYSDATETIME(),
        @error_message nvarchar(4000),
        @sql_version integer;

    /*
    Check SQL Server version - dm_exec_session_wait_stats requires 2016 SP1+
    */
    SELECT
        @sql_version = CONVERT(integer, SERVERPROPERTY(N'ProductMajorVersion'));

    IF @sql_version < 13
    BEGIN
        IF @debug = 1
        BEGIN
            RAISERROR(N'session_wait_stats_collector requires SQL Server 2016 or later (current: %d)', 0, 1, @sql_version) WITH NOWAIT;
        END;

        INSERT INTO
            config.collection_log
        (
            collector_name,
            collection_status,
            rows_collected,
            duration_ms,
            error_message
        )
        VALUES
        (
            N'session_wait_stats_collector',
            N'SKIPPED',
            0,
            DATEDIFF(MILLISECOND, @start_time, SYSDATETIME()),
            N'Requires SQL Server 2016 SP1 or later'
        );

        RETURN;
    END;

    BEGIN TRY
        /*
        Ensure target table exists
        */
        IF OBJECT_ID(N'collect.session_wait_stats', N'U') IS NULL
        BEGIN
            INSERT INTO
                config.collection_log
            (
                collection_time,
                collector_name,
                collection_status,
                rows_collected,
                duration_ms,
                error_message
            )
            VALUES
            (
                @start_time,
                N'session_wait_stats_collector',
                N'TABLE_MISSING',
                0,
                0,
                N'Table collect.session_wait_stats does not exist'
            );

            RAISERROR(N'Table collect.session_wait_stats does not exist. Please run 02_create_tables.sql', 16, 1);
            RETURN;
        END;

        /*
        Ensure config.ignored_wait_types exists
        */
        IF OBJECT_ID(N'config.ignored_wait_types', N'U') IS NULL
        OR NOT EXISTS (SELECT 1/0 FROM config.ignored_wait_types WHERE is_enabled = 1)
        BEGIN
            IF @debug = 1
            BEGIN
                RAISERROR(N'config.ignored_wait_types table missing or empty - calling ensure_config_tables', 0, 1) WITH NOWAIT;
            END;

            EXECUTE config.ensure_config_tables
                @debug = @debug;
        END;

        /*
        Collect session wait statistics
        Joins with sys.dm_exec_sessions for context
        Joins with sys.dm_exec_requests for current query info
        Filters out ignored wait types and low wait times
        */
        INSERT INTO
            collect.session_wait_stats
        (
            session_id,
            wait_type,
            waiting_tasks_count,
            wait_time_ms,
            max_wait_time_ms,
            signal_wait_time_ms,
            database_id,
            database_name,
            login_name,
            host_name,
            program_name,
            sql_handle,
            query_text
        )
        SELECT
            session_id = sws.session_id,
            wait_type = sws.wait_type,
            waiting_tasks_count = sws.waiting_tasks_count,
            wait_time_ms = sws.wait_time_ms,
            max_wait_time_ms = sws.max_wait_time_ms,
            signal_wait_time_ms = sws.signal_wait_time_ms,
            database_id = s.database_id,
            database_name = DB_NAME(s.database_id),
            login_name = s.login_name,
            host_name = s.host_name,
            program_name = s.program_name,
            sql_handle = r.sql_handle,
            query_text = st.text
        FROM sys.dm_exec_session_wait_stats AS sws
        JOIN sys.dm_exec_sessions AS s
          ON s.session_id = sws.session_id
        LEFT JOIN sys.dm_exec_requests AS r
          ON r.session_id = sws.session_id
        OUTER APPLY sys.dm_exec_sql_text(r.sql_handle) AS st
        WHERE sws.wait_time_ms >= @min_wait_time_ms
        AND   s.is_user_process = 1
        AND   NOT EXISTS
        (
            SELECT
                1/0
            FROM config.ignored_wait_types AS iwt
            WHERE iwt.wait_type = sws.wait_type
            AND   iwt.is_enabled = 1
        )
        OPTION(RECOMPILE);

        SET @rows_collected = ROWCOUNT_BIG();

        /*
        Log successful collection
        */
        INSERT INTO
            config.collection_log
        (
            collector_name,
            collection_status,
            rows_collected,
            duration_ms
        )
        VALUES
        (
            N'session_wait_stats_collector',
            N'SUCCESS',
            @rows_collected,
            DATEDIFF(MILLISECOND, @start_time, SYSDATETIME())
        );

        IF @debug = 1
        BEGIN
            RAISERROR(N'Collected %d session wait stats rows', 0, 1, @rows_collected) WITH NOWAIT;
        END;

    END TRY
    BEGIN CATCH
        SET @error_message = ERROR_MESSAGE();

        /*
        Log the error
        */
        INSERT INTO
            config.collection_log
        (
            collector_name,
            collection_status,
            duration_ms,
            error_message
        )
        VALUES
        (
            N'session_wait_stats_collector',
            N'ERROR',
            DATEDIFF(MILLISECOND, @start_time, SYSDATETIME()),
            @error_message
        );

        RAISERROR(N'Error in session_wait_stats_collector: %s', 16, 1, @error_message);
    END CATCH;
END;
GO

PRINT 'Session wait stats collector created successfully';
PRINT 'Use collect.session_wait_stats_collector to collect per-session wait statistics';
PRINT 'Requires SQL Server 2016 SP1 or later';
GO
