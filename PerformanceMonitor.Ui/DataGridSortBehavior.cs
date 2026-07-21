/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace PerformanceMonitor.Ui;

/// <summary>
/// Attached behavior that makes the FIRST click on a DataGrid column header sort
/// DESCENDING instead of the WPF default (ascending). When diagnosing performance you
/// almost always want the largest values — highest CPU / duration / reads — on top
/// first; clicking the same column again toggles to ascending as usual. Applied app-wide
/// via the implicit DataGrid style in the theme dictionaries.
/// </summary>
public static class DataGridSortBehavior
{
    public static readonly DependencyProperty SortDescendingFirstProperty =
        DependencyProperty.RegisterAttached(
            "SortDescendingFirst",
            typeof(bool),
            typeof(DataGridSortBehavior),
            new PropertyMetadata(false, OnSortDescendingFirstChanged));

    public static bool GetSortDescendingFirst(DependencyObject obj) => (bool)obj.GetValue(SortDescendingFirstProperty);
    public static void SetSortDescendingFirst(DependencyObject obj, bool value) => obj.SetValue(SortDescendingFirstProperty, value);

    private static void OnSortDescendingFirstChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not DataGrid grid)
        {
            return;
        }

        /* Idempotent — detach first so re-applying the style doesn't double-subscribe. */
        grid.Sorting -= OnSorting;
        if ((bool)e.NewValue)
        {
            grid.Sorting += OnSorting;
        }
    }

    private static void OnSorting(object? sender, DataGridSortingEventArgs e)
    {
        if (sender is not DataGrid grid)
        {
            return;
        }

        var sortPath = e.Column.SortMemberPath;
        if (string.IsNullOrEmpty(sortPath))
        {
            /* Template column with no SortMemberPath — nothing to sort on; leave the default alone. */
            return;
        }

        var view = CollectionViewSource.GetDefaultView(grid.ItemsSource);
        if (view == null)
        {
            return;
        }

        /* First click on an unsorted column starts DESCENDING; subsequent clicks on the same
           column toggle. (Default WPF is Ascending first, which is backwards for perf triage.) */
        var direction = e.Column.SortDirection == ListSortDirection.Descending
            ? ListSortDirection.Ascending
            : ListSortDirection.Descending;

        foreach (var column in grid.Columns)
        {
            column.SortDirection = column == e.Column ? direction : null;
        }

        view.SortDescriptions.Clear();
        view.SortDescriptions.Add(new SortDescription(sortPath, direction));
        e.Handled = true;
    }
}
