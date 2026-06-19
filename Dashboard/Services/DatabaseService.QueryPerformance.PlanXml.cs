/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using PerformanceMonitor.Ui;
using PerformanceMonitorDashboard.Helpers;
using PerformanceMonitorDashboard.Models;
using PerformanceMonitor.Common;

namespace PerformanceMonitorDashboard.Services
{
    public partial class DatabaseService
    {
        /// <summary>
        /// Fetches query plan XML on demand for a single query_store_data row.
        /// </summary>
        public async Task<string?> GetQueryStorePlanXmlByCollectionIdAsync(long collectionId)
        {
            await using var tc = await OpenThrottledConnectionAsync();
            var connection = tc.Connection;

            string query = @"
        SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;

        SELECT
            CAST(DECOMPRESS(qsd.query_plan_text) AS nvarchar(max)) AS query_plan_text
        FROM collect.query_store_data AS qsd
        WHERE qsd.collection_id = @collection_id;";

            using var command = new SqlCommand(query, connection);
            command.CommandTimeout = 120;
            command.Parameters.Add(new SqlParameter("@collection_id", SqlDbType.BigInt) { Value = collectionId });

            var result = await command.ExecuteScalarAsync();
            return result == DBNull.Value || result == null ? null : (string)result;
        }

        /// <summary>
        /// Fetches query plan XML on demand for a single procedure_stats row.
        /// </summary>
        public async Task<string?> GetProcedureStatsPlanXmlByCollectionIdAsync(long collectionId)
        {
            await using var tc = await OpenThrottledConnectionAsync();
            var connection = tc.Connection;

            string query = @"
        SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;

        SELECT
            CAST(DECOMPRESS(ps.query_plan_text) AS nvarchar(max)) AS query_plan_text
        FROM collect.procedure_stats AS ps
        WHERE ps.collection_id = @collection_id;";

            using var command = new SqlCommand(query, connection);
            command.CommandTimeout = 120;
            command.Parameters.Add(new SqlParameter("@collection_id", SqlDbType.BigInt) { Value = collectionId });

            var result = await command.ExecuteScalarAsync();
            return result == DBNull.Value || result == null ? null : (string)result;
        }

        /// <summary>
        /// Fetches query plan XML on demand for a single query_stats row.
        /// </summary>
        public async Task<string?> GetQueryStatsPlanXmlByCollectionIdAsync(long collectionId)
        {
            await using var tc = await OpenThrottledConnectionAsync();
            var connection = tc.Connection;

            string query = @"
        SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;

        SELECT
            CAST(DECOMPRESS(qs.query_plan_text) AS nvarchar(max)) AS query_plan_text
        FROM collect.query_stats AS qs
        WHERE qs.collection_id = @collection_id;";

            using var command = new SqlCommand(query, connection);
            command.CommandTimeout = 120;
            command.Parameters.Add(new SqlParameter("@collection_id", SqlDbType.BigInt) { Value = collectionId });

            var result = await command.ExecuteScalarAsync();
            return result == DBNull.Value || result == null ? null : (string)result;
        }

        /// <summary>
        /// Fetches the most recent plan XML for a query identified by query_hash.
        /// Used by MCP plan analysis tools.
        /// </summary>
        public async Task<string?> GetPlanXmlByQueryHashAsync(string queryHash)
        {
            await using var tc = await OpenThrottledConnectionAsync();
            var connection = tc.Connection;

            string query = @"
        SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;

        SELECT TOP (1)
            CAST(DECOMPRESS(qs.query_plan_text) AS nvarchar(max))
        FROM collect.query_stats AS qs
        WHERE qs.query_hash = CONVERT(binary(8), @queryHash, 1)
        ORDER BY qs.last_execution_time DESC;";

            using var command = new SqlCommand(query, connection);
            command.CommandTimeout = 120;
            command.Parameters.Add(new SqlParameter("@queryHash", SqlDbType.NVarChar, 20) { Value = queryHash });

            var result = await command.ExecuteScalarAsync();
            return result == DBNull.Value || result == null ? null : (string)result;
        }

        /// <summary>
        /// Fetches the most recent plan XML for a procedure identified by sql_handle.
        /// Used by MCP plan analysis tools.
        /// </summary>
        public async Task<string?> GetProcedurePlanXmlBySqlHandleAsync(string sqlHandle)
        {
            await using var tc = await OpenThrottledConnectionAsync();
            var connection = tc.Connection;

            string query = @"
        SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;

        SELECT TOP (1)
            CAST(DECOMPRESS(ps.query_plan_text) AS nvarchar(max))
        FROM collect.procedure_stats AS ps
        WHERE ps.sql_handle = CONVERT(varbinary(64), @sqlHandle, 1)
        ORDER BY ps.last_execution_time DESC;";

            using var command = new SqlCommand(query, connection);
            command.CommandTimeout = 120;
            command.Parameters.Add(new SqlParameter("@sqlHandle", SqlDbType.NVarChar, 130) { Value = sqlHandle });

            var result = await command.ExecuteScalarAsync();
            return result == DBNull.Value || result == null ? null : (string)result;
        }
    }
}
