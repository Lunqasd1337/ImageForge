using System.ComponentModel;
using System.Runtime.InteropServices;
using ImageForge.Core.Changes;
using ImageForge.Core.Execution;
using ImageForge.Core.Registry;
using ImageForge.Engine.Win32;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Registry;

namespace ImageForge.Engine.Registry;

/// <summary>
/// Loads offline hive files under HKLM with an owned, per-run name (RegLoadKey) and writes only through
/// handles opened beneath that loaded root. Registry symbolic links are never followed.
/// </summary>
public sealed class Win32OfflineHiveLoader(Guid runId) : IOfflineHiveLoader
{
    private const int UnloadAttempts = 5;

    /// <summary>Predefined HKEY_LOCAL_MACHINE; never closed.</summary>
    private static readonly SafeRegistryHandle LocalMachine = new(unchecked((nint)0x80000002), ownsHandle: false);
    private static readonly TimeSpan UnloadRetryDelay = TimeSpan.FromMilliseconds(500);

    public bool HiveFileExists(string hiveFilePath) => File.Exists(hiveFilePath);

    public ILoadedOfflineHive Load(OfflineHive hive, string hiveFilePath)
    {
        if (!File.Exists(hiveFilePath))
        {
            throw new FileNotFoundException($"Offline hive file not found: {hiveFilePath}", hiveFilePath);
        }

        var name = HiveLoadName.Create(runId, hive);
        using (PrivilegeScope.ForHiveLoading())
        {
            Check(PInvoke.RegLoadKey(LocalMachine, name, hiveFilePath), $"RegLoadKey {name}");
        }

        return new LoadedHive(hive, name);
    }

    /// <summary>Unloads hives a run left loaded under HKLM; only names created for <paramref name="runId"/> are touched.</summary>
    public static IReadOnlyList<string> UnloadOwned(Guid runId)
    {
        var prefix = $"ImageForge_{runId:N}_";
        var names = Microsoft.Win32.Registry.LocalMachine.GetSubKeyNames()
            .Where(n => HiveLoadName.IsOwned(n) && n.StartsWith(prefix, StringComparison.Ordinal))
            .ToList();
        if (names.Count == 0)
        {
            return names;
        }

        using (PrivilegeScope.ForHiveLoading())
        {
            foreach (var name in names)
            {
                Check(PInvoke.RegUnLoadKey(LocalMachine, name), $"RegUnLoadKey {name}");
            }
        }

        return names;
    }

    private static void Check(WIN32_ERROR error, string operation)
    {
        if (error != WIN32_ERROR.ERROR_SUCCESS)
        {
            throw new Win32Exception((int)error, $"{operation} failed: {new Win32Exception((int)error).Message}");
        }
    }

    public sealed class LoadedHive : ILoadedOfflineHive
    {
        private const string SymbolicLinkValueName = "SymbolicLinkValue";
        private bool _unloaded;

        internal LoadedHive(OfflineHive hive, string loadName)
        {
            Hive = hive;
            LoadName = loadName;
        }

        public OfflineHive Hive { get; }

        public string LoadName { get; }

        public void SetValue(string subKey, string valueName, RegistryValue value)
        {
            ArgumentNullException.ThrowIfNull(value);
            using var key = OpenPath(subKey, create: true)!;
            Check(PInvoke.RegSetValueEx(key, valueName, (REG_VALUE_TYPE)(uint)value.Type, value.Bytes), $"RegSetValueEx {valueName}");
            Check(PInvoke.RegFlushKey(key), "RegFlushKey");
        }

        public void DeleteValue(string subKey, string valueName)
        {
            using var key = OpenPath(subKey, create: false, writable: true);
            if (key is null)
            {
                return;
            }

            var error = PInvoke.RegDeleteValue(key, valueName);
            if (error != WIN32_ERROR.ERROR_FILE_NOT_FOUND)
            {
                Check(error, $"RegDeleteValue {valueName}");
                Check(PInvoke.RegFlushKey(key), "RegFlushKey");
            }
        }

