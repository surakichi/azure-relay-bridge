﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

#if _WINDOWS || _SYSTEMD || _LAUNCHD
namespace azbridge
{
    using Microsoft.Azure.Relay.Bridge.Configuration;
    using Microsoft.Extensions.DependencyInjection;
    using Microsoft.Extensions.Hosting;
    using Microsoft.Extensions.Logging;
    using Microsoft.Extensions.Logging.Configuration;
    using Microsoft.Extensions.Logging.Console;
    using Microsoft.Extensions.Logging.EventLog;
    using System;
    using System.Collections;
    using System.Configuration;
    using System.Diagnostics;
    using System.IO;
    using System.Reflection;
    using System.ServiceProcess;
    using System.Threading;
    using System.Threading.Tasks;

    static class ServiceLauncher
    {
        const string ServiceName = "azbridgesvc";
        const string DisplayName = "Azure Relay Bridge Service";

        /// <summary>
        /// The main entry point for the application.
        /// </summary>
        internal static async Task RunAsync(CommandLineSettings settings)
        {
            string svcConfigFileName =
                (Environment.OSVersion.Platform == PlatformID.Unix) ?
                    $"/etc/azbridge/azbridge_config.svc.yml" :
             Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                 $"Microsoft\\Azure Relay Bridge\\azbridge_config.svc.yml");

            if (File.Exists(svcConfigFileName))
            {
                settings.ConfigFile = svcConfigFileName;
            }
            else
            {
                svcConfigFileName = Path.Combine(AppContext.BaseDirectory, "azbridge_config.svc.yml");
                if (File.Exists(svcConfigFileName))
                {
                    settings.ConfigFile = svcConfigFileName;
                }
            }
            Config config = Config.LoadConfig(settings);

            LogLevel logLevel = LogLevel.Error;
            if (!settings.Quiet.HasValue || !settings.Quiet.Value)
            {
                if (config.LogLevel != null)
                {
                    switch (config.LogLevel.ToUpper())
                    {
                        case "QUIET":
                            logLevel = LogLevel.None;
                            break;
                        case "FATAL":
                            logLevel = LogLevel.Critical;
                            break;
                        case "ERROR":
                            logLevel = LogLevel.Error;
                            break;
                        case "INFO":
                            logLevel = LogLevel.Information;
                            break;
                        case "VERBOSE":
                            logLevel = LogLevel.Trace;
                            break;
                        case "DEBUG":
                        case "DEBUG1":
                        case "DEBUG2":
                        case "DEBUG3":
                            logLevel = LogLevel.Debug;
                            break;
                    }
                }
            }
            else
            {
                logLevel = LogLevel.None;
            }


            IHost host = Host.CreateDefaultBuilder()
#if _WINDOWS
                .UseWindowsService(options =>
                {
                    options.ServiceName = ServiceName;
                })
#elif _SYSTEMD
                .UseSystemd()
#elif _LAUNCHD
                .UseLaunchd()
#endif

                .ConfigureServices(services =>
                {
                    if (OperatingSystem.IsWindows())
                    {
                        LoggerProviderOptions.RegisterProviderOptions<EventLogSettings, EventLogLoggerProvider>(services);
                        services.Configure<EventLogSettings>(settings =>
                        {
                            if (OperatingSystem.IsWindows())
                            {
                                settings.SourceName = ServiceName;
                            }
                        });
                    }

                    services.AddSingleton(config);
                    services.AddHostedService<RelayBridgeService>();
                })
                .ConfigureLogging((context, logging) =>
                {
                    logging.SetMinimumLevel(logLevel);
                    if (!string.IsNullOrEmpty(config.LogFileName))
                    {
                        var fn = Path.GetFileNameWithoutExtension(config.LogFileName);
                        var dir = Path.GetDirectoryName(config.LogFileName);
                        var ext = Path.GetExtension(config.LogFileName);
                        var fileName = Path.Combine(dir, $"{fn}-{{Date}}{ext}");
                        logging.AddFile(fileName, logLevel);
                    }
                })
                .Build();

            await host.RunAsync();
        }

        /// <summary>
        /// Installs the service
        /// </summary>
        internal static void InstallService()
        {
            if (IsInstalled())
                return;
            var filePath = Path.Combine(AppContext.BaseDirectory, "azbridge.exe");
            ShellExecute(Environment.SystemDirectory + @"\sc.exe", "create \"" + ServiceName + "\" binPath= \"" + filePath + " -svc\" start= auto DisplayName= \"" + DisplayName + "\"");
        }

        internal static void UninstallService()
        {
            if (!IsInstalled())
                return;

            ShellExecute(Environment.SystemDirectory + @"\sc.exe", "delete \"" + ServiceName + "\"");
        }

        public static void ShellExecute(string command, string parameters)
        {
            var process = new Process
            {
                StartInfo = new ProcessStartInfo(command, parameters)
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            process.Start();
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                throw new Exception($"Process failed with exit code {process.ExitCode}");
            }
        }

        internal static bool IsInstalled()
        {
#if _WINDOWS
#pragma warning disable CA1416 // Validate platform compatibility
            using (ServiceController controller = new ServiceController(ServiceName))
            {
                try
                {
                    ServiceControllerStatus status = controller.Status;
                }
                catch
                {
                    return false;
                }
                return true;
            }
#pragma warning restore CA1416 // Validate platform compatibility
#else
             return true;
#endif
        }

        private static IHostBuilder UseLaunchd(this IHostBuilder hostBuilder)
        {
            hostBuilder.ConfigureServices(services =>
            {
                services.Configure<ConsoleLoggerOptions>(options =>
                {
                    options.FormatterName = ConsoleFormatterNames.Simple;
                });
                services.Configure<SimpleConsoleFormatterOptions>(options =>
                {
                    options.SingleLine = true;
                });
            });

            return hostBuilder;
        }
    }
}
#endif