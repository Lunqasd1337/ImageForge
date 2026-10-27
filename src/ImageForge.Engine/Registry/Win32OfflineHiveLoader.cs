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

    public ILoadedOfflineHive Load(OfflineHive hive, string hiveFilePath)
    {
        if (!File.Exists(hiveFilePath))
        {
            throw new FileNotFoundException("Offline hive file not found.", hiveFilePath);
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
            if (value.Type != RegistryValueType.Sz)
            {
                throw new NotSupportedException($"Registry value type {value.Type} is not supported yet.");
            }

            using var key = OpenPath(subKey, create: true)!;
            var bytes = MemoryMarshal.AsBytes((value.Data + "\0").AsSpan());
            Check(PInvoke.RegSetValueEx(key, valueName, (REG_VALUE_TYPE)RegistryValueType.Sz, bytes), $"RegSetValueEx {valueName}");
            Check(PInvoke.RegFlushKey(key), "RegFlushKey");
        }

        /// <summary>Reads a REG_SZ value; null if the key or value does not exist.</summary>
        public string? ReadString(string subKey, string valueName)
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

            Check(error, $"RegQueryValueEx {valueName}");
            if (type != REG_VALUE_TYPE.REG_SZ)
            {
                throw new InvalidDataException($"Value {valueName} has type {type}, expected REG_SZ.");
            }

            var buffer = new byte[size];
            Check(PInvoke.RegQueryValueEx(key, valueName, out type, buffer, ref size), $"RegQueryValueEx {valueName}");

            return MemoryMarshal.Cast<byte, char>(buffer.AsSpan(0, (int)size)).ToString().TrimEnd('\0');
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
        /// as keys instead of following them, and refuses any segment that is a symbolic link.
        /// </summary>
        private SafeRegistryHandle? OpenPath(string subKey, bool create)
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
                        error = PInvoke.RegOpenKeyEx(current, segment, (uint)REG_OPEN_CREATE_OPTIONS.REG_OPTION_OPEN_LINK, REG_SAM_FLAGS.KEY_READ, out next);
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
