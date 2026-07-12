/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Installer.Core;
using Installer.Core.Models;
using Microsoft.Data.SqlClient;
using PerformanceMonitorDashboard.Helpers;
using PerformanceMonitorDashboard.Models;
using PerformanceMonitorDashboard.Services;
using PerformanceMonitor.Common;
using PerformanceMonitor.Notifications;

namespace PerformanceMonitorDashboard
{
    public partial class AddServerDialog : Window
    {
        private enum DialogState
        {
            Initial,
            Connected_NoDatabase,
            Connected_NeedsUpgrade,
            Connected_Current,
            Connected_StatusUnknown,
            Installing,
            InstallComplete,
            MonitoringCredentials
        }

        /*
        Non-null when installing would be unsafe, and why. Three cases, all of which would otherwise
        reinstall over a database whose real state we do not know:
          - discovery threw (a transient error is indistinguishable from "no database"),
          - the installed version is NEWER than this Dashboard (installing would downgrade it),
          - the installed version will not parse at all.
        Guards the install path structurally rather than relying on the button being hidden.
        */
        private string? _installBlockedReason;

        public ServerConnection ServerConnection { get; private set; }
        public string? Username { get; private set; }
        public string? Password { get; private set; }
        private bool _isEditMode;

        private CancellationTokenSource? _installCts;
        private Installer.Core.Models.ServerInfo? _coreServerInfo;
        private string? _installedVersion;
        private InstallationResult? _installResult;
        private string? _reportPath;
        private DialogState _currentState = DialogState.Initial;
        private string? _serverVersion;

        /*
        Cancel the install on ANY close. Cancel_Click covers the Cancel button and ESC (IsCancel), but the
        title-bar X and Alt+F4 bypass it entirely -- without this the install keeps running against the
        database, history write included, with the dialog already destroyed and nothing left to stop it.
        */
        private void CancelInstallOnClose(object? sender, System.ComponentModel.CancelEventArgs e) =>
            _installCts?.Cancel();

        public AddServerDialog()
        {
            InitializeComponent();
            SizeToWorkArea();
            Closing += CancelInstallOnClose;
            _isEditMode = false;
            ServerConnection = new ServerConnection();
            Title = "Add SQL Server";
        }

        public AddServerDialog(ServerConnection existingServer)
        {
            InitializeComponent();
            SizeToWorkArea();
            Closing += CancelInstallOnClose;
            _isEditMode = true;
            ServerConnection = existingServer;
            Title = "Edit SQL Server";

            DisplayNameTextBox.Text = existingServer.DisplayName;
            ServerNameTextBox.Text = existingServer.ServerName;
            DescriptionTextBox.Text = existingServer.Description;
            IsFavoriteCheckBox.IsChecked = existingServer.IsFavorite;
            MonthlyCostTextBox.Text = existingServer.MonthlyCostUsd.ToString(System.Globalization.CultureInfo.InvariantCulture);

            // Load encryption settings
            EncryptModeComboBox.SelectedIndex = existingServer.EncryptMode switch
            {
                "Mandatory" => 1,
                "Strict" => 2,
                _ => 0 // Optional
            };
            TrustServerCertificateCheckBox.IsChecked = existingServer.TrustServerCertificate;
            ReadOnlyIntentCheckBox.IsChecked = existingServer.ReadOnlyIntent;
            MultiSubnetFailoverCheckBox.IsChecked = existingServer.MultiSubnetFailover;
            AlertDeliveryOverrideComboBox.SelectedIndex = existingServer.AlertDeliveryModeOverride switch
            {
                AlertNotificationMode.Summary => 1,
                AlertNotificationMode.PerEvent => 2,
                _ => 0
            };

            if (existingServer.AuthenticationType == AuthenticationTypes.EntraMFA)
            {
                EntraMfaAuthRadio.IsChecked = true;

                var credentialService = new CredentialService();
                var cred = credentialService.GetCredential(existingServer.Id);
                if (cred.HasValue && !string.IsNullOrEmpty(cred.Value.Username))
                {
                    EntraMfaUsernameBox.Text = cred.Value.Username;
                }
            }
            else if (existingServer.AuthenticationType == AuthenticationTypes.SqlServer)
            {
                SqlAuthRadio.IsChecked = true;

                var credentialService = new CredentialService();
                var cred = credentialService.GetCredential(existingServer.Id);
                if (cred.HasValue)
                {
                    UsernameTextBox.Text = cred.Value.Username;
                    PasswordBox.Password = cred.Value.Password;
                }
            }
            else if (existingServer.AuthenticationType == AuthenticationTypes.ServicePrincipal)
            {
                ServicePrincipalAuthRadio.IsChecked = true;
                ServicePrincipalClientIdBox.Text = existingServer.AzureClientId ?? string.Empty;

                // Pre-fill the client id and the client secret from Credential Manager, matching how SQL
                // Server auth pre-fills its password above (and how Lite pre-fills the SP secret). The
                // model's AzureClientId takes precedence for the client id, falling back to the stored
                // credential username. The user can leave the secret untouched to keep it, or overwrite to
                // rotate it — the save path re-persists whatever is in the box.
                var credentialService = new CredentialService();
                var cred = credentialService.GetCredential(existingServer.Id);
                if (cred.HasValue)
                {
                    if (string.IsNullOrEmpty(ServicePrincipalClientIdBox.Text) && !string.IsNullOrEmpty(cred.Value.Username))
                    {
                        ServicePrincipalClientIdBox.Text = cred.Value.Username;
                    }
                    ServicePrincipalSecretBox.Password = cred.Value.Password;
                }
            }
            else if (existingServer.AuthenticationType == AuthenticationTypes.ManagedIdentity)
            {
                ManagedIdentityAuthRadio.IsChecked = true;
                ManagedIdentityClientIdBox.Text = existingServer.ManagedIdentityClientId ?? string.Empty;
            }
            else
            {
                WindowsAuthRadio.IsChecked = true;
            }
        }

        private void SizeToWorkArea()
        {
            var workArea = SystemParameters.WorkArea;
            Height = workArea.Height;
            Top = workArea.Top;
            Left = workArea.Left + (workArea.Width - Width) / 2;
        }

        private void AuthType_Changed(object sender, RoutedEventArgs e)
        {
            if (SqlAuthPanel != null && EntraMfaPanel != null &&
                ServicePrincipalPanel != null && ManagedIdentityPanel != null)
            {
                SqlAuthPanel.Visibility = SqlAuthRadio.IsChecked == true
                    ? System.Windows.Visibility.Visible
                    : System.Windows.Visibility.Collapsed;

                EntraMfaPanel.Visibility = EntraMfaAuthRadio.IsChecked == true
                    ? System.Windows.Visibility.Visible
                    : System.Windows.Visibility.Collapsed;

                ServicePrincipalPanel.Visibility = ServicePrincipalAuthRadio.IsChecked == true
                    ? System.Windows.Visibility.Visible
                    : System.Windows.Visibility.Collapsed;

                ManagedIdentityPanel.Visibility = ManagedIdentityAuthRadio.IsChecked == true
                    ? System.Windows.Visibility.Visible
                    : System.Windows.Visibility.Collapsed;
            }
        }

        private AlertNotificationMode? GetSelectedDeliveryOverride() => AlertDeliveryOverrideComboBox.SelectedIndex switch
        {
            1 => AlertNotificationMode.Summary,
            2 => AlertNotificationMode.PerEvent,
            _ => null
        };

        private string GetSelectedEncryptMode()
        {
            return EncryptModeComboBox.SelectedIndex switch
            {
                1 => "Mandatory",
                2 => "Strict",
                _ => "Optional"
            };
        }

        private static SqlConnectionEncryptOption ParseEncryptOption(string mode)
        {
            return mode switch
            {
                "Mandatory" => SqlConnectionEncryptOption.Mandatory,
                "Strict" => SqlConnectionEncryptOption.Strict,
                _ => SqlConnectionEncryptOption.Optional
            };
        }

