using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Security;

namespace ImageForge.Engine.Win32;

/// <summary>
/// Enables process token privileges for the lifetime of the scope and restores the ones it changed.
/// Hive load/unload privileges are held only while needed (plan, section 5).
/// </summary>
internal sealed class PrivilegeScope : IDisposable
{
    private readonly SafeFileHandle _token;
    private readonly List<LUID> _enabledHere = [];

    private PrivilegeScope(SafeFileHandle token) => _token = token;

    public static PrivilegeScope ForHiveLoading() => Enable(PInvoke.SE_BACKUP_NAME, PInvoke.SE_RESTORE_NAME);

    public static PrivilegeScope Enable(params string[] privileges)
    {
        if (!PInvoke.OpenProcessToken(PInvoke.GetCurrentProcess_SafeHandle(), TOKEN_ACCESS_MASK.TOKEN_ADJUST_PRIVILEGES | TOKEN_ACCESS_MASK.TOKEN_QUERY, out var token))
        {
            throw new Win32Exception();
        }

        var scope = new PrivilegeScope(token);
        try
        {
            foreach (var privilege in privileges)
            {
                scope.EnableOne(privilege);
            }

            return scope;
        }
        catch
        {
            scope.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        foreach (var luid in _enabledHere)
        {
            Adjust(luid, enable: false, out _);
        }

        _enabledHere.Clear();
        _token.Dispose();
    }

    private void EnableOne(string privilege)
    {
        if (!PInvoke.LookupPrivilegeValue(null, privilege, out var luid))
        {
            throw new Win32Exception();
        }

        Adjust(luid, enable: true, out var changed);
        if (changed)
        {
            _enabledHere.Add(luid);
        }
    }

    private unsafe void Adjust(LUID luid, bool enable, out bool changed)
    {
        var state = new TOKEN_PRIVILEGES { PrivilegeCount = 1 };
        state.Privileges[0] = new LUID_AND_ATTRIBUTES
        {
            Luid = luid,
            Attributes = enable ? TOKEN_PRIVILEGES_ATTRIBUTES.SE_PRIVILEGE_ENABLED : 0,
        };
        var previous = default(TOKEN_PRIVILEGES);
        uint returned = 0;

        var hToken = (HANDLE)_token.DangerousGetHandle();
        if (!PInvoke.AdjustTokenPrivileges(hToken, false, &state, (uint)sizeof(TOKEN_PRIVILEGES), &previous, &returned))
        {
            throw new Win32Exception();
        }

        if ((WIN32_ERROR)Marshal.GetLastPInvokeError() == WIN32_ERROR.ERROR_NOT_ALL_ASSIGNED)
        {
            throw new UnauthorizedAccessException("The process token does not hold the privileges required to load registry hives.");
        }

        // PreviousState lists the privilege only if its state actually changed.
        changed = previous.PrivilegeCount == 1;
    }
}
