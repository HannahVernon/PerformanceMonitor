/*
 * Performance Monitor Dashboard
 * Copyright (c) 2026 Darling Data, LLC
 * Licensed under the MIT License - see LICENSE file for details
 */

using System.Collections.Generic;

namespace PerformanceMonitorDashboard.Services
{
    /// <summary>
    /// Optional detail context attached to alert emails.
    /// Populated from blocking/deadlock detail queries at alert time.
    /// </summary>
    public class AlertContext
    {
        public List<AlertDetailItem> Details { get; set; } = new();
        public string? AttachmentXml { get; set; }
        public string? AttachmentFileName { get; set; }
    }

    /// <summary>
    /// A single detail item (e.g., one blocking chain or one deadlock participant).
    /// </summary>
    public class AlertDetailItem
    {
        public string Heading { get; set; } = "";
        public List<(string Label, string Value)> Fields { get; set; } = new();
    }
}