        private SqlConnectionStringBuilder BuildConnectionBuilder()
        {
            var builder = new SqlConnectionStringBuilder
            {
                DataSource = ServerNameTextBox.Text.Trim(),
                InitialCatalog = "PerformanceMonitor",
                ApplicationName = "PerformanceMonitorDashboard",
                ConnectTimeout = 10,
                TrustServerCertificate = TrustServerCertificateCheckBox.IsChecked == true,
                Encrypt = ParseEncryptOption(GetSelectedEncryptMode()),
                ApplicationIntent = ReadOnlyIntentCheckBox.IsChecked == true
                    ? ApplicationIntent.ReadOnly
                    : ApplicationIntent.ReadWrite,
                MultiSubnetFailover = MultiSubnetFailoverCheckBox.IsChecked == true
            };

            // Resolve auth type + per-mode credentials from the live controls, then apply via the SHARED
            // helper so this Test-Connection builder can never diverge from ServerConnection's production
            // builder (the two-bodies trap). See ServerConnection.ApplyAuthentication.
            string authType;
            string? userId = null;
            string? secret = null;
            string? managedIdentityClientId = null;

            if (WindowsAuthRadio.IsChecked == true)
            {
                authType = AuthenticationTypes.Windows;
            }
            else if (SqlAuthRadio.IsChecked == true)
            {
                authType = AuthenticationTypes.SqlServer;
                userId = UsernameTextBox.Text.Trim();
                secret = PasswordBox.Password;
            }
            else if (EntraMfaAuthRadio.IsChecked == true)
            {
                authType = AuthenticationTypes.EntraMFA;
                userId = EntraMfaUsernameBox.Text.Trim();
            }
            else if (ServicePrincipalAuthRadio.IsChecked == true)
            {
                authType = AuthenticationTypes.ServicePrincipal;
                userId = ServicePrincipalClientIdBox.Text.Trim();
                secret = ServicePrincipalSecretBox.Password;
            }
            else if (ManagedIdentityAuthRadio.IsChecked == true)
            {
                authType = AuthenticationTypes.ManagedIdentity;
                managedIdentityClientId = ManagedIdentityClientIdBox.Text.Trim();
            }
            else
            {
                authType = AuthenticationTypes.Windows;
            }

            ServerConnection.ApplyAuthentication(builder, authType, userId, secret, azureClientId: null, managedIdentityClientId);

            return builder;
        }

        private string BuildInstallerConnectionString()
        {
            string server = ServerNameTextBox.Text.Trim();
            bool useWindowsAuth = WindowsAuthRadio.IsChecked == true;
            bool useEntraAuth = EntraMfaAuthRadio.IsChecked == true;
            string? username = null;
            string? password = null;
            string? authenticationType = null;
            string? azureClientId = null;
            string? managedIdentityClientId = null;

            if (SqlAuthRadio.IsChecked == true)
            {
                username = UsernameTextBox.Text.Trim();
                password = PasswordBox.Password;
            }
            else if (useEntraAuth)
            {
                username = EntraMfaUsernameBox.Text.Trim();
            }
            else if (ServicePrincipalAuthRadio.IsChecked == true)
            {
                authenticationType = AuthenticationTypes.ServicePrincipal;
                azureClientId = ServicePrincipalClientIdBox.Text.Trim();
                username = azureClientId;
                password = ServicePrincipalSecretBox.Password;
            }
            else if (ManagedIdentityAuthRadio.IsChecked == true)
            {
                authenticationType = AuthenticationTypes.ManagedIdentity;
                managedIdentityClientId = ManagedIdentityClientIdBox.Text.Trim();
            }

            return InstallationService.BuildConnectionString(
                server,
                useWindowsAuth,
                username,
                password,
                GetSelectedEncryptMode(),
                TrustServerCertificateCheckBox.IsChecked == true,
                useEntraAuth,
                authenticationType,
                azureClientId,
                managedIdentityClientId);
        }

        private static string GetAppVersion()
        {
            var assembly = Assembly.GetExecutingAssembly();
            var infoVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrEmpty(infoVersion))
            {
                /* Strip any +metadata suffix (e.g. "2.4.1+abc123" -> "2.4.1") */
                int plusIndex = infoVersion.IndexOf('+');
                return plusIndex >= 0 ? infoVersion[..plusIndex] : infoVersion;
            }

            var version = assembly.GetName().Version;
            if (version != null)
            {
                /* Normalize 4-part to 3-part: "2.4.1.0" -> "2.4.1" */
                return $"{version.Major}.{version.Minor}.{version.Build}";
            }

            return "0.0.0";
        }

        /// <summary>
        /// Normalize a version string to 3-part for comparison (e.g., "2.4.1.0" -> "2.4.1").
        /// </summary>
        /*
        Reduce a version to its three-part numeric core, stripping any SemVer build (+sha) or
        pre-release (-rc1) suffix. Without the strip, a "3.2.0-rc1" InformationalVersion fails to
        parse, and the install/upgrade routing below then treats EVERY server as up to date. The
        Build clamp matters for the same reason: TryParse("3.1") succeeds with Build == -1, which
        would otherwise format as "3.1.-1" and fail to re-parse. Matches ScriptProvider.ParseVersionCore.
        */
        private static string NormalizeVersion(string version) =>
            ScriptProvider.TryParseVersionCore(version)?.ToString() ?? version;

        private bool ValidateInputs()
        {
            if (string.IsNullOrWhiteSpace(ServerNameTextBox.Text))
            {
                MessageBox.Show(
                    "Please enter a server name or address.",
                    "Validation Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning
                );
                return false;
            }

            if (SqlAuthRadio.IsChecked == true && string.IsNullOrWhiteSpace(UsernameTextBox.Text))
            {
                MessageBox.Show(
                    "Please enter a username for SQL Server authentication.",
                    "Validation Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning
                );
                return false;
            }

            if (ServicePrincipalAuthRadio.IsChecked == true)
            {
                if (string.IsNullOrWhiteSpace(ServicePrincipalClientIdBox.Text))
                {
                    MessageBox.Show(
                        "Please enter the Application (Client) ID for service principal authentication.",
                        "Validation Error",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning
                    );
                    return false;
                }

                if (string.IsNullOrEmpty(ServicePrincipalSecretBox.Password))
                {
                    MessageBox.Show(
                        "Please enter the client secret for service principal authentication.",
                        "Validation Error",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning
                    );
                    return false;
                }
            }

            return true;
        }

        private async System.Threading.Tasks.Task<(bool Connected, string? ErrorMessage, bool MfaCancelled, string? ServerVersion)> RunConnectionTestAsync(Button triggerButton)
        {
            triggerButton.IsEnabled = false;
            SaveButton.IsEnabled = false;

            StatusText.Text = EntraMfaAuthRadio.IsChecked == true
                ? "Testing connection — please complete authentication in the popup window..."
                : "Testing connection...";
            StatusText.Visibility = System.Windows.Visibility.Visible;

            bool connected = false;
            string? errorMessage = null;
            bool mfaCancelled = false;
            string? serverVersion = null;
            try
            {
                /* Connect to master (not PerformanceMonitor) so the test succeeds
                   even when the database doesn't exist yet — installation detection
                   happens after the connection test in DetectDatabaseStatusAsync() */
                var builder = BuildConnectionBuilder();
                builder.InitialCatalog = "master";
                await using var connection = new SqlConnection(builder.ConnectionString);
                await connection.OpenAsync();
                using var cmd = new SqlCommand("SELECT @@VERSION", connection);
                var version = await cmd.ExecuteScalarAsync() as string;
                serverVersion = version?.Split('\n')[0]?.Trim();
                connected = true;
            }
            catch (Exception ex)
            {
                connected = false;
                errorMessage = ex.Message;
                if (EntraMfaAuthRadio.IsChecked == true && MfaAuthenticationHelper.IsMfaCancelledException(ex))
                    mfaCancelled = true;
            }
            finally
            {
                /* Do not re-arm over a running install: SetFormEnabled(false) disabled these deliberately. */
                if (!InstallInProgress)
                {
                    triggerButton.IsEnabled = true;
                    SaveButton.IsEnabled = true;
                    StatusText.Text = string.Empty;
                    StatusText.Visibility = System.Windows.Visibility.Collapsed;
                }
            }

            return (connected, errorMessage, mfaCancelled, serverVersion);
        }

