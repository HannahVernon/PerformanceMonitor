/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

namespace PerformanceMonitor.Ui;

/// <summary>
/// One row of a picker's checkbox list: a display name and whether it is checked. Mutable -- the checkbox
/// two-way-binds <see cref="IsSelected"/>, and the checked-to-top reorder rewrites the list. Shared by Lite's
/// and Darling's wait-type / perfmon / memory-clerk / database pickers. (The deprecated Full Dashboard keeps
/// its own copy in Dashboard/Models/SelectableItem.cs and is not developed further.)
/// </summary>
public sealed class SelectableItem
{
    public string DisplayName { get; set; } = "";
    public bool IsSelected { get; set; }
}