        public void CreateKey(string subKey)
        {
            using var key = OpenPath(subKey, create: true)!;
            Check(PInvoke.RegFlushKey(key), "RegFlushKey");
        }

        /// <summary>
        /// Deletes the key and its subtree depth-first, never through a symbolic link: a tree that contains one is
        /// refused, because following it could delete keys outside the loaded hive.
        /// </summary>
        public void DeleteKeyTree(string subKey)
        {
            if (subKey.Length == 0)
            {
                throw new InvalidOperationException("Deleting the root of a hive is not allowed.");
            }

            var split = subKey.LastIndexOf('\\');
            var parentPath = split < 0 ? string.Empty : subKey[..split];
            if (!KeyExists(parentPath))
            {
                return;
            }

            // The parent exists, so opening it for writing creates nothing.
            using var parent = OpenPath(parentPath, create: true)!;

            DeleteRecursive(parent, split < 0 ? subKey : subKey[(split + 1)..]);
            Check(PInvoke.RegFlushKey(parent), "RegFlushKey");
        }

        public uint? ReadSelectCurrent() =>
            ReadValue("Select", "Current") is { Type: RegistryValueType.Dword, Bytes.Length: 4 } value
                ? BitConverter.ToUInt32(value.Bytes)
                : null;

        /// <summary>Reads a value with its exact type and bytes; null if the key or value does not exist.</summary>
        public RegistryValue? ReadValue(string subKey, string valueName)
        {
            using var key = OpenPath(subKey, create: false);
            if (key is null)
            {
                return null;
            }

            uint size = 0;
            var error = PInvoke.RegQueryValueEx(key, valueName, out var type, Span<byte>.Empty, ref size);
            if (error == WIN32_ERROR.ERROR_FILE_NOT_FOUND)
            {
                return null;
            }

            if (error != WIN32_ERROR.ERROR_MORE_DATA)
            {
                Check(error, $"RegQueryValueEx {valueName}");
            }

            var buffer = new byte[size];
            Check(PInvoke.RegQueryValueEx(key, valueName, out type, buffer, ref size), $"RegQueryValueEx {valueName}");
            return new RegistryValue((RegistryValueType)(uint)type, buffer[..(int)size]);
        }

        /// <summary>Reads a REG_SZ value; null if the key or value does not exist.</summary>
        public string? ReadString(string subKey, string valueName)
        {
            var value = ReadValue(subKey, valueName);
            return value is null
                ? null
                : value.Type == RegistryValueType.Sz ? value.Text : throw new InvalidDataException($"Value {valueName} has type {value.TypeName}, expected REG_SZ.");
        }

        public bool KeyExists(string subKey)
        {
            using var key = OpenPath(subKey, create: false);
            return key is not null;
        }

        private static void DeleteRecursive(SafeRegistryHandle parent, string name)
        {
            const REG_SAM_FLAGS Delete = (REG_SAM_FLAGS)0x00010000;
            var error = PInvoke.RegOpenKeyEx(
                parent, name, (uint)REG_OPEN_CREATE_OPTIONS.REG_OPTION_OPEN_LINK, REG_SAM_FLAGS.KEY_READ | REG_SAM_FLAGS.KEY_WRITE | Delete, out var child);
            if (error == WIN32_ERROR.ERROR_FILE_NOT_FOUND)
            {
                return;
            }

            Check(error, $"Open {name}");
            using (child)
            {
                if (IsSymbolicLink(child))
                {
                    throw new UnauthorizedAccessException($"Registry key '{name}' is a symbolic link; deleting through it is not allowed.");
                }

                using var view = Microsoft.Win32.RegistryKey.FromHandle(new SafeRegistryHandle(child.DangerousGetHandle(), ownsHandle: false));
                foreach (var subKeyName in view.GetSubKeyNames())
                {
                    DeleteRecursive(child, subKeyName);
                }
            }

            // No subkeys are left and the key itself is not a link, so deleting it cannot reach outside the hive.
            using var parentView = Microsoft.Win32.RegistryKey.FromHandle(new SafeRegistryHandle(parent.DangerousGetHandle(), ownsHandle: false));
            parentView.DeleteSubKey(name, throwOnMissingSubKey: false);
        }

