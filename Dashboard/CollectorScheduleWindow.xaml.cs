/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows;
using PerformanceMonitorDashboard.Models;
using PerformanceMonitorDashboard.Services;

namespace PerformanceMonitorDashboard
{
    public partial class CollectorScheduleWindow : Window
    {
        private readonly DatabaseService _databaseService;
        private List<CollectorScheduleItem>? _schedules;

        public CollectorScheduleWindow(DatabaseService databaseService)
        {
            InitializeComponent();
            _databaseService = databaseService;
            Loaded += CollectorScheduleWindow_Loaded;
            Closing += CollectorScheduleWindow_Closing;
        }

        private void CollectorScheduleWindow_Closing(object? sender, CancelEventArgs e)
        {
            /* Unsubscribe from property change events to prevent memory leaks */
            if (_schedules != null)
            {
                foreach (var schedule in _schedules)
                {
                    schedule.PropertyChanged -= Schedule_PropertyChanged;
                }
            }
        }

        private async void CollectorScheduleWindow_Loaded(object sender, RoutedEventArgs e)
        {
            await LoadSchedulesAsync();
        }

        private async System.Threading.Tasks.Task LoadSchedulesAsync()
        {
            try
            {
                _schedules = await _databaseService.GetCollectorSchedulesAsync();

                // Subscribe to property changes for auto-save
                foreach (var schedule in _schedules)
                {
                    schedule.PropertyChanged += Schedule_PropertyChanged;
                }

                ScheduleDataGrid.ItemsSource = _schedules;
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Failed to load collector schedules:\n\n{ex.Message}",
                    "Error Loading Schedules",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error
                );
            }
        }

        private async void Schedule_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (sender is CollectorScheduleItem schedule)
            {
                // Only save for the editable properties
                if (e.PropertyName == nameof(CollectorScheduleItem.Enabled) ||
                    e.PropertyName == nameof(CollectorScheduleItem.FrequencyMinutes) ||
                    e.PropertyName == nameof(CollectorScheduleItem.RetentionDays))
                {
                    try
                    {
                        await _databaseService.UpdateCollectorScheduleAsync(
                            schedule.ScheduleId,
                            schedule.Enabled,
                            schedule.FrequencyMinutes,
                            schedule.RetentionDays
                        );
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(
                            $"Failed to save changes:\n\n{ex.Message}",
                            "Error Saving Schedule",
                            MessageBoxButton.OK,
                            MessageBoxImage.Error
                        );
                    }
                }
            }
        }

        private async void Refresh_Click(object sender, RoutedEventArgs e)
        {
            // Unsubscribe from old items
            if (_schedules != null)
            {
                foreach (var schedule in _schedules)
                {
                    schedule.PropertyChanged -= Schedule_PropertyChanged;
                }
            }

            await LoadSchedulesAsync();
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