        private async void CheckForUpdates_Click(object sender, RoutedEventArgs e)
        {
            if (!ValidateInputs()) return;

            CheckForUpdatesButton.IsEnabled = false;
            CheckForUpdatesButton.Content = "Checking...";

            try
            {
                await DetectDatabaseStatusAsync();
            }
            finally
            {
                CheckForUpdatesButton.Content = "Check for Updates";
                /* Do not re-arm over a running install: SetFormEnabled(false) disabled this deliberately. */
                if (!InstallInProgress)
                {
                    CheckForUpdatesButton.IsEnabled = true;
                }
            }
        }

        private async void TestConnection_Click(object sender, RoutedEventArgs e)
        {
            if (!ValidateInputs()) return;

            var (connected, errorMessage, mfaCancelled, serverVersion) = await RunConnectionTestAsync(TestConnectionButton);

            if (connected)
            {
                _serverVersion = serverVersion;

                /* Show connection + database status inline instead of a popup */
                await DetectDatabaseStatusAsync();
            }
            else if (mfaCancelled)
            {
                MessageBox.Show(
                    "Authentication was cancelled. Click Test to try again.",
                    "Authentication Cancelled",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning
                );
            }
            else
            {
                var detail = errorMessage != null ? $"\n\nError: {errorMessage}" : string.Empty;
                MessageBox.Show(
                    $"Could not connect to {ServerNameTextBox.Text}.{detail}\n\nPlease check:\n" +
                    "• Server name/address is correct\n" +
                    "• Server is accessible from this machine\n" +
                    "• Firewall allows SQL Server connections\n" +
                    "• SQL Server service is running\n" +
                    "• You have the 'PerformanceMonitor' database and access to it",
                    "Connection Test Failed",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error
                );
            }
        }

        /*
        True once an install has started. Detection runs are async and can still be in flight when the
        user starts an install, and their continuation would otherwise transition back to a Connected_*
        state -- un-collapsing the panel over a RUNNING install, re-showing the install button, and
        re-enabling Advanced Options. From there a second click starts a concurrent install (and a
        newly-reachable "Clean install" tick drops the database out from under the first one).
        Disabling the buttons is not enough: the detection paths re-enable them in their finally blocks.
        */
        private bool InstallInProgress => _currentState == DialogState.Installing;

        /* The state the install was launched from, so a cancel/failure can return to it. */
        private DialogState _preInstallState = DialogState.Initial;

        /*
        Return to the state the install was launched from -- NOT Initial.

        Initial does not reset InstallUpgradeButton.Content or DatabaseStatusText, and the exit paths
        used to force-show InstallationPanel on top of it. Launched from Connected_Current that produced
        a genuinely dangerous screen: the button still read "Repair", the text still read "is up to
        date", and Advanced Options -- holding "Perform clean install (drops existing database)" -- was
        now visible behind it. Ticking that and clicking [Repair] dropped the database. Connected_Current
        keeps its options panel collapsed precisely so that cannot happen, so restoring it honestly is
        the fix; there, the failure is surfaced in the status panel that IS visible.
        */
        private void RestoreAfterInstall(string message)
        {
            TransitionToState(_preInstallState);

            DatabaseStatusPanel.Visibility = Visibility.Visible;
            InstallationPanel.Visibility = Visibility.Visible;
            InstallStatusText.Text = message;

            /*
            Show the log either way -- it is the only place the actual SQL error text lives, and hiding it
            leaves the user one exception message to work with. But when the run was launched from
            Connected_Current the button still reads "Reinstall Objects" and the clean-install checkbox
            must stay out of reach, so re-show the panel with Advanced Options DISABLED rather than
            collapsed: WPF propagates IsEnabled to children, so the checkbox cannot be ticked.
            */
            AdvancedOptionsExpander.IsEnabled = _preInstallState != DialogState.Connected_Current;
        }

        private async System.Threading.Tasks.Task DetectDatabaseStatusAsync()
        {
            if (InstallInProgress) return;

            try
            {
                StatusText.Text = "Checking database status...";
                StatusText.Visibility = Visibility.Visible;

                /*
                A fresh detection means a fresh server/state, so the destructive and mode-changing
                options start clear. Without this they persist across a retarget of the server box.
                */
                RepairCheckBox.IsChecked = false;
                CleanInstallCheckBox.IsChecked = false;
                /* Sticky and INVISIBLE behind a collapsed panel otherwise -- it TRUNCATEs config.collection_schedule,
                   so a tick left over from another server would wipe this one's tuned intervals with no consent. */
                ResetScheduleCheckBox.IsChecked = false;

                string installerConnStr = BuildInstallerConnectionString();
                string appVersion = GetAppVersion();

                /*
                Land in a LOCAL, guard, and only then commit to the shared field. An install may have
                started while this was in flight (against a different server -- the box stays editable),
                and it reads _coreServerInfo. Guarding after the field write would already have clobbered
                it with the previous server's ServerInfo.
                */
                var probedServerInfo = await InstallationService.TestConnectionAsync(installerConnStr);

                /* An install started while we were connecting: do not touch what it now owns. */
                if (InstallInProgress) return;

                _coreServerInfo = probedServerInfo;

                if (_coreServerInfo == null || !_coreServerInfo.IsConnected)
                {
                    StatusText.Text = string.Empty;
                    StatusText.Visibility = Visibility.Collapsed;
                    return;
                }

                if (!_coreServerInfo.IsSupportedVersion)
                {
                    string serverName = ServerNameTextBox.Text;
                    ConnectionInfoText.Text = _serverVersion != null
                        ? $"Connected to {serverName} ({_serverVersion})"
                        : $"Connected to {serverName}";
                    DatabaseStatusText.Text = $"Warning: {_coreServerInfo.ProductMajorVersionName} is not supported. SQL Server 2016+ is required.";
                    DatabaseStatusPanel.Visibility = Visibility.Visible;
                    InstallUpgradeButton.Visibility = Visibility.Collapsed;
                    SkipInstallText.Visibility = Visibility.Collapsed;
                    StatusText.Text = string.Empty;
                    StatusText.Visibility = Visibility.Collapsed;
                    return;
                }

                /*
                Check installed version. throwOnError matters: with the soft overload a transient
                SqlException -- a timeout, the database OFFLINE/RESTORING, a permissions blip -- comes
                back as null, which is indistinguishable from "no database". That drops us into the
                fresh-install path, which skips every migration, reinstalls over the existing
                database, and then stamps installation_history SUCCESS at the target version --
                stranding every pending hop permanently. The CLI passes throwOnError: true for
                exactly this reason; the Dashboard's install path must too.
                */
                _installBlockedReason = null;

                try
                {
                    /* Local first, guard, then commit -- a running install reads _installedVersion. */
                    var probedVersion = await InstallationService.GetInstalledVersionAsync(
                        installerConnStr,
                        throwOnError: true);

                    /* An install started while we were probing: do not touch what it now owns. */
                    if (InstallInProgress) return;

                    _installedVersion = probedVersion;
                }
                catch (Exception ex)
                {
                    if (InstallInProgress) return;

                    BlockInstall(
                        $"Could not determine the installed PerformanceMonitor version: {ex.Message}\n\n" +
                        "Install and upgrade are blocked until this resolves. Proceeding could reinstall over an " +
                        "existing database, skip its pending migrations, and record it as up to date.");
                    return;
                }

                string? blockReason = GetInstallBlockReason(_installedVersion, appVersion);
                if (blockReason != null)
                {
                    BlockInstall(blockReason);
                    return;
                }

                if (_installedVersion == null)
                {
                    TransitionToState(DialogState.Connected_NoDatabase);
                    return;
                }

                /* Both parse and installed <= app, or GetInstallBlockReason would have blocked above. */
                var installedCore = ScriptProvider.TryParseVersionCore(_installedVersion)!;
                var appCore = ScriptProvider.TryParseVersionCore(appVersion)!;

                TransitionToState(installedCore < appCore
                    ? DialogState.Connected_NeedsUpgrade
                    : DialogState.Connected_Current);
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Could not check database status: {ex.Message}";
                StatusText.Visibility = Visibility.Visible;
            }
        }

