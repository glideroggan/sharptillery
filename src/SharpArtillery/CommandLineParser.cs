using System;
using System.Collections.Generic;
using System.CommandLine;
using System.CommandLine.Parsing;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using SharpArtillery.Configs;
using SharpArtillery.Models;
using SharpArtillery.YamlConfig;

namespace SharpArtillery;

internal static class CommandLineParser
{
    public static RootCommand BuildRootCommand(IHost host)
    {
        // Define all options with short and long forms
        var targetOption = new Option<string?>("--target", "-t")
        {
            Description = "Target URL (e.g., https://example.com/api/endpoint)"
        };

        var clientsOption = new Option<int>("--clients", "-c")
        {
            Description = "Virtual users (parallel clients)",
            DefaultValueFactory = _ => 1
        };

        var maxRequestsOption = new Option<int?>("--requests", "-n")
        {
            Description = "Max number of requests to send"
        };

        var durationOption = new Option<TimeSpan?>("--duration", "-d")
        {
            Description = "Duration to run (format: HH:MM:SS)"
        };

        var timeoutOption = new Option<TimeSpan>("--timeout", "-T")
        {
            Description = "Per-request timeout (format: HH:MM:SS)",
            DefaultValueFactory = _ => TimeSpan.FromSeconds(5)
        };

        var methodOption = new Option<string?>("--method", "-m")
        {
            Description = "HTTP method: GET, POST, PUT"
        };

        var bodyOption = new Option<string?>("--body", "-b")
        {
            Description = "JSON body content"
        };

        var headerOption = new Option<string[]>("--header", "-H")
        {
            Description = "Add header (format: Name:Value). Can be used multiple times.",
            AllowMultipleArgumentsPerToken = true
        };

        var outputOption = new Option<string?>("--output", "-o")
        {
            Description = "Output report name (without extension)"
        };

        var extensionOption = new Option<string>("--extension", "-e")
        {
            Description = "Report type: html",
            DefaultValueFactory = _ => "html"
        };

        var yamlOption = new Option<string?>("--yaml", "-y")
        {
            Description = "YAML config file path"
        };

        var rpsOption = new Option<int?>("--rps", "-r")
        {
            Description = "Target requests per second (distributed smoothly across clients with jitter)"
        };

        var autoTuneOption = new Option<bool>("--autotune", "-A")
        {
            Description = "Auto-tune to find max sustainable RPS (duration mode only)"
        };

        var autoTuneMaxOption = new Option<int>("--autotune-max", "-M")
        {
            Description = "Auto-tune max clients",
            DefaultValueFactory = _ => 256
        };

        var autoTuneStepOption = new Option<int>("--autotune-step", "-S")
        {
            Description = "Auto-tune client step increment",
            DefaultValueFactory = _ => 5
        };

        var autoTuneIntervalOption = new Option<TimeSpan>("--autotune-interval", "-I")
        {
            Description = "Auto-tune sampling interval",
            DefaultValueFactory = _ => TimeSpan.FromSeconds(5)
        };

        var autoTuneErrorOption = new Option<float>("--autotune-error", "-E")
        {
            Description = "Auto-tune max error ratio (e.g., 0.02 for 2%)",
            DefaultValueFactory = _ => 0.02f
        };

        // Build root command
        var rootCommand = new RootCommand("Sharptillery - HTTP load testing tool");
        rootCommand.Options.Add(targetOption);
        rootCommand.Options.Add(clientsOption);
        rootCommand.Options.Add(maxRequestsOption);
        rootCommand.Options.Add(durationOption);
        rootCommand.Options.Add(timeoutOption);
        rootCommand.Options.Add(methodOption);
        rootCommand.Options.Add(bodyOption);
        rootCommand.Options.Add(headerOption);
        rootCommand.Options.Add(outputOption);
        rootCommand.Options.Add(extensionOption);
        rootCommand.Options.Add(yamlOption);
        rootCommand.Options.Add(rpsOption);
        rootCommand.Options.Add(autoTuneOption);
        rootCommand.Options.Add(autoTuneMaxOption);
        rootCommand.Options.Add(autoTuneStepOption);
        rootCommand.Options.Add(autoTuneIntervalOption);
        rootCommand.Options.Add(autoTuneErrorOption);

        // Add validators
        AddValidators(rootCommand, targetOption, maxRequestsOption, durationOption, yamlOption, autoTuneOption);

        // Set the action handler
        rootCommand.SetAction(async (parseResult, cancellationToken) =>
        {
            var config = BuildConfig(
                parseResult,
                targetOption,
                clientsOption,
                maxRequestsOption,
                durationOption,
                timeoutOption,
                methodOption,
                bodyOption,
                headerOption,
                outputOption,
                extensionOption,
                yamlOption,
                rpsOption,
                autoTuneOption,
                autoTuneMaxOption,
                autoTuneStepOption,
                autoTuneIntervalOption,
                autoTuneErrorOption);

            var mainService = host.Services.GetRequiredService<MainService>();
            await mainService.Run(config);
        });

        return rootCommand;
    }

