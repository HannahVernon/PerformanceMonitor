/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor Lite.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

/* Phase-5 A0: the six alert row types moved to the shared PerformanceMonitor.Alerting library
   (one canonical copy instead of member-identical Lite/Dashboard twins). These aliases keep every
   existing call site compiling unchanged against the shared types. */
global using AnomalousJobInfo = PerformanceMonitor.Alerting.AnomalousJobInfo;
global using FailedJobInfo = PerformanceMonitor.Alerting.FailedJobInfo;
global using LongRunningQueryInfo = PerformanceMonitor.Alerting.LongRunningQueryInfo;
global using PoisonWaitDelta = PerformanceMonitor.Alerting.PoisonWaitDelta;
global using TempDbSpaceInfo = PerformanceMonitor.Alerting.TempDbSpaceInfo;
global using VolumeFreeSpaceInfo = PerformanceMonitor.Alerting.VolumeFreeSpaceInfo;
