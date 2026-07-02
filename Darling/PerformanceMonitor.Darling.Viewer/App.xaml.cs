/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System.Windows;

namespace PerformanceMonitor.Darling.Viewer;

/// <summary>
/// The Darling viewer — a Postgres client of the central store. MainWindow owns startup:
/// it loads the viewer's sliver of darling.json (<see cref="ViewerSettings"/>) and connects
/// on first render. Theming and single-instance plumbing come in a later milestone.
/// </summary>
public partial class App : Application
{
}
