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
