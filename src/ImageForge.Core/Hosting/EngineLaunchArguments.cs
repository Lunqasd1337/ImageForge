using System.Globalization;
using System.Text.RegularExpressions;

namespace ImageForge.Core.Hosting;

/// <summary>
/// Command line the UI passes to the elevated engine host: a per-launch random pipe name and the identity
/// (PID + process start time) of the UI process the engine must serve.
/// </summary>
public sealed partial record EngineLaunchArguments(string PipeName, int ClientProcessId, long ClientStartTimeUtc)
{
    private const string PipePrefix = "ImageForge.Engine.";

    public static EngineLaunchArguments CreateFor(int clientProcessId, long clientStartTimeUtc) =>
        new(PipePrefix + Guid.NewGuid().ToString("N"), clientProcessId, clientStartTimeUtc);

    public static bool IsValidPipeName(string pipeName) => PipeNamePattern().IsMatch(pipeName);

    public static EngineLaunchArguments Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Count % 2 != 0)
        {
            throw new FormatException("Arguments must be --name value pairs.");
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Count; i += 2)
        {
            var key = args[i];
            if (key is not ("--pipe" or "--client-pid" or "--client-start") || !values.TryAdd(key, args[i + 1]))
            {
                throw new FormatException($"Unexpected or repeated argument '{key}'.");
            }
        }

        if (values.Count != 3)
        {
            throw new FormatException("Missing engine launch argument.");
        }

        var pipe = values["--pipe"];
        if (!IsValidPipeName(pipe))
        {
            throw new FormatException("Invalid pipe name.");
        }

        if (!int.TryParse(values["--client-pid"], NumberStyles.None, CultureInfo.InvariantCulture, out var pid) || pid <= 0)
        {
            throw new FormatException("Invalid client process id.");
        }

        if (!long.TryParse(values["--client-start"], NumberStyles.None, CultureInfo.InvariantCulture, out var start) || start <= 0)
        {
            throw new FormatException("Invalid client start time.");
        }

        return new EngineLaunchArguments(pipe, pid, start);
    }

    public IReadOnlyList<string> ToArgumentList() =>
    [
        "--pipe", PipeName,
        "--client-pid", ClientProcessId.ToString(CultureInfo.InvariantCulture),
        "--client-start", ClientStartTimeUtc.ToString(CultureInfo.InvariantCulture),
    ];

    [GeneratedRegex(@"^ImageForge\.Engine\.[0-9a-f]{32}\z", RegexOptions.CultureInvariant)]
    private static partial Regex PipeNamePattern();
}
