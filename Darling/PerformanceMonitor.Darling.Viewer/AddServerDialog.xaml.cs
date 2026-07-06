/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Globalization;
using System.Windows;
using PerformanceMonitor.Common;
using PerformanceMonitor.Notifications;

namespace PerformanceMonitor.Darling.Viewer;

/// <summary>
/// The viewer's Add / Edit server dialog — a faithful port of Lite's <c>AddServerDialog</c>, with the two
/// live-connection affordances dropped as hard viewer facts: there is no <b>Test Connection</b> button (the
/// viewer never opens a SqlClient connection to a monitored server — it reads only the Postgres store) and
/// no shared credential-profile picker (the viewer has no <c>ProfileManager</c>). Everything else — all five
/// inline auth modes, encryption, connection options, database / utility DB, cost, alert-delivery override,
/// favorite — is captured verbatim into a <see cref="ViewerServerEntry"/> and persisted through
/// <see cref="ViewerServerStore"/> (secrets to Credential Manager). Making the running service honor the
/// saved definition is the separate wiring flagged in the PR.
/// </summary>
public partial class AddServerDialog : Window
{
    private readonly ViewerServerStore _serverStore;

    /// <summary>The server that was added or edited, or null when the dialog was cancelled.</summary>
    public ViewerServerEntry? AddedServer { get; private set; }

    public AddServerDialog(ViewerServerStore serverStore)
    {
        InitializeComponent();
        _serverStore = serverStore;
    }

    /// <summary>Constructor for editing an existing server definition.</summary>
    public AddServerDialog(ViewerServerStore serverStore, ViewerServerEntry existing)
        : this(serverStore)
    {
        Title = "Edit SQL Server";
        ServerNameBox.Text = existing.ServerName;
        DisplayNameBox.Text = existing.DisplayName;
        EnabledCheckBox.IsChecked = existing.IsEnabled;
        TrustCertCheckBox.IsChecked = existing.TrustServerCertificate;

        EncryptModeComboBox.SelectedIndex = existing.EncryptMode switch
        {
            "Mandatory" => 1,
            "Strict" => 2,
            _ => 0
        };

        FavoriteCheckBox.IsChecked = existing.IsFavorite;
        DescriptionTextBox.Text = existing.Description ?? "";
        DatabaseNameBox.Text = existing.DatabaseName ?? "";
        UtilityDatabaseBox.Text = existing.UtilityDatabase ?? "";
        ReadOnlyIntentCheckBox.IsChecked = existing.ReadOnlyIntent;
        MultiSubnetFailoverCheckBox.IsChecked = existing.MultiSubnetFailover;
        MonthlyCostBox.Text = existing.MonthlyCostUsd.ToString(CultureInfo.InvariantCulture);
        AlertDeliveryOverrideBox.SelectedIndex = existing.AlertDeliveryModeOverride switch
        {
            AlertNotificationMode.Summary => 1,
            AlertNotificationMode.PerEvent => 2,
            _ => 0
        };

        if (existing.AuthenticationType == AuthenticationTypes.EntraMFA)
        {
            EntraMfaAuthRadio.IsChecked = true;
            EntraMfaUsernameBox.Text = existing.EntraUsername ?? "";
        }
        else if (existing.AuthenticationType == AuthenticationTypes.SqlServer)
        {
            SqlAuthRadio.IsChecked = true;
            var cred = _serverStore.GetCredential(existing.Id);
            if (cred.HasValue)
            {
                UsernameBox.Text = cred.Value.Username;
                PasswordBox.Password = cred.Value.Password;
            }
        }
        else if (existing.AuthenticationType == AuthenticationTypes.ServicePrincipal)
        {
            ServicePrincipalAuthRadio.IsChecked = true;
            AzureClientIdBox.Text = existing.AzureClientId ?? "";
            AzureTenantIdBox.Text = existing.AzureTenantId ?? "";
            var cred = _serverStore.GetCredential(existing.Id);
            if (cred.HasValue)
            {
                if (string.IsNullOrEmpty(AzureClientIdBox.Text))
                {
                    AzureClientIdBox.Text = cred.Value.Username;
                }
                AzureClientSecretBox.Password = cred.Value.Password;
            }
        }
        else if (existing.AuthenticationType == AuthenticationTypes.ManagedIdentity)
        {
            ManagedIdentityAuthRadio.IsChecked = true;
            ManagedIdentityClientIdBox.Text = existing.ManagedIdentityClientId ?? "";
        }
        else
        {
            WindowsAuthRadio.IsChecked = true;
        }

        AddedServer = existing;
    }

