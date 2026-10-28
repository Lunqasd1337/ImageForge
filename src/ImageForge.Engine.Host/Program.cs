using ImageForge.Core.Hosting;
using ImageForge.Engine.Hosting;
using ImageForge.Engine.Host;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;

const int InvalidArgumentsExitCode = 2;
const int ClientVerificationExitCode = 3;

EngineLaunchArguments launch;
try
{
    launch = EngineLaunchArguments.Parse(args);
}
catch (FormatException)
{
    return InvalidArgumentsExitCode;
}

var logDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ImageForge", "logs");
Log.Logger = new LoggerConfiguration()
    .WriteTo.File(Path.Combine(logDirectory, "engine-.log"), rollingInterval: RollingInterval.Day, retainedFileCountLimit: 14, formatProvider: null)
    .CreateLogger();

try
{
    ClientProcess client;
    try
    {
        client = ClientProcess.Open(launch.ClientProcessId, launch.ClientStartTimeUtc);
    }
    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
    {
        Log.Error(ex, "Client process verification failed for PID {Pid}", launch.ClientProcessId);
        return ClientVerificationExitCode;
    }

    using (client)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSerilog();
        // A DISM step in progress is never killed; stopping waits for it and for the cleanup that follows.
        builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromMinutes(30));
        builder.Services.AddSingleton(launch);
        builder.Services.AddSingleton(client);
        builder.Services.AddHostedService<EnginePipeServer>();
        using var host = builder.Build();
        await host.RunAsync().ConfigureAwait(false);
    }

    return 0;
}
finally
{
    await Log.CloseAndFlushAsync().ConfigureAwait(false);
}