        /* Record why installing is unsafe and drop into the state that hides the install button. */
        private void BlockInstall(string reason)
        {
            _installBlockedReason = reason;
            TransitionToState(DialogState.Connected_StatusUnknown);
        }

        /*
        Decide whether installing against this installed version is safe, and if not, why. Returns null
        when it is safe (including "no database", which is a fresh install).

        Both the Test Connection path and the install click run this. It cannot be computed once and
        cached: the server box stays editable and BuildInstallerConnectionString() rebuilds the
        connection string live, so a verdict from the last detection may belong to a different server.
        Refreshing the version alone is not enough either -- the "installed is NEWER than this build"
        case produces zero hops and zero failures, so nothing downstream catches it, and the install
        would silently revert a newer database to this binary's older object definitions and record
        the lower version as SUCCESS.
        */
        internal static string? GetInstallBlockReason(string? installedVersion, string appVersion)
        {
            /*
            The DECISION lives in Installer.Core/InstallGuard, shared with the CLI and pinned by tests
            that actually run in CI. Only the wording is ours. Two of these cases are invisible to every
            other guard -- they produce zero upgrade hops AND zero failures, so the migration-failure
            abort never fires and nothing downstream notices.
            */
            switch (InstallGuard.Check(installedVersion, appVersion))
            {
                case InstallBlock.UnreadableBuildVersion:
                    return $"This Dashboard reports its own version as '{appVersion}', which is not a valid version.\n\n" +
                        "Install and upgrade are blocked: without a comparable version we cannot tell which " +
                        "migrations still need to run, and a fresh install would record that value as the " +
                        "server's version. This is a build problem, not a problem with the server.";

                case InstallBlock.UnreadableInstalledVersion:
                    return $"Could not interpret the installed PerformanceMonitor version ('{installedVersion}').\n\n" +
                        "Install and upgrade are blocked: without a comparable version we cannot tell which " +
                        "migrations still need to run. Correct the most recent SUCCESS row in " +
                        "PerformanceMonitor.config.installation_history, or rebuild the database with the " +
                        "CLI installer's --reinstall (destructive).";

                case InstallBlock.InstalledIsNewerThanBuild:
                    return $"PerformanceMonitor v{NormalizeVersion(installedVersion!)} is installed, which is newer than this " +
                        $"Dashboard (v{NormalizeVersion(appVersion)}).\n\n" +
                        "Install and repair are blocked: running the older installer would revert this server's " +
                        "objects to the older definitions and record it at the lower version. Update this Dashboard first.";

                default:
                    return null;
            }
        }

        private void TransitionToState(DialogState newState)
        {
            _currentState = newState;
            string appVersion = GetAppVersion();

            /* Reset panel visibility */
            DatabaseStatusPanel.Visibility = Visibility.Collapsed;
            InstallationPanel.Visibility = Visibility.Collapsed;
            MonitoringCredsPanel.Visibility = Visibility.Collapsed;
            ViewReportButton.Visibility = Visibility.Collapsed;
            /*
            Unfreeze Advanced Options. The install states below re-disable it immediately, but without
            this it was only ever re-enabled on the abort/cancel/fatal paths -- so after a completed
            install it stayed disabled for the rest of the dialog's life, stranding whatever mode
            checkboxes were ticked inside it with no way to untick them.
            */
            AdvancedOptionsExpander.IsEnabled = true;
            /* Only the Installing state arms Cancel; otherwise it paints "Cancelling..." over nothing. */
            CancelInstallButton.IsEnabled = false;
            StatusText.Text = string.Empty;
            StatusText.Visibility = Visibility.Collapsed;
            ConnectionInfoText.Text = string.Empty;
            InstallUpgradeButton.Visibility = Visibility.Visible;
            SkipInstallText.Visibility = Visibility.Visible;

            /* Build the connection header shown for all connected states */
            string serverName = ServerNameTextBox.Text;
            string connectionHeader = _serverVersion != null
                ? $"Connected to {serverName} ({_serverVersion})"
                : $"Connected to {serverName}";

            switch (newState)
            {
                case DialogState.Connected_NoDatabase:
                    ConnectionInfoText.Text = connectionHeader;
                    DatabaseStatusText.Text = "No PerformanceMonitor database found on this server. " +
                        $"Click Install Now to create the monitoring database, collection jobs, and stored procedures (v{appVersion}).";
                    InstallUpgradeButton.Content = "Install Now";
                    DatabaseStatusPanel.Visibility = Visibility.Visible;
                    InstallationPanel.Visibility = Visibility.Visible;
                    /* Reachable from Installing (the "nothing to repair" refusal), which disabled the form. */
                    SetFormEnabled(true);
                    SaveButton.IsEnabled = false;
                    break;

                case DialogState.Connected_NeedsUpgrade:
                    string normalizedInstalled = NormalizeVersion(_installedVersion!);
                    ConnectionInfoText.Text = connectionHeader;
                    DatabaseStatusText.Text = $"PerformanceMonitor v{normalizedInstalled} is installed. " +
                        $"v{appVersion} is available — click Upgrade Now to apply the update.";
                    InstallUpgradeButton.Content = "Upgrade Now";
                    DatabaseStatusPanel.Visibility = Visibility.Visible;
                    InstallationPanel.Visibility = Visibility.Visible;
                    /* Reachable from Installing (every upgrade abort restores here), which disabled the form. */
                    SetFormEnabled(true);
                    SaveButton.IsEnabled = true;
                    break;

                case DialogState.Connected_Current:
                    string normalizedCurrent = NormalizeVersion(_installedVersion!);
                    ConnectionInfoText.Text = connectionHeader;
                    DatabaseStatusText.Text = $"PerformanceMonitor v{normalizedCurrent} is up to date. " +
                        "If objects are missing or damaged, click Reinstall Objects to restore them.";
                    /*
                    Repair stays reachable at the current version: the install scripts are idempotent,
                    so re-running them restores missing objects. This state now means installed ==
                    target exactly, so there are no migrations to skip and this is a plain reinstall at
                    the same version. The CLI can already do this with --repair; without it the
                    Dashboard had no non-destructive recovery for a healthy-version-but-damaged database.

                    InstallationPanel stays collapsed on purpose: it holds the "drops existing database"
                    clean-install checkbox, which must never sit behind this button.

                    Named "Reinstall Objects", NOT "Repair": the Repair CHECKBOX means something different
                    (skip the migrations, write no history row), and it is deliberately unreachable here.
                    This button runs the ordinary install path -- with installed == target there are no
                    migrations to skip, so it is a same-version REINSTALL and correctly records one.
                    */
                    InstallUpgradeButton.Content = "Reinstall Objects";
                    SkipInstallText.Visibility = Visibility.Collapsed;
                    DatabaseStatusPanel.Visibility = Visibility.Visible;
                    /* Reachable from Installing (a failed reinstall restores here), which disabled the form. */
                    SetFormEnabled(true);
                    SaveButton.IsEnabled = true;
                    break;

                case DialogState.Connected_StatusUnknown:
                    ConnectionInfoText.Text = connectionHeader;
                    DatabaseStatusText.Text = _installBlockedReason ?? "The database status could not be determined.";
                    InstallUpgradeButton.Visibility = Visibility.Collapsed;
                    SkipInstallText.Visibility = Visibility.Collapsed;
                    DatabaseStatusPanel.Visibility = Visibility.Visible;
                    /* Reachable from Installing (the install-time re-check blocks), which disabled the form. */
                    CancelInstallButton.IsEnabled = false;
                    SetFormEnabled(true);
                    SaveButton.IsEnabled = true;
                    break;

                case DialogState.Installing:
                    DatabaseStatusPanel.Visibility = Visibility.Collapsed;
                    InstallationPanel.Visibility = Visibility.Visible;
                    AdvancedOptionsExpander.IsEnabled = false;
                    CancelInstallButton.IsEnabled = true;
                    SetFormEnabled(false);
                    break;

                case DialogState.InstallComplete:
                    InstallationPanel.Visibility = Visibility.Visible;
                    /*
                    Clear both mode checkboxes once a run completes, on EVERY outcome. They are sticky
                    otherwise: the form is re-enabled here, so the user can retype the server box and
                    click again -- carrying "Repair" (skip every migration) or, worse, "Clean install"
                    (drop the database) onto a server that was never consented to.
                    */
                    RepairCheckBox.IsChecked = false;
                    CleanInstallCheckBox.IsChecked = false;
                    /* Sticky and INVISIBLE behind a collapsed panel otherwise -- it TRUNCATEs config.collection_schedule,
                       so a tick left over from another server would wipe this one's tuned intervals with no consent. */
                    ResetScheduleCheckBox.IsChecked = false;
                    AdvancedOptionsExpander.IsEnabled = false;
                    CancelInstallButton.IsEnabled = false;
                    SetFormEnabled(true);
                    if (_reportPath != null)
                    {
                        ViewReportButton.Visibility = Visibility.Visible;
                    }
                    /* Transition to monitoring credentials if using SQL auth */
                    if (SqlAuthRadio.IsChecked == true)
                    {
                        TransitionToState(DialogState.MonitoringCredentials);
                        return;
                    }
                    SaveButton.IsEnabled = true;
                    SaveButton.Content = "Save & Connect";
                    break;

                case DialogState.MonitoringCredentials:
                    InstallationPanel.Visibility = Visibility.Visible;
                    AdvancedOptionsExpander.IsEnabled = false;
                    CancelInstallButton.IsEnabled = false;
                    MonitoringCredsPanel.Visibility = Visibility.Visible;
                    if (_reportPath != null)
                    {
                        ViewReportButton.Visibility = Visibility.Visible;
                    }
                    SetFormEnabled(true);
                    SaveButton.IsEnabled = true;
                    SaveButton.Content = "Save & Connect";
                    break;

                case DialogState.Initial:
                default:
                    /*
                    The cancel and fatal-error paths land here, and both re-enable the form and re-show
                    the panels -- so a ticked "Clean install (drops existing database)" would survive a
                    cancelled run, and the server box could then be retargeted and clicked, dropping a
                    database that was never consented to. Clear the mode flags here as well as at
                    InstallComplete, so every exit from a run clears them.
                    */
                    RepairCheckBox.IsChecked = false;
                    CleanInstallCheckBox.IsChecked = false;
                    /* Sticky and INVISIBLE behind a collapsed panel otherwise -- it TRUNCATEs config.collection_schedule,
                       so a tick left over from another server would wipe this one's tuned intervals with no consent. */
                    ResetScheduleCheckBox.IsChecked = false;
                    SetFormEnabled(true);
                    SaveButton.IsEnabled = true;
                    SaveButton.Content = "Save";
                    break;
            }
        }