    private void AuthMode_Changed(object sender, RoutedEventArgs e)
    {
        if (SqlCredentialsPanel is null || EntraMfaPanel is null ||
            ServicePrincipalPanel is null || ManagedIdentityPanel is null)
        {
            return;
        }

        SqlCredentialsPanel.Visibility = SqlAuthRadio.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        EntraMfaPanel.Visibility = EntraMfaAuthRadio.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        ServicePrincipalPanel.Visibility = ServicePrincipalAuthRadio.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        ManagedIdentityPanel.Visibility = ManagedIdentityAuthRadio.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private AlertNotificationMode? GetSelectedDeliveryOverride() => AlertDeliveryOverrideBox.SelectedIndex switch
    {
        1 => AlertNotificationMode.Summary,
        2 => AlertNotificationMode.PerEvent,
        _ => null
    };

    private string GetSelectedEncryptMode() => EncryptModeComboBox.SelectedIndex switch
    {
        1 => "Mandatory",
        2 => "Strict",
        _ => "Optional"
    };

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        var serverName = ServerNameBox.Text.Trim();
        if (string.IsNullOrEmpty(serverName))
        {
            StatusText.Text = "Server name is required.";
            return;
        }

        var displayName = DisplayNameBox.Text.Trim();
        if (string.IsNullOrEmpty(displayName))
        {
            displayName = serverName;
        }

        string authenticationType;
        string? username = null;
        string? password = null;
        string? entraUsername = null;

        if (WindowsAuthRadio.IsChecked == true)
        {
            authenticationType = AuthenticationTypes.Windows;
        }
        else if (EntraMfaAuthRadio.IsChecked == true)
        {
            authenticationType = AuthenticationTypes.EntraMFA;
            entraUsername = EntraMfaUsernameBox.Text.Trim();
        }
        else if (ServicePrincipalAuthRadio.IsChecked == true)
        {
            authenticationType = AuthenticationTypes.ServicePrincipal;
            username = AzureClientIdBox.Text.Trim();
            password = AzureClientSecretBox.Password;

            if (string.IsNullOrEmpty(username))
            {
                StatusText.Text = "Client (Application) ID is required for service principal authentication.";
                return;
            }
            if (string.IsNullOrEmpty(password))
            {
                StatusText.Text = "Client secret is required for service principal authentication.";
                return;
            }
        }
        else if (ManagedIdentityAuthRadio.IsChecked == true)
        {
            authenticationType = AuthenticationTypes.ManagedIdentity;
        }
        else if (SqlAuthRadio.IsChecked == true)
        {
            authenticationType = AuthenticationTypes.SqlServer;
            username = UsernameBox.Text.Trim();
            password = PasswordBox.Password;

            if (string.IsNullOrEmpty(username))
            {
                StatusText.Text = "Username is required for SQL Server authentication.";
                return;
            }
        }
        else
        {
            authenticationType = AuthenticationTypes.Windows;
        }

        decimal monthlyCost = 0m;
        if (decimal.TryParse(MonthlyCostBox.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsedCost) && parsedCost >= 0)
        {
            monthlyCost = parsedCost;
        }

        try
        {
            if (AddedServer is not null && Title == "Edit SQL Server")
            {
                /* Editing an existing definition — mutate in place and re-persist. */
                var entry = AddedServer;
                entry.ServerName = serverName;
                entry.DisplayName = displayName;
                entry.AuthenticationType = authenticationType;
                entry.EntraUsername = authenticationType == AuthenticationTypes.EntraMFA
                    ? (string.IsNullOrWhiteSpace(entraUsername) ? null : entraUsername)
                    : null;
                entry.AzureClientId = authenticationType == AuthenticationTypes.ServicePrincipal
                    ? (string.IsNullOrWhiteSpace(AzureClientIdBox.Text) ? null : AzureClientIdBox.Text.Trim())
                    : null;
                entry.AzureTenantId = authenticationType == AuthenticationTypes.ServicePrincipal
                    ? (string.IsNullOrWhiteSpace(AzureTenantIdBox.Text) ? null : AzureTenantIdBox.Text.Trim())
                    : null;
                entry.ManagedIdentityClientId = authenticationType == AuthenticationTypes.ManagedIdentity
                    ? (string.IsNullOrWhiteSpace(ManagedIdentityClientIdBox.Text) ? null : ManagedIdentityClientIdBox.Text.Trim())
                    : null;
                entry.IsEnabled = EnabledCheckBox.IsChecked == true;
                entry.TrustServerCertificate = TrustCertCheckBox.IsChecked == true;
                entry.EncryptMode = GetSelectedEncryptMode();
                entry.IsFavorite = FavoriteCheckBox.IsChecked == true;
                entry.Description = DescriptionTextBox.Text.Trim();
                entry.DatabaseName = string.IsNullOrWhiteSpace(DatabaseNameBox.Text) ? null : DatabaseNameBox.Text.Trim();
                entry.UtilityDatabase = string.IsNullOrWhiteSpace(UtilityDatabaseBox.Text) ? null : UtilityDatabaseBox.Text.Trim();
                entry.ReadOnlyIntent = ReadOnlyIntentCheckBox.IsChecked == true;
                entry.MultiSubnetFailover = MultiSubnetFailoverCheckBox.IsChecked == true;
                entry.MonthlyCostUsd = monthlyCost;
                entry.AlertDeliveryModeOverride = GetSelectedDeliveryOverride();

                _serverStore.UpdateServer(entry, username, password);
            }
            else
            {
                /* Adding a new definition. */
                AddedServer = new ViewerServerEntry
                {
                    ServerName = serverName,
                    DisplayName = displayName,
                    AuthenticationType = authenticationType,
                    EntraUsername = authenticationType == AuthenticationTypes.EntraMFA
                        ? (string.IsNullOrWhiteSpace(entraUsername) ? null : entraUsername)
                        : null,
                    AzureClientId = authenticationType == AuthenticationTypes.ServicePrincipal
                        ? (string.IsNullOrWhiteSpace(AzureClientIdBox.Text) ? null : AzureClientIdBox.Text.Trim())
                        : null,
                    AzureTenantId = authenticationType == AuthenticationTypes.ServicePrincipal
                        ? (string.IsNullOrWhiteSpace(AzureTenantIdBox.Text) ? null : AzureTenantIdBox.Text.Trim())
                        : null,
                    ManagedIdentityClientId = authenticationType == AuthenticationTypes.ManagedIdentity
                        ? (string.IsNullOrWhiteSpace(ManagedIdentityClientIdBox.Text) ? null : ManagedIdentityClientIdBox.Text.Trim())
                        : null,
                    IsEnabled = EnabledCheckBox.IsChecked == true,
                    TrustServerCertificate = TrustCertCheckBox.IsChecked == true,
                    EncryptMode = GetSelectedEncryptMode(),
                    IsFavorite = FavoriteCheckBox.IsChecked == true,
                    Description = DescriptionTextBox.Text.Trim(),
                    DatabaseName = string.IsNullOrWhiteSpace(DatabaseNameBox.Text) ? null : DatabaseNameBox.Text.Trim(),
                    UtilityDatabase = string.IsNullOrWhiteSpace(UtilityDatabaseBox.Text) ? null : UtilityDatabaseBox.Text.Trim(),
                    ReadOnlyIntent = ReadOnlyIntentCheckBox.IsChecked == true,
                    MultiSubnetFailover = MultiSubnetFailoverCheckBox.IsChecked == true,
                    MonthlyCostUsd = monthlyCost,
                    AlertDeliveryModeOverride = GetSelectedDeliveryOverride()
                };

                _serverStore.AddServer(AddedServer, username, password);
            }

            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Error: {ex.Message}";
            ViewerLogger.Error("AddServerDialog", "Failed to save server definition", ex);
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