    private static void AddValidators(
        RootCommand rootCommand,
        Option<string?> targetOption,
        Option<int?> maxRequestsOption,
        Option<TimeSpan?> durationOption,
        Option<string?> yamlOption,
        Option<bool> autoTuneOption)
    {
        // Mutual exclusion: -n and -d cannot be used together
        rootCommand.Validators.Add(result =>
        {
            var hasRequests = result.GetValue(maxRequestsOption) != null;
            var hasDuration = result.GetValue(durationOption) != null;
            if (hasRequests && hasDuration)
            {
                result.AddError("Cannot use -n/--requests and -d/--duration together. Choose one.");
            }
        });

        // Require one of -n, -d, or -y
        rootCommand.Validators.Add(result =>
        {
            var hasRequests = result.GetValue(maxRequestsOption) != null;
            var hasDuration = result.GetValue(durationOption) != null;
            var hasYaml = !string.IsNullOrEmpty(result.GetValue(yamlOption));
            if (!hasRequests && !hasDuration && !hasYaml)
            {
                result.AddError("Must specify -n/--requests, -d/--duration, or -y/--yaml.");
            }
        });

        // Target required unless YAML is provided
        rootCommand.Validators.Add(result =>
        {
            var target = result.GetValue(targetOption);
            var yaml = result.GetValue(yamlOption);
            if (string.IsNullOrEmpty(target) && string.IsNullOrEmpty(yaml))
            {
                result.AddError("Target URL (-t/--target) is required unless using YAML config (-y/--yaml).");
            }
        });

        // Validate target URL format
        rootCommand.Validators.Add(result =>
        {
            var target = result.GetValue(targetOption);
            if (!string.IsNullOrEmpty(target))
            {
                if (!Uri.TryCreate(target, UriKind.Absolute, out var uri))
                {
                    result.AddError($"Invalid target URL: '{target}'. Expected http(s)://host/path.");
                }
                else if (!string.Equals(uri.Scheme, "http", StringComparison.OrdinalIgnoreCase) &&
                         !string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase))
                {
                    result.AddError($"Invalid URL scheme: '{uri.Scheme}'. Use http or https.");
                }
            }
        });

        // Auto-tune requires duration mode
        rootCommand.Validators.Add(result =>
        {
            var autoTune = result.GetValue(autoTuneOption);
            var hasDuration = result.GetValue(durationOption) != null;
            var hasRequests = result.GetValue(maxRequestsOption) != null;
            if (autoTune && !hasDuration)
            {
                result.AddError("Auto-tune (-A/--autotune) requires duration mode (-d/--duration).");
            }
            if (autoTune && hasRequests)
            {
                result.AddError("Auto-tune (-A/--autotune) is not supported with max requests (-n/--requests).");
            }
        });
    }

    private static ArtilleryConfig BuildConfig(
        ParseResult parseResult,
        Option<string?> targetOption,
        Option<int> clientsOption,
        Option<int?> maxRequestsOption,
        Option<TimeSpan?> durationOption,
        Option<TimeSpan> timeoutOption,
        Option<string?> methodOption,
        Option<string?> bodyOption,
        Option<string[]> headerOption,
        Option<string?> outputOption,
        Option<string> extensionOption,
        Option<string?> yamlOption,
        Option<int?> rpsOption,
        Option<bool> autoTuneOption,
        Option<int> autoTuneMaxOption,
        Option<int> autoTuneStepOption,
        Option<TimeSpan> autoTuneIntervalOption,
        Option<float> autoTuneErrorOption)
    {
        var config = new ArtilleryConfig
        {
            Target = parseResult.GetValue(targetOption),
            Vu = parseResult.GetValue(clientsOption),
            MaxRequests = parseResult.GetValue(maxRequestsOption),
            Duration = parseResult.GetValue(durationOption),
            RequestTimeout = parseResult.GetValue(timeoutOption),
            Method = parseResult.GetValue(methodOption),
            Yaml = parseResult.GetValue(yamlOption),
            ConstantRps = parseResult.GetValue(rpsOption),
            AutoTuneMaxRps = parseResult.GetValue(autoTuneOption),
            AutoTuneMaxClients = parseResult.GetValue(autoTuneMaxOption),
            AutoTuneStep = parseResult.GetValue(autoTuneStepOption),
            AutoTuneInterval = parseResult.GetValue(autoTuneIntervalOption),
            AutoTuneMaxErrorRatio = parseResult.GetValue(autoTuneErrorOption)
        };

        // Parse JSON body
        var bodyStr = parseResult.GetValue(bodyOption);
        if (!string.IsNullOrEmpty(bodyStr))
        {
            bodyStr = bodyStr.Replace('\'', '"');
            config.JsonContent = JsonSerializer.Deserialize<object>(bodyStr);
        }

        // Parse headers
        var headers = parseResult.GetValue(headerOption);
        if (headers != null)
        {
            foreach (var header in headers)
            {
                var colonIndex = header.IndexOf(':');
                if (colonIndex > 0)
                {
                    var name = header[..colonIndex].Trim();
                    var value = header[(colonIndex + 1)..].Trim();
                    config.Headers[name] = value;
                }
            }
        }

        // Parse output settings
        var output = parseResult.GetValue(outputOption);
        var extension = parseResult.GetValue(extensionOption);
        if (!string.IsNullOrEmpty(output))
        {
            config.ReportSettings.Enabled = true;
            config.ReportSettings.Name = output;
            config.ReportSettings.Extension = YamlHelper.GetReportExtension(extension ?? "html");
        }

        // Load YAML config if specified
        if (!string.IsNullOrEmpty(config.Yaml))
        {
            config.YamlConfig = YamlHelper.ReadYaml(config.Yaml);
            if (config.YamlConfig?.Settings?.Target != null)
            {
                config.Target = config.YamlConfig.Settings.Target;
            }
        }

        return config;
    }
}
