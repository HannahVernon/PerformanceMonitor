/*
Copyright 2026 Darling Data, LLC
https://www.erikdarling.com/

For usage, licensing, and support:
https://github.com/erikdarlingdata/DarlingData

Long Query Trace Management Collector - Performance Monitor
Erik Darling - erik@erikdarling.com
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

IF OBJECT_ID(N'collect.trace_management_collector', N'P') IS NULL
BEGIN
    EXECUTE(N'CREATE PROCEDURE collect.trace_management_collector AS RETURN 138;');
END;
GO

ALTER PROCEDURE
    collect.trace_management_collector
(
    @action varchar(20) = 'START', /*START, STOP, STATUS, or RESTART*/
    @trace_name varchar(50) = 'LongQueries', /*name identifier for trace files*/
    @duration_threshold_ms bigint = 2000000, /*minimum duration in microseconds (2 seconds)*/
    @cpu_threshold_ms integer = 1000, /*minimum CPU time in milliseconds (1 second)*/
    @max_file_size_mb bigint = 200, /*maximum trace file size in MB*/
    @debug bit = 0 /*prints additional diagnostic information*/
)
WITH RECOMPILE
AS
BEGIN
    SET NOCOUNT ON;
    SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;

    /*
    Declare variables for trace management
    */
    DECLARE
        @trace_id integer = NULL,
        @file_path nvarchar(4000) = N'',
        @trace_search_pattern nvarchar(4000) = N'',
        @error_log_path nvarchar(4000) = N'',
        @trace_status integer = 0,
        @existing_trace_count integer = 0,
        @sql nvarchar(max) = N'',
        @error_message nvarchar(2048) = N'',
        @collection_start_time datetime2(7) = SYSDATETIME(),
        @rows_affected bigint = 0;

    BEGIN TRY
        /*
        Parameter validation
        */
        IF @action NOT IN (N'START', N'STOP', N'STATUS', N'RESTART')
        BEGIN
            SET @error_message = N'@action must be START, STOP, STATUS, or RESTART';
            RAISERROR(@error_message, 16, 1);
            RETURN;
        END;

        IF @duration_threshold_ms < 0 OR @duration_threshold_ms > 2147483647 -- ~35 minutes
        BEGIN
            SET @error_message = N'@duration_threshold_ms must be between 0 and 2,147,483,647 (~35 minutes)';
            RAISERROR(@error_message, 16, 1);
            RETURN;
        END;

        IF @cpu_threshold_ms < 0 OR @cpu_threshold_ms > 3600000 -- 1 hour
        BEGIN
            SET @error_message = N'@cpu_threshold_ms must be between 0 and 3,600,000 (1 hour)';
            RAISERROR(@error_message, 16, 1);
            RETURN;
        END;

        /*
        Get SQL Server error log path dynamically
        */
        SELECT @error_log_path =
            LEFT
            (
                CONVERT(nvarchar(4000), SERVERPROPERTY('ErrorLogFileName')),
                LEN(CONVERT(nvarchar(4000), SERVERPROPERTY('ErrorLogFileName'))) -
                CHARINDEX('\', REVERSE(CONVERT(nvarchar(4000), SERVERPROPERTY('ErrorLogFileName')))) + 1
            );

        /*
        Build search pattern to find ALL traces for this trace name (without timestamp)
        Format: Monitor_LongQueries_%
        */
        SET @trace_search_pattern = @error_log_path + N'Monitor_' + @trace_name + N'_%';

        /*
        Build unique trace file name with datetime stamp to avoid file conflicts
        Format: Monitor_LongQueries_20260101_151030_
        */
        SET @file_path = @error_log_path + N'Monitor_' + @trace_name + N'_' +
            CONVERT(varchar(8), @collection_start_time, 112) + N'_' +
            REPLACE(CONVERT(varchar(8), @collection_start_time, 108), ':', '') + N'_';

        IF @debug = 1
        BEGIN
            RAISERROR(N'Trace search pattern: %s', 0, 1, @trace_search_pattern) WITH NOWAIT;
            RAISERROR(N'New trace file path: %s%%d.trc', 0, 1, @file_path) WITH NOWAIT;
        END;

        /*
        Get existing traces matching our pattern (all traces for this name, regardless of timestamp)
        */
        SELECT
            @existing_trace_count = COUNT_BIG(*)
        FROM sys.traces AS t
        WHERE t.path LIKE @trace_search_pattern
        AND   t.is_default = 0

        IF @debug = 1
        BEGIN
            RAISERROR(N'Found %d existing traces with pattern: %s', 0, 1, @existing_trace_count, @trace_search_pattern) WITH NOWAIT;
        END;

        /*
        Handle different actions
        */
        IF @action = N'STATUS'
        BEGIN
            /*
            Show status of existing traces
            */
            SELECT
                trace_id = t.id,
                trace_status =
                    CASE t.status
                         WHEN 0 THEN 'STOPPED'
                         WHEN 1 THEN 'RUNNING'
                         ELSE 'UNKNOWN'
                    END,
                file_path = t.path,
                max_size_mb = t.max_size / 1024 / 1024,
                stop_time = t.stop_time,
                max_files = t.max_files,
                is_rowset = t.is_rowset,
                is_rollover = t.is_rollover,
                is_shutdown = t.is_shutdown,
                is_default = t.is_default,
                buffer_count = t.buffer_count,
                buffer_size = t.buffer_size,
                file_position = t.file_position,
                reader_spid = t.reader_spid,
                start_time = t.start_time,
                last_event_time = t.last_event_time,
                dropped_event_count = t.dropped_event_count,
                event_count = t.event_count
            FROM sys.traces AS t
            WHERE (t.path LIKE @trace_search_pattern AND t.is_default = 0)
            OR    t.id = 1 /*always show default trace*/
            ORDER BY
                t.id;
        END;
        ELSE IF @action = N'STOP'
        BEGIN
            /*
            Stop ALL existing traces matching our pattern (regardless of timestamp)
            */
            DECLARE @trace_cursor CURSOR

            SET @trace_cursor =
                CURSOR
                LOCAL
                STATIC
                READ_ONLY
            FOR
                SELECT
                    t.id
                FROM sys.traces AS t
                WHERE t.path LIKE @trace_search_pattern
                AND   t.is_default = 0
                ORDER BY
                    t.id;

            OPEN @trace_cursor;
            
            FETCH NEXT 
            FROM @trace_cursor 
            INTO @trace_id;

            WHILE @@FETCH_STATUS = 0
            BEGIN
                EXECUTE sys.sp_trace_setstatus 
                    @trace_id, 
                    0; /*stop*/
                
                EXECUTE sys.sp_trace_setstatus 
                    @trace_id, 
                    2; /*close and delete*/

                IF @debug = 1
                BEGIN
                    RAISERROR(N'Stopped and closed trace ID: %d', 0, 1, @trace_id) WITH NOWAIT;
                END;

                SET @rows_affected += 1;

                FETCH NEXT
                FROM @trace_cursor
                INTO @trace_id;
            END;

            IF @rows_affected = 0
            BEGIN
                RAISERROR(N'No running traces found to stop with pattern: %s', 0, 1, @trace_search_pattern) WITH NOWAIT;
            END;
        END;
        ELSE IF @action IN (N'START', N'RESTART')
        BEGIN
            /*
            Stop ALL existing traces first if RESTART (regardless of timestamp)
            */
            IF @action = N'RESTART'
            BEGIN
                DECLARE @restart_cursor CURSOR;

                SET @restart_cursor =
                    CURSOR
                    LOCAL
                    STATIC
                    READ_ONLY
                FOR
                SELECT
                    t.id
                FROM sys.traces AS t
                WHERE t.path LIKE @trace_search_pattern
                AND   t.is_default = 0
                ORDER BY
                    t.id;

                OPEN @restart_cursor;
                
                FETCH NEXT 
                FROM @restart_cursor 
                INTO @trace_id;

                WHILE @@FETCH_STATUS = 0
                BEGIN
                    EXECUTE sys.sp_trace_setstatus 
                        @trace_id, 
                        0; /*stop*/
                    
                    EXECUTE sys.sp_trace_setstatus 
                        @trace_id, 
                        2; /*close and delete*/

                    IF @debug = 1
                    BEGIN
                        RAISERROR(N'Stopped existing trace ID for restart: %d', 0, 1, @trace_id) WITH NOWAIT;
                    END;

                    FETCH NEXT 
                    FROM @restart_cursor 
                    INTO @trace_id;
                END;
            END;

            /*
            Check for existing running traces (any with this trace name)
            If already running, return success (idempotent behavior for scheduled operation)
            */
            IF @action = N'START'
            AND EXISTS
            (
                SELECT
                    1/0
                FROM sys.traces AS t
                WHERE t.path LIKE @trace_search_pattern
                AND   t.is_default = 0
                AND   t.status = 1
            )
            BEGIN
                /*
                Get the existing trace ID for the return result
                */
                SELECT TOP (1)
                    @trace_id = t.id
                FROM sys.traces AS t
                WHERE t.path LIKE @trace_search_pattern
                AND   t.is_default = 0
                AND   t.status = 1;

                IF @debug = 1
                BEGIN
                    RAISERROR(N'Trace already running (ID: %d) - no action needed', 0, 1, @trace_id) WITH NOWAIT;
                END;

                /*
                Log success and return - trace is already running as desired
                */
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
                    @collection_start_time,
                    N'trace_management_collector',
                    N'SUCCESS',
                    0,
                    DATEDIFF(MILLISECOND, @collection_start_time, SYSDATETIME()),
                    N'Trace already running - no action needed'
                );

                SELECT
                    action_performed = @action,
                    trace_id = @trace_id,
                    file_path = N'(existing trace)',
                    duration_threshold_seconds = @duration_threshold_ms / 1000000.0,
                    cpu_threshold_ms = @cpu_threshold_ms,
                    max_file_size_mb = @max_file_size_mb,
                    collection_time = @collection_start_time,
                    success = 1,
                    message = N'Trace already running';

                RETURN;
            END;

            /*
            Create new trace
            */
            EXECUTE sys.sp_trace_create 
                @traceid = @trace_id OUTPUT,
                @options = 2, /*file rollover enabled*/
                @tracefile = @file_path,
                @maxfilesize = @max_file_size_mb;

            IF @debug = 1
            BEGIN
                RAISERROR(N'Created trace ID: %d', 0, 1, @trace_id) WITH NOWAIT;
            END;

            /*
            Configure trace events for comprehensive long-running query capture
            RPC:Completed (Event 10) for stored procedures
            */
            EXECUTE sys.sp_trace_setevent @trace_id, 10, 1,  1;  /*TextData - procedure call with parameters*/
            EXECUTE sys.sp_trace_setevent @trace_id, 10, 3,  1;  /*DatabaseID*/
            EXECUTE sys.sp_trace_setevent @trace_id, 10, 6,  1;  /*NTUserName*/
            EXECUTE sys.sp_trace_setevent @trace_id, 10, 7,  1;  /*NTDomainName*/
            EXECUTE sys.sp_trace_setevent @trace_id, 10, 8,  1;  /*HostName*/
            EXECUTE sys.sp_trace_setevent @trace_id, 10, 9,  1;  /*ClientProcessID*/
            EXECUTE sys.sp_trace_setevent @trace_id, 10, 10, 1;  /*ApplicationName*/
            EXECUTE sys.sp_trace_setevent @trace_id, 10, 11, 1;  /*LoginName*/
            EXECUTE sys.sp_trace_setevent @trace_id, 10, 12, 1;  /*SPID*/
            EXECUTE sys.sp_trace_setevent @trace_id, 10, 13, 1;  /*Duration microseconds*/
            EXECUTE sys.sp_trace_setevent @trace_id, 10, 14, 1;  /*StartTime*/
            EXECUTE sys.sp_trace_setevent @trace_id, 10, 15, 1;  /*EndTime*/
            EXECUTE sys.sp_trace_setevent @trace_id, 10, 16, 1;  /*Reads*/
            EXECUTE sys.sp_trace_setevent @trace_id, 10, 17, 1;  /*Writes*/
            EXECUTE sys.sp_trace_setevent @trace_id, 10, 18, 1;  /*CPU*/
            EXECUTE sys.sp_trace_setevent @trace_id, 10, 22, 1;  /*ObjectID*/
            EXECUTE sys.sp_trace_setevent @trace_id, 10, 35, 1;  /*DatabaseName*/
            EXECUTE sys.sp_trace_setevent @trace_id, 10, 48, 1;  /*RowCounts*/

            /*
            SQL:BatchCompleted (Event 12) for ad-hoc queries
            */
            EXECUTE sys.sp_trace_setevent @trace_id, 12, 1,  1;  /*TextData - full SQL*/
            EXECUTE sys.sp_trace_setevent @trace_id, 12, 3,  1;  /*DatabaseID*/
            EXECUTE sys.sp_trace_setevent @trace_id, 12, 6,  1;  /*NTUserName*/
            EXECUTE sys.sp_trace_setevent @trace_id, 12, 7,  1;  /*NTDomainName*/
            EXECUTE sys.sp_trace_setevent @trace_id, 12, 8,  1;  /*HostName*/
            EXECUTE sys.sp_trace_setevent @trace_id, 12, 9,  1;  /*ClientProcessID*/
            EXECUTE sys.sp_trace_setevent @trace_id, 12, 10, 1;  /*ApplicationName*/
            EXECUTE sys.sp_trace_setevent @trace_id, 12, 11, 1;  /*LoginName*/
            EXECUTE sys.sp_trace_setevent @trace_id, 12, 12, 1;  /*SPID*/
            EXECUTE sys.sp_trace_setevent @trace_id, 12, 13, 1;  /*Duration*/
            EXECUTE sys.sp_trace_setevent @trace_id, 12, 14, 1;  /*StartTime*/
            EXECUTE sys.sp_trace_setevent @trace_id, 12, 15, 1;  /*EndTime*/
            EXECUTE sys.sp_trace_setevent @trace_id, 12, 16, 1;  /*Reads*/
            EXECUTE sys.sp_trace_setevent @trace_id, 12, 17, 1;  /*Writes*/
            EXECUTE sys.sp_trace_setevent @trace_id, 12, 18, 1;  /*CPU*/
            EXECUTE sys.sp_trace_setevent @trace_id, 12, 35, 1;  /*DatabaseName*/
            EXECUTE sys.sp_trace_setevent @trace_id, 12, 48, 1;  /*RowCounts*/

            /*
            Attention (Event 16) for query cancellations/timeouts
            */
            EXECUTE sys.sp_trace_setevent @trace_id, 16, 1,  1;  /*TextData*/
            EXECUTE sys.sp_trace_setevent @trace_id, 16, 11, 1;  /*LoginName*/
            EXECUTE sys.sp_trace_setevent @trace_id, 16, 12, 1;  /*SPID*/
            EXECUTE sys.sp_trace_setevent @trace_id, 16, 14, 1;  /*StartTime*/
            EXECUTE sys.sp_trace_setevent @trace_id, 16, 35, 1;  /*DatabaseName*/

            /*
            Set performance-based filters
            Duration filter (either long duration OR high CPU)
            */
            EXECUTE sys.sp_trace_setfilter @trace_id, 13, 0, 4, @duration_threshold_ms;
            EXECUTE sys.sp_trace_setfilter @trace_id, 18, 1, 4, @cpu_threshold_ms;

            /*
            Exclude system databases and PerformanceMonitor for cleaner data
            */
            EXECUTE sys.sp_trace_setfilter @trace_id, 35, 0, 7, N'master';
            EXECUTE sys.sp_trace_setfilter @trace_id, 35, 0, 7, N'msdb';
            EXECUTE sys.sp_trace_setfilter @trace_id, 35, 0, 7, N'model';
            EXECUTE sys.sp_trace_setfilter @trace_id, 35, 0, 7, N'tempdb';
            EXECUTE sys.sp_trace_setfilter @trace_id, 35, 0, 7, N'PerformanceMonitor';

            /*
            Exclude system processes and maintenance
            */
            EXECUTE sys.sp_trace_setfilter @trace_id, 10, 0, 7, N'%Replication%';
            EXECUTE sys.sp_trace_setfilter @trace_id, 10, 0, 7, N'%Backup%';

            /*
            Start the trace
            */
            EXECUTE sys.sp_trace_setstatus 
                @trace_id, 
                1;

            SET @rows_affected = 1;

            IF @debug = 1
            BEGIN
                RAISERROR(N'Started trace ID: %d with filters - Duration >= %d microseconds OR CPU >= %d ms', 0, 1,
                         @trace_id, @duration_threshold_ms, @cpu_threshold_ms) WITH NOWAIT;
            END;
        END;

        /*
        Log collection activity
        */
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
            @collection_start_time,
            N'trace_management_collector',
            N'SUCCESS',
            @rows_affected,
            DATEDIFF(MILLISECOND, @collection_start_time, SYSDATETIME()),
            NULL
        );

        /*
        Return summary information
        */
        SELECT
            action_performed = @action,
            trace_id = @trace_id,
            file_path = @file_path + N'%d.trc',
            duration_threshold_seconds = @duration_threshold_ms / 1000000.0,
            cpu_threshold_ms = @cpu_threshold_ms,
            max_file_size_mb = @max_file_size_mb,
            collection_time = @collection_start_time,
            success = 1;

    END TRY
    BEGIN CATCH
        SET @error_message = ERROR_MESSAGE();

        /*
        Log errors to collection log
        */
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
            @collection_start_time,
            N'trace_management_collector',
            N'ERROR',
            0,
            DATEDIFF(MILLISECOND, @collection_start_time, SYSDATETIME()),
            @error_message
        );

        IF @@TRANCOUNT > 0
        BEGIN
            ROLLBACK;
        END;

        THROW;
    END CATCH;
END;
GO