        public void Unload()
        {
            if (_unloaded)
            {
                return;
            }

            using (PrivilegeScope.ForHiveLoading())
            {
                for (var attempt = 1; ; attempt++)
                {
                    var error = PInvoke.RegUnLoadKey(LocalMachine, LoadName);
                    if (error == WIN32_ERROR.ERROR_SUCCESS)
                    {
                        _unloaded = true;
                        return;
                    }

                    if (attempt == UnloadAttempts)
                    {
                        Check(error, $"RegUnLoadKey {LoadName} (after {UnloadAttempts} attempts)");
                    }

                    Thread.Sleep(UnloadRetryDelay);
                }
            }
        }

        /// <summary>
        /// Opens (or creates) <paramref name="subKey"/> one segment at a time below the loaded root, opening links
        /// as keys instead of following them, and refuses any segment that is a symbolic link. Without
        /// <paramref name="create"/> the key is opened read-only unless <paramref name="writable"/> is set.
        /// </summary>
        private SafeRegistryHandle? OpenPath(string subKey, bool create, bool writable = false)
        {
            ObjectDisposedException.ThrowIf(_unloaded, this);

            var current = OpenRoot();
            try
            {
                var segments = subKey.Length == 0 ? [] : subKey.Split('\\');
                foreach (var segment in segments)
                {
                    SafeRegistryHandle next;
                    WIN32_ERROR error;
                    if (create)
                    {
                        error = PInvoke.RegCreateKeyEx(
                            current, segment, default,
                            REG_OPEN_CREATE_OPTIONS.REG_OPTION_NON_VOLATILE | REG_OPEN_CREATE_OPTIONS.REG_OPTION_OPEN_LINK,
                            REG_SAM_FLAGS.KEY_READ | REG_SAM_FLAGS.KEY_WRITE, null, out next);
                    }
                    else
                    {
                        var access = writable ? REG_SAM_FLAGS.KEY_READ | REG_SAM_FLAGS.KEY_WRITE : REG_SAM_FLAGS.KEY_READ;
                        error = PInvoke.RegOpenKeyEx(current, segment, (uint)REG_OPEN_CREATE_OPTIONS.REG_OPTION_OPEN_LINK, access, out next);
                        if (error == WIN32_ERROR.ERROR_FILE_NOT_FOUND)
                        {
                            current.Dispose();
                            return null;
                        }
                    }

                    Check(error, $"Open {segment}");
                    current.Dispose();
                    current = next;
                    if (IsSymbolicLink(current))
                    {
                        throw new UnauthorizedAccessException($"Registry key '{segment}' is a symbolic link; writing through it is not allowed.");
                    }
                }

                return current;
            }
            catch
            {
                current.Dispose();
                throw;
            }
        }

        private SafeRegistryHandle OpenRoot()
        {
            var error = PInvoke.RegOpenKeyEx(
                LocalMachine, LoadName, (uint)REG_OPEN_CREATE_OPTIONS.REG_OPTION_OPEN_LINK,
                REG_SAM_FLAGS.KEY_READ | REG_SAM_FLAGS.KEY_WRITE, out var root);
            Check(error, $"Open loaded hive {LoadName}");
            return root;
        }

        private static bool IsSymbolicLink(SafeRegistryHandle key)
        {
            uint size = 0;
            var error = PInvoke.RegQueryValueEx(key, SymbolicLinkValueName, out var type, Span<byte>.Empty, ref size);
            return error is WIN32_ERROR.ERROR_SUCCESS or WIN32_ERROR.ERROR_MORE_DATA && type == REG_VALUE_TYPE.REG_LINK;
        }
    }
}