        private async void InstallOrUpgrade_Click(object sender, RoutedEventArgs e)
        {
            /*
            Never install when the detected state said it is unsafe: we cannot tell an absent database
            from an existing one we simply could not read, and guessing "absent" reinstalls over it with
            the migrations skipped and then records it as up to date.
            */
            /* Structural backstop: never start a second install over a running one. */
            if (InstallInProgress) return;

            if (_installBlockedReason != null)
            {
                MessageBox.Show(_installBlockedReason, "Install Blocked", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            /*
            Freeze the UI and arm cancellation BEFORE the first await. TransitionToState(Installing)
            collapses DatabaseStatusPanel, which is the Border InstallUpgradeButton lives in -- and that
            collapse is the ONLY thing that makes the button unclickable, since SetFormEnabled never
            touches it. Awaiting the version re-read in front of it would leave the button live for the
            whole connect timeout (unbounded for interactive Entra auth), and this handler is async void:
            a second click starts a second concurrent install that clobbers _installCts and
            _installResult, races two clean installs, and leaves Cancel pointing at the wrong run.
            Creating the CTS first also means Cancel and ESC work DURING the probe rather than no-oping
            on a null and letting the install continue after the dialog has closed.
            */
            _preInstallState = _currentState;

            TransitionToState(DialogState.Installing);
            InstallLogTextBox.Clear();
            InstallProgressBar.Value = 0;
            InstallStatusText.Text = "Checking installed version...";

            _installCts?.Dispose();
            _installCts = new CancellationTokenSource();
            var cancellationToken = _installCts.Token;

            try
            {
                var provider = ScriptProvider.FromEmbeddedResources();
                string installerConnStr = BuildInstallerConnectionString();
                string appVersion = GetAppVersion();

                /*
                Re-read the installed version against the connection we are about to WRITE to, and
                re-run the safety verdict on the result. The cached _installedVersion and
                _installBlockedReason come from the last Test Connection, but the server box stays
                editable and the connection string is rebuilt live -- so retyping it and clicking
                straight through would install into a different server while still reasoning about the
                previous one. Refreshing the version alone is not enough: the "installed is NEWER than
                this build" verdict produces zero hops and zero failures, so nothing downstream catches
                it, and the install would silently downgrade the server and record the lower version.
                */
                try
                {
                    _installedVersion = await InstallationService.GetInstalledVersionAsync(
                        installerConnStr,
                        throwOnError: true,
                        cancellationToken: cancellationToken);
                }
                catch (Exception) when (cancellationToken.IsCancellationRequested)
                {
                    /*
                    SqlCommand does NOT surface cancellation as an OperationCanceledException: it cancels
                    the in-flight command and the task faults with a SqlException ("Operation cancelled by
                    user."). Without this, the user's own Cancel would be reported as an
                    unknown-database-state block, hiding the install button until they re-tested.
                    */
                    throw new OperationCanceledException(cancellationToken);
                }
                catch (Exception ex)
                {
                    await Dispatcher.InvokeAsync(() => BlockInstall(
                        $"Could not determine the installed PerformanceMonitor version on this server: {ex.Message}\n\n" +
                        "Install and upgrade are blocked: proceeding could reinstall over an existing database " +
                        "and skip its pending migrations."));
                    return;
                }

                /*
                Refuse to repair what is not there. Falling through would silently run a FULL fresh install
                and stamp the target version -- a whole new database on a mistyped server, and, if the
                database exists but its history table does not, a target-version stamp that strands every
                migration in between. Someone who ticked Repair asked for the opposite.
                */
                if (RepairCheckBox.IsChecked == true &&
                    CleanInstallCheckBox.IsChecked != true &&
                    _installedVersion == null)
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        RepairCheckBox.IsChecked = false;
                        InstallStatusText.Text = string.Empty;
                        TransitionToState(DialogState.Connected_NoDatabase);
                        MessageBox.Show(
                            "There is no existing PerformanceMonitor installation on this server to repair.\n\n" +
                            "Repair reinstalls the objects of an existing installation; it will not create one. " +
                            "Repair has been cleared -- click Install Now to perform a fresh install.",
                            "Nothing to Repair",
                            MessageBoxButton.OK,
                            MessageBoxImage.Information);
                    });
                    return;
                }

                string? blockReason = GetInstallBlockReason(_installedVersion, appVersion);
                if (blockReason != null)
                {
                    await Dispatcher.InvokeAsync(() => BlockInstall(blockReason));
                    return;
                }

                InstallStatusText.Text = "Preparing installation...";

                bool cleanInstall = CleanInstallCheckBox.IsChecked == true;

                /*
                Repair only means anything for an existing installation we are not about to drop.
                Resolving it to the version we repair FROM (rather than a bare bool) keeps the
                history write below from ever recording the old version after a clean install.
                */
                string? repairFromVersion = RepairCheckBox.IsChecked == true && !cleanInstall
                    ? _installedVersion
                    : null;

                bool resetSchedule = ResetScheduleCheckBox.IsChecked == true;
                bool runValidation = ValidationCheckBox.IsChecked == true;
                bool installDeps = InstallDepsCheckBox.IsChecked == true;

                var progress = new Progress<InstallationProgress>(p =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        /* Update progress bar */
                        if (p.ProgressPercent.HasValue)
                        {
                            InstallProgressBar.Value = p.ProgressPercent.Value;
                        }

                        /* Update status text */
                        if (!string.IsNullOrEmpty(p.Message))
                        {
                            InstallStatusText.Text = p.Message;
                        }

                        /* Append to log (filter out Debug messages) */
                        if (p.Status != "Debug")
                        {
                            AppendInstallLog(p.Message, p.Status);
                        }
                    });
                });

                /* Run upgrades if applicable (existing database, not clean install) */
                int upgradeSuccess = 0;
                int upgradeFailure = 0;

                if (repairFromVersion != null)
                {
                    /*
                    Repair reinstalls the schema objects (install scripts are idempotent) without
                    running migrations, so a hop that failed on a missing or damaged object can be
                    recovered without dropping the database. The pending upgrade runs afterwards.
                    */
                    AppendInstallLog(
                        "Repair mode: skipping upgrade scripts. Objects will be reinstalled at their current definitions; the pending upgrade still needs to run afterwards.",
                        "Warning");
                }
                else if (!cleanInstall && _installedVersion != null)
                {
                    AppendInstallLog($"Checking for upgrades from v{NormalizeVersion(_installedVersion)} to v{appVersion}...", "Info");

                    var upgradeResult = await InstallationService.ExecuteAllUpgradesAsync(
                        provider,
                        installerConnStr,
                        _installedVersion,
                        appVersion,
                        progress,
                        cancellationToken);

                    upgradeSuccess = upgradeResult.totalSuccessCount;
                    upgradeFailure = upgradeResult.totalFailureCount;

                    if (upgradeResult.upgradeCount > 0)
                    {
                        AppendInstallLog($"Upgrades complete: {upgradeSuccess} succeeded, {upgradeFailure} failed", upgradeFailure == 0 ? "Success" : "Warning");
                    }

                    /*
                    Abort when an upgrade script fails, matching the CLI installer.
                    Reinstalling over a partially-upgraded database compounds the damage, and
                    recording a successful install at the target version would strand the failed
                    migration permanently: version detection reads the most recent SUCCESS row, so
                    the hop would never be offered again. Writing no history row leaves the server
                    at its current version, and upgrade scripts are idempotent, so re-running after
                    fixing the error resumes cleanly.
                    */
                    if (upgradeFailure > 0)
                    {
                        await Dispatcher.InvokeAsync(() =>
                        {
                            InstallProgressBar.Value = 0;
                            AppendInstallLog(
                                $"Installation aborted: {upgradeFailure} upgrade script(s) failed. Upgrade scripts must succeed before installation can proceed.",
                                "Error");
                            AppendInstallLog(
                                "Fix the errors above and run the upgrade again. The server remains at its current version.",
                                "Info");
                            AppendInstallLog(
                                "If the failure is a missing or damaged object, tick 'Repair' in Advanced Options to reinstall the schema objects without running migrations, then run the upgrade again.",
                                "Info");
                            RestoreAfterInstall($"Upgrade aborted: {upgradeFailure} upgrade script(s) failed.");
                        });

                        return;
                    }
                }

                /*
                A repair reinstalls objects without running migrations, so it must NOT record the
                target version -- that would strand every pending hop, which is exactly the bug the
                abort above exists to prevent.

                So a repair writes NO history row at all. It does not change the version, and
                installation_history is the version ledger -- writing back a version we merely READ is
                how a guess becomes a fact. Concretely: GetInstalledVersionAsync returns "1.0.0" as a
                #538 fallback when the database exists but has no SUCCESS row, meaning "unknown, try
                every upgrade". Echoing that back as a SUCCESS row would persist the guess as truth.
                Writing nothing leaves the previous row as the version of record, which is exactly
                right -- the pending upgrade is still offered afterwards.
                */
                bool isRepair = repairFromVersion != null;

                /* Run main installation */
                AppendInstallLog("Starting main installation...", "Info");

                Func<System.Threading.Tasks.Task>? preValidationAction = null;
                if (installDeps)
                {
                    preValidationAction = async () =>
                    {
                        AppendInstallLog("Installing community dependencies...", "Info");
                        string communityDir = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "community");
                        using var depInstaller = new DependencyInstaller(communityDir);
                        await depInstaller.InstallDependenciesAsync(installerConnStr, progress, cancellationToken);
                    };
                }

                _installResult = await InstallationService.ExecuteInstallationAsync(
                    installerConnStr,
                    provider,
                    cleanInstall,
                    resetSchedule,
                    progress,
                    preValidationAction,
                    cancellationToken);

                /* Log installation history -- but never for a repair, which changes no version (see above). */
                if (isRepair)
                {
                    AppendInstallLog(
                        "Repair does not change the recorded version, so no installation history row was written.",
                        "Info");
                }
                else
                {
                    try
                    {
                        AppendInstallLog("Recording installation history...", "Info");

                        /*
                        Fold the upgrade script counts in. InstallationResult only covers the install
                        files, so passing it alone would under-report files_executed and could record a
                        SUCCESS at the target version even when a migration had failed.
                        */
                        await InstallationService.LogInstallationHistoryAsync(
                            installerConnStr,
                            appVersion,
                            appVersion,
                            _installResult.StartTime,
                            upgradeSuccess + _installResult.FilesSucceeded,
                            upgradeFailure + _installResult.FilesFailed,
                            upgradeFailure == 0 && _installResult.Success,
                            progress);
                        AppendInstallLog("Installation history recorded", "Success");
                    }
                    catch (Exception ex)
                    {
                        AppendInstallLog($"Could not record installation history: {ex.Message}", "Warning");
                    }
                }

                /* Run validation if requested */
                if (runValidation && _installResult.Success)
                {
                    try
                    {
                        AppendInstallLog("Running post-install validation...", "Info");
                        var (collectorsSucceeded, collectorsFailed) = await InstallationService.RunValidationAsync(
                            installerConnStr,
                            progress,
                            cancellationToken);
                        AppendInstallLog($"Validation: {collectorsSucceeded} collectors succeeded, {collectorsFailed} failed",
                            collectorsFailed == 0 ? "Success" : "Warning");
                    }
                    catch (Exception ex)
                    {
                        AppendInstallLog($"Validation failed: {ex.Message}", "Warning");
                    }
                }

                /* Generate summary report */
                try
                {
                    /* This parameter is printed as "Installer Version" -- it names the BINARY that ran, not the database. */
                    _reportPath = InstallationService.GenerateSummaryReport(
                        ServerNameTextBox.Text.Trim(),
                        _coreServerInfo?.SqlServerVersion ?? "",
                        _coreServerInfo?.SqlServerEdition ?? "",
                        appVersion,
                        _installResult);
                    AppendInstallLog($"Report saved: {_reportPath}", "Info");
                }
                catch (Exception ex)
                {
                    AppendInstallLog($"Could not generate report: {ex.Message}", "Warning");
                }

                /*
                Did a CRITICAL file (01_/02_/03_) fail? ExecuteInstallationAsync aborts the whole pass on
                those, so the repair reinstalled almost nothing. That is NOT the expected Msg 207 outcome
                below, and must not be dressed up as one.
                */
                bool criticalFileFailed = _installResult.Errors.Any(e => Patterns.IsCriticalFile(e.FileName));

                /* Shared with the CLI so the two cannot drift -- see RepairOutcome. */
                bool repairHasPendingUpgrade = RepairOutcome.FailuresAreExpected(
                    isRepair,
                    repairFromVersion,
                    appVersion,
                    criticalFileFailed);

                bool repairUsable = isRepair && !criticalFileFailed &&
                    (_installResult.Success || repairHasPendingUpgrade);

                /* Update final status */
                await Dispatcher.InvokeAsync(() =>
                {
                    InstallProgressBar.Value = 100;
                    if (repairUsable)
                    {
                        /*
                        A repair on a database with pending migrations is EXPECTED to report file errors,
                        and that is not a failed repair. The install scripts compile against the CURRENT
                        schema -- e.g. install/23_process_blocked_process_xml.sql reads
                        collect.blocking_BlockedProcessReport.monitor_loop, a column the 3.0.0-to-3.1.0
                        migration adds -- and ALTER PROCEDURE binds columns at compile time, so those
                        procedures cannot compile until the upgrade runs (Msg 207, "Invalid column name").
                        A failed CREATE OR ALTER leaves the old body intact, so nothing is damaged; the
                        upgrade's own install pass recompiles them once the schema is current.

                        So the handoff is NOT gated on _installResult.Success: gating it would leave the
                        user staring at "completed with N error(s)" and no next step, which is how the
                        half-migrated database this feature exists to escape gets left behind. It IS gated
                        on there being a pending upgrade, because without one those errors are real.
                        */
                        if (repairHasPendingUpgrade)
                        {
                            InstallStatusText.Text = _installResult.Success
                                ? "Repair completed. Run the upgrade to apply pending migrations."
                                : "Repair completed with expected errors. Run the upgrade to complete.";

                            AppendInstallLog(
                                $"Repair complete. The server is still at v{NormalizeVersion(repairFromVersion!)} and no version was recorded -- click Upgrade Now to apply the pending migrations.",
                                "Success");

                            if (!_installResult.Success)
                            {
                                AppendInstallLog(
                                    $"{_installResult.FilesFailed} object(s) could not be compiled because the pending upgrade has not run yet. " +
                                    "This is expected -- they reference columns the upgrade adds, and the upgrade will recompile them.",
                                    "Info");
                            }
                        }
                        else
                        {
                            InstallStatusText.Text = "Repair completed successfully.";
                            AppendInstallLog(
                                "Repair complete. This server was already at the current version, so no version was recorded and there is no upgrade to apply.",
                                "Success");
                        }
                    }
                    else if (_installResult.Success)
                    {
                        InstallStatusText.Text = "Installation completed successfully!";
                        AppendInstallLog("Installation completed successfully!", "Success");
                    }
                    else
                    {
                        InstallStatusText.Text = $"Installation completed with {_installResult.FilesFailed} error(s).";
                        AppendInstallLog($"Installation completed with {_installResult.FilesFailed} error(s).", "Error");
                    }

                    TransitionToState(DialogState.InstallComplete);

                    /*
                    A successful repair leaves the migrations UNAPPLIED by design, so the user has to run
                    the upgrade next. InstallComplete collapses DatabaseStatusPanel, which is the Border
                    InstallUpgradeButton lives in -- so without re-showing it, the completion message
                    points at a button that is not on screen and the obvious next click is Save & Connect,
                    leaving exactly the half-migrated database this feature exists to escape.

                    MonitoringCredentials is included: InstallComplete chains straight into it for SQL
                    auth, and that state does not re-show the panel either -- so without it, every SQL-auth
                    user (the ones most likely to hit this) would get the dead end. The three panels live in
                    separate grid rows and coexist fine.

                    Not gated on _installResult.Success, for the reason above: a repair with the expected
                    compile errors still needs the handoff. It IS gated on no critical file having failed,
                    because that repair reinstalled nothing.
                    */
                    if (repairUsable && repairHasPendingUpgrade &&
                        (_currentState == DialogState.InstallComplete ||
                         _currentState == DialogState.MonitoringCredentials))
                    {
                        /* The prologue blanks this, and neither completion state repopulates it. */
                        ConnectionInfoText.Text = $"Connected to {ServerNameTextBox.Text}";
                        DatabaseStatusText.Text =
                            $"Objects were reinstalled. PerformanceMonitor is still at v{NormalizeVersion(repairFromVersion!)} " +
                            "-- the pending upgrade has not been applied. Click Upgrade Now to apply it.";
                        InstallUpgradeButton.Content = "Upgrade Now";
                        InstallUpgradeButton.Visibility = Visibility.Visible;
                        DatabaseStatusPanel.Visibility = Visibility.Visible;
                        /*
                        That Border also holds the "Skip, just add server" link. In MonitoringCredentials it
                        would otherwise become clickable for the first time, and SkipInstall_Click flips
                        _currentState to Initial -- after which Save_Click silently saves the INSTALLER
                        credentials instead of the monitoring credentials the user just typed.
                        */
                        SkipInstallText.Visibility = Visibility.Collapsed;
                    }
                });
            }
            catch (OperationCanceledException)
            {
                Dispatcher.Invoke(() =>
                {
                    AppendInstallLog("Installation was cancelled by user.", "Warning");
                    InstallProgressBar.Value = 0;
                    RestoreAfterInstall("Installation cancelled.");
                });
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() =>
                {
                    AppendInstallLog($"Fatal error: {ex.Message}", "Error");
                    RestoreAfterInstall($"Installation failed: {ex.Message}");
                });
            }
            finally
            {
                _installCts?.Dispose();
                _installCts = null;
            }
        }

        private void CancelInstall_Click(object sender, RoutedEventArgs e)
        {
            _installCts?.Cancel();
            CancelInstallButton.IsEnabled = false;
            InstallStatusText.Text = "Cancelling...";
        }

        /* A clean install drops and recreates the database, so there is nothing left to repair. */
        private void CleanInstallCheckBox_Checked(object sender, RoutedEventArgs e) =>
            RepairCheckBox.IsChecked = false;

        /* Repair is the non-destructive recovery path, so it cannot mean "drop the database". */
        private void RepairCheckBox_Checked(object sender, RoutedEventArgs e) =>
            CleanInstallCheckBox.IsChecked = false;

        private void AppendInstallLog(string message, string status)
        {
            if (string.IsNullOrEmpty(message))
                return;

            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() => AppendInstallLog(message, status));
                return;
            }

            string prefix = status switch
            {
                "Success" => "[OK] ",
                "Error" => "[ERROR] ",
                "Warning" => "[WARN] ",
                _ => ""
            };

            InstallLogTextBox.AppendText($"{prefix}{message}\n");
            InstallLogTextBox.ScrollToEnd();
        }

        private void SkipInstall_Click(object sender, MouseButtonEventArgs e)
        {
            DatabaseStatusPanel.Visibility = Visibility.Collapsed;
            InstallationPanel.Visibility = Visibility.Collapsed;
            SaveButton.IsEnabled = true;
            SaveButton.Content = "Save";
            _currentState = DialogState.Initial;
        }

        private void ViewReport_Click(object sender, RoutedEventArgs e)
        {
            if (_reportPath != null && File.Exists(_reportPath))
            {
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = _reportPath,
                        UseShellExecute = true
                    });
                }
                catch (Exception ex)
                {
                    MessageBox.Show(
                        $"Could not open report: {ex.Message}",
                        "Error",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning
                    );
                }
            }
        }

        private void UseSameCredsCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (MonitorCredsFieldsPanel != null)
            {
                MonitorCredsFieldsPanel.Visibility = UseSameCredsCheckBox.IsChecked == true
                    ? Visibility.Collapsed
                    : Visibility.Visible;
            }
        }

        private void SetFormEnabled(bool enabled)
        {
            ServerNameTextBox.IsEnabled = enabled;
            DisplayNameTextBox.IsEnabled = enabled;
            WindowsAuthRadio.IsEnabled = enabled;
            SqlAuthRadio.IsEnabled = enabled;
            EntraMfaAuthRadio.IsEnabled = enabled;
            ServicePrincipalAuthRadio.IsEnabled = enabled;
            ManagedIdentityAuthRadio.IsEnabled = enabled;
            UsernameTextBox.IsEnabled = enabled;
            PasswordBox.IsEnabled = enabled;
            EntraMfaUsernameBox.IsEnabled = enabled;
            ServicePrincipalClientIdBox.IsEnabled = enabled;
            ServicePrincipalSecretBox.IsEnabled = enabled;
            ManagedIdentityClientIdBox.IsEnabled = enabled;
            EncryptModeComboBox.IsEnabled = enabled;
            TrustServerCertificateCheckBox.IsEnabled = enabled;
            ReadOnlyIntentCheckBox.IsEnabled = enabled;
            MultiSubnetFailoverCheckBox.IsEnabled = enabled;
            IsFavoriteCheckBox.IsEnabled = enabled;
            MonthlyCostTextBox.IsEnabled = enabled;
            AlertDeliveryOverrideComboBox.IsEnabled = enabled;
            DescriptionTextBox.IsEnabled = enabled;
            TestConnectionButton.IsEnabled = enabled;
            /*
            Check for Updates re-runs DetectDatabaseStatusAsync, which transitions back to a Connected_*
            state -- re-showing the install button and collapsing the running install's progress panel.
            Leaving it live during Installing let a second click start a concurrent install against the
            same database, clobbering _installResult and leaving Cancel pointing at the wrong run.
            */
            CheckForUpdatesButton.IsEnabled = enabled;
            SaveButton.IsEnabled = enabled;
        }

        private async void Save_Click(object sender, RoutedEventArgs e)
        {
            if (!ValidateInputs()) return;

            /* If we just finished installing, skip re-testing the connection */
            bool skipConnectionTest = _currentState == DialogState.InstallComplete ||
                                       _currentState == DialogState.MonitoringCredentials;

            if (!skipConnectionTest)
            {
                var (connected, errorMessage, mfaCancelled, _) = await RunConnectionTestAsync(SaveButton);

                /*
                An install started while this connection test was in flight (InstallUpgradeButton stays
                hit-testable -- SetFormEnabled does not touch it). Falling through would set DialogResult
                and Close(), destroying the dialog while the install runs on against the database with no
                UI and nothing left to cancel it.
                */
                if (InstallInProgress) return;

                if (!connected)
                {
                    if (mfaCancelled)
                    {
                        MessageBox.Show(
                            "Authentication was cancelled. Click Save to try again, or Cancel to abort.",
                            "Authentication Cancelled",
                            MessageBoxButton.OK,
                            MessageBoxImage.Warning
                        );
                        return;
                    }

                    var detail = errorMessage != null ? $"\n\nError: {errorMessage}" : string.Empty;
                    var result = MessageBox.Show(
                        $"Could not connect to {ServerNameTextBox.Text}.{detail}\n\n" +
                        "Do you still want to save this connection?",
                        "Connection Failed",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Warning
                    );

                    if (result != MessageBoxResult.Yes)
                        return;
                }
            }

            // Determine authentication type and credentials
            string authenticationType;
            string? azureClientId = null;
            string? managedIdentityClientId = null;
            if (WindowsAuthRadio.IsChecked == true)
            {
                authenticationType = AuthenticationTypes.Windows;
                Username = null;
                Password = null;
            }
            else if (EntraMfaAuthRadio.IsChecked == true)
            {
                authenticationType = AuthenticationTypes.EntraMFA;
                Username = EntraMfaUsernameBox.Text.Trim();
                Password = null;
            }
            else if (ServicePrincipalAuthRadio.IsChecked == true)
            {
                authenticationType = AuthenticationTypes.ServicePrincipal;
                // Surface the client id as the Username and the secret as the Password so the existing
                // ServerManager credential-save path persists them (client id + client secret).
                azureClientId = ServicePrincipalClientIdBox.Text.Trim();
                Username = azureClientId;
                Password = ServicePrincipalSecretBox.Password;
            }
            else if (ManagedIdentityAuthRadio.IsChecked == true)
            {
                authenticationType = AuthenticationTypes.ManagedIdentity;
                managedIdentityClientId = string.IsNullOrWhiteSpace(ManagedIdentityClientIdBox.Text)
                    ? null
                    : ManagedIdentityClientIdBox.Text.Trim();
                // No credential to store for managed identity.
                Username = null;
                Password = null;
            }
            else
            {
                authenticationType = AuthenticationTypes.SqlServer;

                /*
                Use the monitoring credentials if the user actually entered them.

                Deliberately NOT gated on _currentState: the repair handoff puts a live "Upgrade Now"
                button in front of MonitoringCredentials, which used to be terminal. Any exit from the
                install that button starts (abort, cancel, fatal, version block) transitions away, and
                the typed monitoring login would then be silently discarded and the INSTALLER account --
                typically sysadmin -- persisted as the ongoing monitoring credential instead. Keying on
                what was entered rather than on a transient state cannot drift that way.
                */
                if (UseSameCredsCheckBox.IsChecked == false &&
                    !string.IsNullOrWhiteSpace(MonitorUsernameTextBox.Text))
                {
                    Username = MonitorUsernameTextBox.Text.Trim();
                    Password = MonitorPasswordBox.Password;
                }
                else
                {
                    Username = UsernameTextBox.Text.Trim();
                    Password = PasswordBox.Password;
                }
            }

            // Use server name as display name if not provided
            var displayName = string.IsNullOrWhiteSpace(DisplayNameTextBox.Text)
                ? ServerNameTextBox.Text.Trim()
                : DisplayNameTextBox.Text.Trim();

            if (_isEditMode)
            {
                ServerConnection.DisplayName = displayName;
                ServerConnection.ServerName = ServerNameTextBox.Text.Trim();
                ServerConnection.AuthenticationType = authenticationType;
                ServerConnection.AzureClientId = azureClientId;
                ServerConnection.ManagedIdentityClientId = managedIdentityClientId;
                ServerConnection.Description = DescriptionTextBox.Text.Trim();
                ServerConnection.IsFavorite = IsFavoriteCheckBox.IsChecked == true;
                ServerConnection.EncryptMode = GetSelectedEncryptMode();
                ServerConnection.TrustServerCertificate = TrustServerCertificateCheckBox.IsChecked == true;
                ServerConnection.ReadOnlyIntent = ReadOnlyIntentCheckBox.IsChecked == true;
                ServerConnection.MultiSubnetFailover = MultiSubnetFailoverCheckBox.IsChecked == true;
                if (decimal.TryParse(MonthlyCostTextBox.Text, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var editCost) && editCost >= 0)
                    ServerConnection.MonthlyCostUsd = editCost;
                ServerConnection.AlertDeliveryModeOverride = GetSelectedDeliveryOverride();
            }
            else
            {
                decimal monthlyCost = 0m;
                if (decimal.TryParse(MonthlyCostTextBox.Text, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var newCost) && newCost >= 0)
                    monthlyCost = newCost;

                ServerConnection = new ServerConnection
                {
                    DisplayName = displayName,
                    ServerName = ServerNameTextBox.Text.Trim(),
                    AuthenticationType = authenticationType,
                    AzureClientId = azureClientId,
                    ManagedIdentityClientId = managedIdentityClientId,
                    Description = DescriptionTextBox.Text.Trim(),
                    IsFavorite = IsFavoriteCheckBox.IsChecked == true,
                    CreatedDate = DateTime.Now,
                    LastConnected = DateTime.Now,
                    EncryptMode = GetSelectedEncryptMode(),
                    TrustServerCertificate = TrustServerCertificateCheckBox.IsChecked == true,
                    ReadOnlyIntent = ReadOnlyIntentCheckBox.IsChecked == true,
                    MultiSubnetFailover = MultiSubnetFailoverCheckBox.IsChecked == true,
                    MonthlyCostUsd = monthlyCost,
                    AlertDeliveryModeOverride = GetSelectedDeliveryOverride()
                };
            }

            DialogResult = true;
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            _installCts?.Cancel();
            DialogResult = false;
            Close();
        }

    }
}
