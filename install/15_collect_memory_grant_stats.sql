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
Memory grant statistics collector
Collects memory grant semaphore data from sys.dm_exec_query_resource_semaphores
Stores MB values and calculates pressure warnings
Point-in-time snapshot data for memory grant pressure monitoring
*/

IF OBJECT_ID(N'collect.memory_grant_stats_collector', N'P') IS NULL
BEGIN
    EXECUTE(N'CREATE PROCEDURE collect.memory_grant_stats_collector AS RETURN 138;');
END;
GO

ALTER PROCEDURE
    collect.memory_grant_stats_collector
(
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
        @error_message nvarchar(4000);

    BEGIN TRY
        BEGIN TRANSACTION;

        /*
        Ensure target table exists
        */
        IF OBJECT_ID(N'collect.memory_grant_stats', N'U') IS NULL
        BEGIN
            /*
            Log missing table before attempting to create
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
                @start_time,
                N'memory_grant_stats_collector',
                N'TABLE_MISSING',
                0,
                0,
                N'Table collect.memory_grant_stats does not exist, calling ensure procedure'
            );

            /*
            Call procedure to create table
            */
            EXECUTE config.ensure_collection_table
                @table_name = N'memory_grant_stats',
                @debug = @debug;

            /*
            Verify table now exists
            */
            IF OBJECT_ID(N'collect.memory_grant_stats', N'U') IS NULL
            BEGIN
                ROLLBACK TRANSACTION;
                RAISERROR(N'Table collect.memory_grant_stats still missing after ensure procedure', 16, 1);
                RETURN;
            END;
        END;

        /*
        Collect memory grant semaphore statistics
        Stores MB values and calculates pressure warnings
        This is point-in-time state data showing current memory grant pressure
        */
        INSERT INTO
            collect.memory_grant_stats
        (
            resource_semaphore_id,
            pool_id,
            target_memory_mb,
            max_target_memory_mb,
            total_memory_mb,
            available_memory_mb,
            granted_memory_mb,
            used_memory_mb,
            grantee_count,
            waiter_count,
            timeout_error_count,
            forced_grant_count,
            available_memory_pressure_warning,
            waiter_count_warning,
            timeout_error_warning,
            forced_grant_warning
        )
        SELECT
            resource_semaphore_id = deqrs.resource_semaphore_id,
            pool_id = deqrs.pool_id,
            target_memory_mb = deqrs.target_memory_kb / 1024.0,
            max_target_memory_mb = deqrs.max_target_memory_kb / 1024.0,
            total_memory_mb = deqrs.total_memory_kb / 1024.0,
            available_memory_mb = deqrs.available_memory_kb / 1024.0,
            granted_memory_mb = deqrs.granted_memory_kb / 1024.0,
            used_memory_mb = deqrs.used_memory_kb / 1024.0,
            grantee_count = deqrs.grantee_count,
            waiter_count = deqrs.waiter_count,
            timeout_error_count = ISNULL(deqrs.timeout_error_count, 0),
            forced_grant_count = ISNULL(deqrs.forced_grant_count, 0),
            available_memory_pressure_warning =
                CASE
                    WHEN prev.available_memory_mb IS NOT NULL
                    AND  (deqrs.available_memory_kb / 1024.0) < (prev.available_memory_mb * 0.80)
                    THEN 1
                    ELSE 0
                END,
            waiter_count_warning =
                CASE
                    WHEN deqrs.waiter_count > 0
                    THEN 1
                    ELSE 0
                END,
            timeout_error_warning =
                CASE
                    WHEN ISNULL(deqrs.timeout_error_count, 0) > 0
                    THEN 1
                    ELSE 0
                END,
            forced_grant_warning =
                CASE
                    WHEN ISNULL(deqrs.forced_grant_count, 0) > 0
                    THEN 1
                    ELSE 0
                END
        FROM sys.dm_exec_query_resource_semaphores AS deqrs
        OUTER APPLY
        (
            SELECT TOP (1)
                prev.available_memory_mb
            FROM collect.memory_grant_stats AS prev
            WHERE prev.resource_semaphore_id = deqrs.resource_semaphore_id
            AND   prev.pool_id = deqrs.pool_id
            ORDER BY
                prev.collection_id DESC
        ) AS prev
        WHERE deqrs.max_target_memory_kb IS NOT NULL
        OPTION(RECOMPILE);

        SET @rows_collected = ROWCOUNT_BIG();

        /*
        Debug output for pressure warnings
        */
        IF @debug = 1
        BEGIN
            DECLARE
                @current_available_memory_mb decimal(19,2),
                @previous_available_memory_mb decimal(19,2),
                @current_waiter_count integer,
                @current_timeout_error_count bigint,
                @current_forced_grant_count bigint,
                @available_warning bit,
                @waiter_warning bit,
                @timeout_warning bit,
                @forced_warning bit;

            SELECT
                @current_available_memory_mb = mgs.available_memory_mb,
                @current_waiter_count = mgs.waiter_count,
                @current_timeout_error_count = mgs.timeout_error_count,
                @current_forced_grant_count = mgs.forced_grant_count,
                @available_warning = mgs.available_memory_pressure_warning,
                @waiter_warning = mgs.waiter_count_warning,
                @timeout_warning = mgs.timeout_error_warning,
                @forced_warning = mgs.forced_grant_warning
            FROM collect.memory_grant_stats AS mgs
            WHERE mgs.collection_id =
            (
                SELECT
                    MAX(mgs2.collection_id)
                FROM collect.memory_grant_stats AS mgs2
            );

            /*
            Get previous available memory for warning message
            */
            SELECT TOP (1)
                @previous_available_memory_mb = mgs.available_memory_mb
            FROM collect.memory_grant_stats AS mgs
            WHERE mgs.collection_id <
            (
                SELECT
                    MAX(mgs2.collection_id)
                FROM collect.memory_grant_stats AS mgs2
            )
            ORDER BY
                mgs.collection_id DESC;

            IF @available_warning = 1
            BEGIN
                DECLARE @available_msg nvarchar(500) =
                    N'WARNING: Available memory grant dropped from ' +
                    CONVERT(nvarchar(20), @previous_available_memory_mb) + N' MB to ' +
                    CONVERT(nvarchar(20), @current_available_memory_mb) + N' MB (>20% drop)';
                RAISERROR(@available_msg, 0, 1) WITH NOWAIT;
            END;

            IF @waiter_warning = 1
            BEGIN
                DECLARE @waiter_msg nvarchar(500) =
                    N'WARNING: Memory grant waiters detected: ' +
                    CONVERT(nvarchar(20), @current_waiter_count);
                RAISERROR(@waiter_msg, 0, 1) WITH NOWAIT;
            END;

            IF @timeout_warning = 1
            BEGIN
                DECLARE @timeout_msg nvarchar(500) =
                    N'WARNING: Memory grant timeout errors detected: ' +
                    CONVERT(nvarchar(20), @current_timeout_error_count);
                RAISERROR(@timeout_msg, 0, 1) WITH NOWAIT;
            END;

            IF @forced_warning = 1
            BEGIN
                DECLARE @forced_msg nvarchar(500) =
                    N'WARNING: Forced memory grants detected: ' +
                    CONVERT(nvarchar(20), @current_forced_grant_count);
                RAISERROR(@forced_msg, 0, 1) WITH NOWAIT;
            END;
        END;

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
            N'memory_grant_stats_collector',
            N'SUCCESS',
            @rows_collected,
            DATEDIFF(MILLISECOND, @start_time, SYSDATETIME())
        );

        IF @debug = 1
        BEGIN
            RAISERROR(N'Collected %d memory grant stats rows', 0, 1, @rows_collected) WITH NOWAIT;
        END;

        COMMIT TRANSACTION;

    END TRY
    BEGIN CATCH
        IF @@TRANCOUNT > 0
        BEGIN
            ROLLBACK TRANSACTION;
        END;

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
            N'memory_grant_stats_collector',
            N'ERROR',
            DATEDIFF(MILLISECOND, @start_time, SYSDATETIME()),
            @error_message
        );

        RAISERROR(N'Error in memory grant stats collector: %s', 16, 1, @error_message);
    END CATCH;
END;
GO

PRINT 'Memory grant stats collector created successfully';
PRINT 'Collects point-in-time memory grant semaphore data from sys.dm_exec_query_resource_semaphores';
PRINT 'Stores MB values and calculates pressure warnings for memory grant monitoring';
GO
