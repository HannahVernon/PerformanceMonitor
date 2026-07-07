/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using PerformanceMonitor.Common;
using PerformanceMonitor.Notifications;

namespace PerformanceMonitor.Darling.Viewer;

/// <summary>
/// The viewer's Add / Edit server dialog — a faithful port of Lite's <c>AddServerDialog</c>, with the one
/// live-connection affordance dropped as a hard viewer fact: there is no <b>Test Connection</b> button (the
/// viewer never opens a SqlClient connection to a monitored server — it reads only the Postgres store).
/// Everything else — all five inline auth modes AND the shared credential-profile picker (credential
/// MANAGEMENT is fully in scope; only live connection resolution is the service's job), encryption,
/// connection options, database / utility DB, cost, alert-delivery override, favorite — is captured into a
/// <see cref="ViewerServerEntry"/> and persisted through <see cref="ViewerServerStore"/> (per-server
/// secrets to Credential Manager; a chosen profile via <see cref="ViewerServerEntry.CredentialProfileId"/>).
/// Making the running service honor the saved definition is the separate wiring flagged in the PR.
/// </summary>
public partial class AddServerDialog : Window
{
    private readonly ViewerServerStore _serverStore;
    private readonly ViewerProfileStore _profileStore;

    /// <summary>The server that was added or edited, or null when the dialog was cancelled.</summary>
    public ViewerServerEntry? AddedServer { get; private set; }

    public AddServerDialog(ViewerServerStore serverStore, ViewerProfileStore profileStore)
    {
        InitializeComponent();
        _serverStore = serverStore;
        _profileStore = profileStore;
        PopulateProfilePicker();
    }

    /// <summary>
    /// Populates the profile dropdown from the current profile list. Disables the "use profile" option
    /// when no profiles exist.
    /// </summary>
    private void PopulateProfilePicker()
    {
        var profiles = _profileStore.GetAll();
        ProfileComboBox.ItemsSource = profiles;
        if (profiles.Count == 0)
        {
            UseProfileRadio.IsEnabled = false;
            NoProfilesNote.Visibility = Visibility.Visible;
        }
    }

    /// <summary>Constructor for editing an existing server definition.</summary>
    public AddServerDialog(ViewerServerStore serverStore, ViewerProfileStore profileStore, ViewerServerEntry existing)
        : this(serverStore, profileStore)
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

        /* Profile-backed server: preselect "use profile" + the dropdown; the inline auth panels stay
           hidden (CredentialSource_Changed, fired by IsChecked). No per-server secret is loaded. */
        if (!string.IsNullOrEmpty(existing.CredentialProfileId))
        {
            UseProfileRadio.IsChecked = true;
            var match = _profileStore.GetProfile(existing.CredentialProfileId);
            if (match is not null)
            {
                ProfileComboBox.SelectedItem = ProfileComboBox.Items
                    .Cast<ViewerCredentialProfile>().FirstOrDefault(p => p.Id == match.Id);
            }

            AddedServer = existing;
            return;
        }

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

