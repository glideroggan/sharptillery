using System;
using System.Collections.Generic;
using System.CommandLine;
using System.CommandLine.Parsing;
using System.Globalization;
using System.Text;
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
            Description = "Report type: html or csv",
            DefaultValueFactory = _ => "html"
        };

        var yamlOption = new Option<string?>("--yaml", "-y")
        {
            Description = "YAML config file path"
        };

        var rpsOption = new Option<int?>("--rps", "-r")
        {
            Description = "Target requests per second, on one schedule shared by the clients"
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
            DefaultValueFactory = _ => 0.02f,
            CustomParser = r => ParseFloat(r, "Auto-tune max error ratio (-E/--autotune-error)")
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
        AddValueValidators(rootCommand, methodOption, bodyOption, headerOption, extensionOption,
            autoTuneMaxOption, autoTuneStepOption, autoTuneIntervalOption, autoTuneErrorOption);

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
        // -n and -d count as given when typed, even with a value that cannot be parsed: the parser
        // reports that value itself, and these checks must not add "missing" on top of it.
        // Mutual exclusion: -n and -d cannot be used together
        rootCommand.Validators.Add(result =>
        {
            var hasRequests = Given(result, maxRequestsOption);
            var hasDuration = Given(result, durationOption);
            if (hasRequests && hasDuration)
            {
                result.AddError("Cannot use -n/--requests and -d/--duration together. Choose one.");
            }
        });

        // Require one of -n, -d, or -y
        rootCommand.Validators.Add(result =>
        {
            var hasRequests = Given(result, maxRequestsOption);
            var hasDuration = Given(result, durationOption);
            var hasYaml = !string.IsNullOrEmpty(ValueOrDefault(result, yamlOption));
            if (!hasRequests && !hasDuration && !hasYaml)
            {
                result.AddError("Must specify -n/--requests, -d/--duration, or -y/--yaml.");
            }
        });

        // Target required unless YAML is provided
        rootCommand.Validators.Add(result =>
        {
            var target = ValueOrDefault(result, targetOption);
            var yaml = ValueOrDefault(result, yamlOption);
            if (string.IsNullOrEmpty(target) && string.IsNullOrEmpty(yaml))
            {
                result.AddError("Target URL (-t/--target) is required unless using YAML config (-y/--yaml).");
            }
        });

        // Validate target URL format
        rootCommand.Validators.Add(result =>
        {
            var target = ValueOrDefault(result, targetOption);
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

        // The YAML file is read at parse time, so a key the run would ignore, or a file that cannot
        // be read, stops the command before any request is sent.
        rootCommand.Validators.Add(result =>
        {
            var yaml = ValueOrDefault(result, yamlOption);
            if (string.IsNullOrEmpty(yaml)) return;
            foreach (var error in YamlHelper.Load(yaml).Errors)
                result.AddError(error);
        });

        // Auto-tune requires duration mode
        rootCommand.Validators.Add(result =>
        {
            var autoTune = ValueOrDefault(result, autoTuneOption);
            var hasDuration = Given(result, durationOption);
            var hasRequests = Given(result, maxRequestsOption);
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

    /// <summary>
    /// The HTTP methods a request can use. <see cref="RequestTemplate"/> maps the same names.
    /// </summary>
    internal static readonly string[] HttpMethods = ["GET", "POST", "PUT", "DELETE", "PATCH", "HEAD", "OPTIONS"];

    /// <summary>
    /// Checks the value of each option before the run starts, so a bad value is a parse error
    /// instead of a crash or a silent fallback once the run is under way.
    /// </summary>
    private static void AddValueValidators(
        RootCommand rootCommand,
        Option<string?> methodOption,
        Option<string?> bodyOption,
        Option<string[]> headerOption,
        Option<string> extensionOption,
        Option<int> autoTuneMaxOption,
        Option<int> autoTuneStepOption,
        Option<TimeSpan> autoTuneIntervalOption,
        Option<float> autoTuneErrorOption)
    {
        rootCommand.Validators.Add(result =>
        {
            var method = ValueOrDefault(result, methodOption);
            if (method != null && Array.FindIndex(HttpMethods, m => string.Equals(m, method, StringComparison.OrdinalIgnoreCase)) < 0)
            {
                result.AddError($"Unsupported HTTP method: '{method}'. Use {string.Join(", ", HttpMethods)}.");
            }
        });

        rootCommand.Validators.Add(result =>
        {
            var body = ValueOrDefault(result, bodyOption);
            if (string.IsNullOrEmpty(body)) return;
            try
            {
                ParseBody(body);
            }
            catch (JsonException e)
            {
                result.AddError($"Body (-b/--body) is not valid JSON: {e.Message}");
            }
        });

        rootCommand.Validators.Add(result =>
        {
            foreach (var header in ValueOrDefault(result, headerOption) ?? Array.Empty<string>())
            {
                var error = HeaderError(header);
                if (error != null) result.AddError(error);
            }
        });

        rootCommand.Validators.Add(result =>
        {
            // The default ("html") is always valid, so only a value typed on the command line is checked.
            if (!Typed(result, extensionOption, out var extension)) return;
            if (!YamlHelper.IsSupportedReportType(extension!))
            {
                result.AddError($"Unsupported report type: '{extension}'. Use html or csv.");
            }
        });

        // The auto-tune defaults are valid, so each check runs only for a value typed on the command line:
        // a validator reads an option that was not typed as its zero value.
        rootCommand.Validators.Add(result =>
        {
            if (Typed(result, autoTuneMaxOption, out var maxClients) && maxClients < 1)
                result.AddError("Auto-tune max clients (-M/--autotune-max) must be at least 1.");
            if (Typed(result, autoTuneStepOption, out var step) && step < 1)
                result.AddError("Auto-tune step (-S/--autotune-step) must be at least 1.");
            if (Typed(result, autoTuneIntervalOption, out var interval) && interval <= TimeSpan.Zero)
                result.AddError("Auto-tune interval (-I/--autotune-interval) must be longer than zero.");

            // Written so that NaN, which compares false to everything, is rejected too.
            if (Typed(result, autoTuneErrorOption, out var error) && !(error >= 0f && error <= 1f))
                result.AddError("Auto-tune max error ratio (-E/--autotune-error) must be between 0 and 1.");
        });
    }

    /// <summary>
    /// True when the option was given on the command line, false when it is absent or its default is in use.
    /// </summary>
    private static bool Given(CommandResult result, Option option) =>
        result.GetResult(option) is OptionResult { Implicit: false };

    /// <summary>
    /// True, with the value, when the option was given on the command line and its value could be parsed.
    /// The parser converts a value only when it is read, so a value that cannot be parsed makes GetValue
    /// throw inside a validator; that value is skipped here, and the parser reports it as its own error.
    /// </summary>
    private static bool Typed<T>(CommandResult result, Option<T> option, out T? value)
    {
        value = default;
        if (!Given(result, option)) return false;
        try
        {
            value = result.GetValue(option);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// The typed value, or the type's default when the option was not typed or its value could not be parsed.
    /// </summary>
    private static T? ValueOrDefault<T>(CommandResult result, Option<T> option) =>
        Typed(result, option, out var value) ? value : default;

    /// <summary>
    /// Reads a float with a dot as the decimal separator, as the help text shows it, and falls back to
    /// the PC's own culture so a comma still works where that is the separator.
    /// </summary>
    private static float ParseFloat(ArgumentResult result, string label)
    {
        var token = result.Tokens.Count > 0 ? result.Tokens[^1].Value : "";
        if (float.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ||
            float.TryParse(token, NumberStyles.Float, CultureInfo.CurrentCulture, out value))
        {
            return value;
        }
        result.AddError($"{label} is not a number: '{token}'.");
        return default;
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

        // Parse JSON body (validated by AddValueValidators)
        var bodyStr = parseResult.GetValue(bodyOption);
        if (!string.IsNullOrEmpty(bodyStr))
        {
            config.JsonContent = ParseBody(bodyStr);
        }

        // Parse headers
        ParseHeaders(parseResult.GetValue(headerOption), config.Headers);

        // Load YAML config if specified (checked by AddValidators)
        if (!string.IsNullOrEmpty(config.Yaml))
        {
            config.YamlConfig = YamlHelper.ReadYaml(config.Yaml);
            if (config.YamlConfig?.Settings?.Target != null)
            {
                config.Target = config.YamlConfig.Settings.Target;
            }
        }

        // Parse output settings
        var output = parseResult.GetValue(outputOption);
        var typedExtension = parseResult.GetResult(extensionOption) is OptionResult { Implicit: false }
            ? parseResult.GetValue(extensionOption)
            : null;
        if (!string.IsNullOrEmpty(output))
        {
            config.ReportSettings.Enabled = true;
            config.ReportSettings.Name = output;
            config.ReportSettings.Extension = YamlHelper.GetReportExtension(ReportType(typedExtension, config.YamlConfig));
        }

        return config;
    }

    /// <summary>
    /// The report type: a typed <c>-e</c>, else the YAML <c>settings.report</c>, else html.
    /// </summary>
    internal static string ReportType(string? typedExtension, ConfigRoot? yaml) =>
        typedExtension ?? yaml?.Settings?.Report ?? "html";

    /// <summary>
    /// Adds each <c>Name:Value</c> header to <paramref name="target"/>, splitting at the first colon
    /// and trimming both parts. An entry without a colon or without a name throws, the same as
    /// <see cref="HeaderError"/> reports it at parse time.
    /// </summary>
    internal static void ParseHeaders(IEnumerable<string>? headers, Dictionary<string, string> target)
    {
        if (headers == null) return;

        foreach (var header in headers)
        {
            var error = HeaderError(header);
            if (error != null) throw new ArgumentException(error);

            var colonIndex = header.IndexOf(':');
            var name = header[..colonIndex].Trim();
            var value = header[(colonIndex + 1)..].Trim();
            target[name] = value;
        }
    }

    /// <summary>
    /// Why a <c>-H</c> value is not <c>Name:Value</c>, or null when it is.
    /// </summary>
    internal static string? HeaderError(string header)
    {
        var colonIndex = header.IndexOf(':');
        if (colonIndex < 0)
            return $"Header '{header}' has no colon. Use Name:Value.";
        if (header[..colonIndex].Trim().Length == 0)
            return $"Header '{header}' has no name before the colon. Use Name:Value.";
        return null;
    }

    /// <summary>
    /// Reads a <c>-b</c> value as JSON. Single quotes may stand for double quotes around strings,
    /// so a body is easy to type in a shell; only quotes outside a double-quoted string are
    /// rewritten, so an apostrophe inside a string (<c>"O'Brien"</c>) stays as typed.
    /// </summary>
    internal static object? ParseBody(string body) =>
        JsonSerializer.Deserialize<object>(SingleToDoubleQuotes(body));

    internal static string SingleToDoubleQuotes(string json)
    {
        var sb = new StringBuilder(json.Length);
        var inDouble = false;
        var inSingle = false;
        for (var i = 0; i < json.Length; i++)
        {
            var c = json[i];
            if (inDouble)
            {
                sb.Append(c);
                if (c == '\\' && i + 1 < json.Length)
                    sb.Append(json[++i]);
                else if (c == '"')
                    inDouble = false;
            }
            else if (inSingle)
            {
                if (c == '\\' && i + 1 < json.Length)
                {
                    var next = json[++i];
                    // \' is an apostrophe inside the string; any other escape is kept as it is.
                    if (next == '\'') sb.Append('\'');
                    else sb.Append('\\').Append(next);
                }
                else if (c == '\'')
                {
                    sb.Append('"');
                    inSingle = false;
                }
                else if (c == '"')
                {
                    // A double quote inside a single-quoted string must be escaped once it is one.
                    sb.Append("\\\"");
                }
                else
                {
                    sb.Append(c);
                }
            }
            else if (c == '"')
            {
                inDouble = true;
                sb.Append(c);
            }
            else if (c == '\'')
            {
                inSingle = true;
                sb.Append('"');
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }
}
