/*
 * Performance Monitor Dashboard
 * Copyright (c) 2026 Darling Data, LLC
 * Licensed under the MIT License - see LICENSE file for details
 */

using System.Collections.Generic;
using System.Windows;
using PerformanceMonitor.Notifications;
using PerformanceMonitorDashboard.Controls;
using PerformanceMonitorDashboard.Services;

namespace PerformanceMonitorDashboard
{
    public partial class AlertDetailWindow : Window
    {
        public AlertDetailWindow(AlertHistoryDisplayItem item)
        {
            InitializeComponent();

            TimeText.Text = item.TimeLocal;
            ServerText.Text = item.ServerName;
            MetricText.Text = item.MetricName;
            CurrentValueText.Text = item.CurrentValue;
            ThresholdText.Text = item.ThresholdValue;
            NotificationText.Text = item.NotificationType;
            StatusText.Text = item.StatusDisplay;

            if (item.Muted)
                MutedBanner.Visibility = Visibility.Visible;

            /* Prefer the structured context (advice / remediation T-SQL / drill-down) persisted as
               ContextJson; fall back to the flat detail text for older rows that have no context. */
            if (TryBuildDetailViews(item.ContextJson, out var views))
            {
                DetailItems.ItemsSource = views;
                DetailItemsScroll.Visibility = Visibility.Visible;
                DetailPanel.Visibility = Visibility.Visible;
            }
            else if (!string.IsNullOrWhiteSpace(item.DetailText))
            {
                DetailTextBox.Text = item.DetailText;
                DetailTextBox.Visibility = Visibility.Visible;
                DetailPanel.Visibility = Visibility.Visible;
            }
        }

        /* WPF cannot data-bind to AlertDetailItem.Fields (a List<(string,string)> ValueTuple — its
           Item1/Item2 are fields, not properties), so the deserialized context is projected into
           bindable view-models the DataTemplate can render. */
        private static bool TryBuildDetailViews(string? contextJson, out List<DetailItemView> views)
        {
            views = new List<DetailItemView>();
            if (!AlertContextSerializer.TryDeserialize(contextJson, out var context))
                return false;

            foreach (var detail in context.Details)
            {
                var view = new DetailItemView
                {
                    Heading = detail.Heading,
                    Body = detail.Body,
                    IsCodeBlock = detail.IsCodeBlock
                };
                foreach (var (label, value) in detail.Fields)
                    view.Fields.Add(new FieldView { Label = label, Value = value });
                views.Add(view);
            }

            return views.Count > 0;
        }

        private void CopyTsqlButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { DataContext: DetailItemView view } && !string.IsNullOrEmpty(view.Body))
            {
                try
                {
                    Clipboard.SetText(view.Body);
                }
                catch
                {
                    /* Clipboard can be locked by another process — swallow contention. */
                }
            }
        }

        public sealed class DetailItemView
        {
            public string Heading { get; set; } = "";
            public string? Body { get; set; }
            public bool IsCodeBlock { get; set; }
            public List<FieldView> Fields { get; } = new();

            public bool IsProse => !IsCodeBlock && !string.IsNullOrEmpty(Body);
            public bool HasFields => Fields.Count > 0;
        }

        public sealed class FieldView
        {
            public string Label { get; set; } = "";
            public string Value { get; set; } = "";
        }
    }
}
