/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;

namespace PerformanceMonitorDashboard.Models
{
    public class BlockingEventItem
    {
        public long BlockingId { get; set; }
        public DateTime CollectionTime { get; set; }
        public string BlockedProcessReport { get; set; } = string.Empty;
        public DateTime? EventTime { get; set; }
        public string DatabaseName { get; set; } = string.Empty;
        public string CurrentDbName { get; set; } = string.Empty;
        public string ContentiousObject { get; set; } = string.Empty;
        public string Activity { get; set; } = string.Empty;
        public string BlockingTree { get; set; } = string.Empty;
        public int? Spid { get; set; }
        public int? Ecid { get; set; }
        public string QueryText { get; set; } = string.Empty;
        public long? WaitTimeMs { get; set; }
        public string Status { get; set; } = string.Empty;
        public string IsolationLevel { get; set; } = string.Empty;
        public string LockMode { get; set; } = string.Empty;
        public string ResourceOwnerType { get; set; } = string.Empty;
        public int? TransactionCount { get; set; }
        public string TransactionName { get; set; } = string.Empty;
        public DateTime? LastTransactionStarted { get; set; }
        public DateTime? LastTransactionCompleted { get; set; }
        public string ClientOption1 { get; set; } = string.Empty;
        public string ClientOption2 { get; set; } = string.Empty;
        public string WaitResource { get; set; } = string.Empty;
        public int? Priority { get; set; }
        public long? LogUsed { get; set; }
        public string ClientApp { get; set; } = string.Empty;
        public string HostName { get; set; } = string.Empty;
        public string LoginName { get; set; } = string.Empty;
        public long? TransactionId { get; set; }
        public string BlockedProcessReportXml { get; set; } = string.Empty;

        public double? WaitTimeSec => WaitTimeMs.HasValue ? WaitTimeMs.Value / 1000.0 : null;
    }
}
