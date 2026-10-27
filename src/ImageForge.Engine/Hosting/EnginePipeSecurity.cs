using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;

namespace ImageForge.Engine.Hosting;

public static class EnginePipeSecurity
{
    /// <summary>
    /// DACL for the engine pipe: the initiating UI user and SYSTEM may connect, network logons are denied.
    /// The initiator SID comes from the UI process token, not from the elevated account.
    /// </summary>
    public static PipeSecurity Create(SecurityIdentifier initiator)
    {
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        security.AddAccessRule(new PipeAccessRule(initiator, PipeAccessRights.ReadWrite, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        return security;
    }
}