    /// <summary>
    /// Toggles between the inline per-server auth UI and the credential-profile picker. When a profile is
    /// chosen the inline auth radios + all per-mode credential panels are hidden (the profile fully
    /// overrides the server's auth type + creds).
    /// </summary>
    private void CredentialSource_Changed(object sender, RoutedEventArgs e)
    {
        /* Guard against early Checked events during InitializeComponent. */
        if (ProfilePickerPanel is null || InlineAuthRadios is null)
        {
            return;
        }

        var useProfile = UseProfileRadio.IsChecked == true;

        ProfilePickerPanel.Visibility = useProfile ? Visibility.Visible : Visibility.Collapsed;
        InlineAuthRadios.Visibility = useProfile ? Visibility.Collapsed : Visibility.Visible;

        if (useProfile)
        {
            /* Hide every per-mode inline credential panel. */
            if (SqlCredentialsPanel is not null) SqlCredentialsPanel.Visibility = Visibility.Collapsed;
            if (EntraMfaPanel is not null) EntraMfaPanel.Visibility = Visibility.Collapsed;
            if (ServicePrincipalPanel is not null) ServicePrincipalPanel.Visibility = Visibility.Collapsed;
            if (ManagedIdentityPanel is not null) ManagedIdentityPanel.Visibility = Visibility.Collapsed;
        }
        else
        {
            /* Re-show the panel for the currently selected inline auth mode. */
            AuthMode_Changed(sender, e);
        }
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

        /* Credential source: a shared profile, or inline per-server auth. */
        var useProfile = UseProfileRadio.IsChecked == true;
        ViewerCredentialProfile? selectedProfile = null;

        string authenticationType;
        string? username = null;
        string? password = null;
        string? entraUsername = null;

        if (useProfile)
        {
            selectedProfile = ProfileComboBox.SelectedItem as ViewerCredentialProfile;
            if (selectedProfile is null)
            {
                StatusText.Text = "Select a credential profile, or choose \"Configure credentials on this server\".";
                return;
            }
            /* The profile fully overrides the server's auth; mirror its auth type onto the entry (display
               only). Resolution always comes from the profile via CredentialProfileId; no per-server
               secret is stored. */
            authenticationType = selectedProfile.AuthType;
        }
        else if (WindowsAuthRadio.IsChecked == true)
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
                entry.CredentialProfileId = useProfile ? selectedProfile!.Id : null;
                entry.EntraUsername = (!useProfile && authenticationType == AuthenticationTypes.EntraMFA)
                    ? (string.IsNullOrWhiteSpace(entraUsername) ? null : entraUsername)
                    : null;
                entry.AzureClientId = (!useProfile && authenticationType == AuthenticationTypes.ServicePrincipal)
                    ? (string.IsNullOrWhiteSpace(AzureClientIdBox.Text) ? null : AzureClientIdBox.Text.Trim())
                    : null;
                entry.AzureTenantId = (!useProfile && authenticationType == AuthenticationTypes.ServicePrincipal)
                    ? (string.IsNullOrWhiteSpace(AzureTenantIdBox.Text) ? null : AzureTenantIdBox.Text.Trim())
                    : null;
                entry.ManagedIdentityClientId = (!useProfile && authenticationType == AuthenticationTypes.ManagedIdentity)
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

                /* Profile-backed → store no per-server secret (UpdateServer with null creds deletes any
                   stale one, matching Lite's switch-cleanup). */
                _serverStore.UpdateServer(entry, useProfile ? null : username, useProfile ? null : password);
            }
            else
            {
                /* Adding a new definition. */
                AddedServer = new ViewerServerEntry
                {
                    ServerName = serverName,
                    DisplayName = displayName,
                    AuthenticationType = authenticationType,
                    CredentialProfileId = useProfile ? selectedProfile!.Id : null,
                    EntraUsername = (!useProfile && authenticationType == AuthenticationTypes.EntraMFA)
                        ? (string.IsNullOrWhiteSpace(entraUsername) ? null : entraUsername)
                        : null,
                    AzureClientId = (!useProfile && authenticationType == AuthenticationTypes.ServicePrincipal)
                        ? (string.IsNullOrWhiteSpace(AzureClientIdBox.Text) ? null : AzureClientIdBox.Text.Trim())
                        : null,
                    AzureTenantId = (!useProfile && authenticationType == AuthenticationTypes.ServicePrincipal)
                        ? (string.IsNullOrWhiteSpace(AzureTenantIdBox.Text) ? null : AzureTenantIdBox.Text.Trim())
                        : null,
                    ManagedIdentityClientId = (!useProfile && authenticationType == AuthenticationTypes.ManagedIdentity)
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

                _serverStore.AddServer(AddedServer, useProfile ? null : username, useProfile ? null : password);
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
