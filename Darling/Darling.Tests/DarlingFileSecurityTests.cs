/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using PerformanceMonitor.Darling.Service;
using Xunit;

namespace Darling.Tests;

/// <summary>
/// The DPAPI-credential ACL hardening (V8 security hardening, #1262). Windows-gated (ACLs are a
/// Windows concept). Proves the created files/dirs carry a PROTECTED DACL (inheritance stripped) with
/// no world-readable ACE (Everyone / Authenticated Users / Users), SYSTEM + Administrators + the
/// service account granted, INTERACTIVE granted only where the operator's Viewer must read
/// (admin/viewer credentials), and the trusted-owner guard accepting a file this process created.
/// </summary>
public sealed class DarlingFileSecurityTests
{
    private static readonly SecurityIdentifier s_system = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier s_administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier s_interactive = new(WellKnownSidType.InteractiveSid, null);
    private static readonly SecurityIdentifier s_everyone = new(WellKnownSidType.WorldSid, null);
    private static readonly SecurityIdentifier s_authenticatedUsers = new(WellKnownSidType.AuthenticatedUserSid, null);
    private static readonly SecurityIdentifier s_builtinUsers = new(WellKnownSidType.BuiltinUsersSid, null);

    [Fact]
    public void HardenFile_SuperuserCredential_LocksOutWorldAndInteractive()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "ACLs are Windows-only.");

        var path = Path.Combine(Path.GetTempPath(), "darling-acl-owner-" + Guid.NewGuid().ToString("N") + ".dpapi");
        File.WriteAllText(path, "blob");
        try
        {
            DarlingFileSecurity.HardenFile(path, allowInteractiveRead: false);

            var rules = ReadRules(new FileInfo(path).GetAccessControl());
            AssertProtectedAndNoWorldRead(new FileInfo(path).GetAccessControl(), rules);

            /* SYSTEM + Administrators present; INTERACTIVE absent (the Viewer never reads the superuser cred). */
            Assert.Contains(rules, r => r.sid.Equals(s_system));
            Assert.Contains(rules, r => r.sid.Equals(s_administrators));
            Assert.DoesNotContain(rules, r => r.sid.Equals(s_interactive));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void HardenFile_RoleCredential_GrantsInteractiveRead()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "ACLs are Windows-only.");

        var path = Path.Combine(Path.GetTempPath(), "darling-acl-admin-" + Guid.NewGuid().ToString("N") + ".dpapi");
        File.WriteAllText(path, "blob");
        try
        {
            DarlingFileSecurity.HardenFile(path, allowInteractiveRead: true);

            var security = new FileInfo(path).GetAccessControl();
            var rules = ReadRules(security);
            AssertProtectedAndNoWorldRead(security, rules);

            /* The operator's Viewer (interactive) can READ, but INTERACTIVE gets no more than read. */
            var interactive = rules.Where(r => r.sid.Equals(s_interactive)).ToList();
            Assert.NotEmpty(interactive);
            Assert.All(interactive, r => Assert.True(
                (r.rights & FileSystemRights.Read) == FileSystemRights.Read
                && (r.rights & FileSystemRights.Write) == 0,
                $"INTERACTIVE should be read-only, was {r.rights}"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void HardenDirectory_StripsInheritance_AndGrantsInteractiveTraverseOnly()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "ACLs are Windows-only.");

        var path = Path.Combine(Path.GetTempPath(), "darling-acl-dir-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        try
        {
            DarlingFileSecurity.HardenDirectory(path, allowInteractiveTraverse: true);

            var security = new DirectoryInfo(path).GetAccessControl();
            var rules = ReadRules(security);
            AssertProtectedAndNoWorldRead(security, rules);

            Assert.Contains(rules, r => r.sid.Equals(s_system));
            Assert.Contains(rules, r => r.sid.Equals(s_administrators));

            /* INTERACTIVE gets traverse (execute) but NOT list/read-data, and only on this folder. */
            var interactive = rules.Where(r => r.sid.Equals(s_interactive)).ToList();
            Assert.NotEmpty(interactive);
            Assert.All(interactive, r =>
            {
                Assert.True((r.rights & FileSystemRights.ExecuteFile) == FileSystemRights.ExecuteFile, "traverse missing");
                Assert.True((r.rights & FileSystemRights.ListDirectory) == 0, "should not grant list");
                Assert.Equal(InheritanceFlags.None, r.inheritance);
            });
        }
        finally
        {
            Directory.Delete(path, recursive: true);
        }
    }

    [Fact]
    public void IsTrustedOwner_TrueForAFileThisProcessCreated()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "ACLs are Windows-only.");

        var path = Path.Combine(Path.GetTempPath(), "darling-acl-owner-check-" + Guid.NewGuid().ToString("N") + ".dpapi");
        File.WriteAllText(path, "blob");
        try
        {
            /* Created by this process => owned by the service account (this identity) => trusted. */
            Assert.True(DarlingFileSecurity.IsTrustedOwner(path));
        }
        finally
        {
            File.Delete(path);
        }

        /* A path that doesn't exist is not trusted (fails closed). */
        Assert.False(DarlingFileSecurity.IsTrustedOwner(path));
    }

    [Fact]
    public void HardenDirectory_OnTheCredentialParent_LeavesTheSiblingLogDirectoryWritable()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "ACLs are Windows-only.");

        /* Mirrors the REAL on-disk layout. DarlingManagedPostgres hardens ParentOf(dataDirectory) —
           %ProgramData%\PerformanceMonitorDarling — which ALSO contains the service's own `logs`
           directory (a sibling of the `pg` data dir). #1581 asked whether that credential lockdown is
           what silenced file logging in the field. It is NOT: HardenDirectory grants
           WindowsIdentity.GetCurrent().User — the identity the service is actually running as — Full
           Control with ContainerInherit|ObjectInherit, so `logs` INHERITS write access and the running
           service can never lock itself out. Proven with real I/O, not just ACE inspection, because
           that is the claim that matters. A field ACL failure therefore means something EXTERNAL
           re-ACL'd the path (on the field box, a SYSTEM-context SSM operation on one log file), not
           that the hardening scope is wrong — so DO NOT "fix" this by narrowing the scope off the
           parent, which would drop the inherited protection from the data dir subtree (server.key). */
        var parent = Path.Combine(Path.GetTempPath(), "darling-acl-parent-" + Guid.NewGuid().ToString("N"));
        var logs = Path.Combine(parent, "logs");
        Directory.CreateDirectory(logs);
        var existingLog = Path.Combine(logs, "darling-service_existing.log");
        File.WriteAllText(existingLog, "before\n");
        try
        {
            /* Exactly what DarlingManagedPostgres does before initdb. */
            DarlingFileSecurity.HardenDirectory(parent, allowInteractiveTraverse: true);

            /* 1. An already-open-style log file stays APPENDABLE (the flush path). */
            File.AppendAllText(existingLog, "after\n");
            Assert.Equal("before\nafter\n", File.ReadAllText(existingLog));

            /* 2. A NEW log file — the daily-rotation case — can still be created and written. */
            var rotatedLog = Path.Combine(logs, "darling-service_rotated.log");
            File.WriteAllText(rotatedLog, "rotated\n");
            Assert.Equal("rotated\n", File.ReadAllText(rotatedLog));

            /* 3. A subdirectory created AFTER hardening — the initdb data-dir case — is writable, which
                  is the whole reason the parent is hardened first (the subtree inherits). */
            var dataDir = Path.Combine(parent, "pg");
            Directory.CreateDirectory(dataDir);
            var serverKey = Path.Combine(dataDir, "server.key");
            File.WriteAllText(serverKey, "key\n");
            Assert.Equal("key\n", File.ReadAllText(serverKey));

            /* 4. The lockdown DID reach both (no world-readable principal survives) — the log directory
                  really is swept into the credential ACL; it is simply still writable by the service. */
            foreach (var swept in new[] { logs, dataDir })
            {
                var rules = ReadRules(new DirectoryInfo(swept).GetAccessControl());
                Assert.DoesNotContain(rules, r => r.sid.Equals(s_everyone));
                Assert.DoesNotContain(rules, r => r.sid.Equals(s_authenticatedUsers));
                Assert.DoesNotContain(rules, r => r.sid.Equals(s_builtinUsers));
            }
        }
        finally
        {
            Directory.Delete(parent, recursive: true);
        }
    }

    private static (SecurityIdentifier sid, FileSystemRights rights, InheritanceFlags inheritance)[] ReadRules(
        FileSystemSecurity security)
    {
        return security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .Where(r => r.AccessControlType == AccessControlType.Allow)
            .Select(r => ((SecurityIdentifier)r.IdentityReference, r.FileSystemRights, r.InheritanceFlags))
            .ToArray();
    }

    private static void AssertProtectedAndNoWorldRead(
        FileSystemSecurity security, (SecurityIdentifier sid, FileSystemRights rights, InheritanceFlags inheritance)[] rules)
    {
        /* Inheritance stripped: the ACL is exactly what we set, nothing inherited from %ProgramData%. */
        Assert.True(security.AreAccessRulesProtected, "DACL must be protected (inheritance disabled)");

        /* No world-readable principal survives. */
        Assert.DoesNotContain(rules, r => r.sid.Equals(s_everyone));
        Assert.DoesNotContain(rules, r => r.sid.Equals(s_authenticatedUsers));
        Assert.DoesNotContain(rules, r => r.sid.Equals(s_builtinUsers));
    }
}
