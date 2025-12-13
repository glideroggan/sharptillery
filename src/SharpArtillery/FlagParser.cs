using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using SharpArtillery.Configs;
using SharpArtillery.YamlConfig;

namespace SharpArtillery
{
    internal static class ProgramSettings
    {
        internal static ArtilleryConfig HandleConfigsAndSettings(string[] args)
        {
            // read arguments
            var parser = new FlagParser<ArtilleryConfig>()
                .AddFlagNoValue('v', (val, c) => 
                {
                    c.ShowInfo = true;
                    c.Quit = true;
                })
                .AddFlag('b', (val, c) =>
                {
                    val = val!.Replace('\'', '"');
                    c.JsonContent = JsonSerializer.Deserialize<object>(val);
                })
                .AddFlag('m', (val, c) => c.Method = val)
                .AddFlag('h', (val, c) =>
                {
                    var values = val!.Split(':');
                    c.Headers.Add(values[0], values[1]);
                })
                .AddFlag('n', (val, c) => c.MaxRequests = int.Parse(val!, CultureInfo.InvariantCulture))
                .AddFlag('r', (val, c) => c.ConstantRps = int.Parse(val!, CultureInfo.InvariantCulture),
                    'n')
                .AddFlag('d',
                    (val, c) => c.Duration = TimeSpan.Parse(val!, CultureInfo.InvariantCulture),
                    'n')
                .AddFlag('T',
                    (val, c) => c.RequestTimeout = TimeSpan.Parse(val!, CultureInfo.InvariantCulture))
                .AddFlagNoValue('A', (val, c) => c.AutoTuneMaxRps = true)
                .AddFlag('M', (val, c) => c.AutoTuneMaxClients = int.Parse(val!, CultureInfo.InvariantCulture))
                .AddFlag('S', (val, c) => c.AutoTuneStep = int.Parse(val!, CultureInfo.InvariantCulture))
                .AddFlag('I', (val, c) => c.AutoTuneInterval = TimeSpan.Parse(val!, CultureInfo.InvariantCulture))
                .AddFlag('E', (val, c) => c.AutoTuneMaxErrorRatio = float.Parse(val!, CultureInfo.InvariantCulture))
                .AddFlag('c', (val, c) => c.Clients = int.Parse(val!, CultureInfo.InvariantCulture))
                .AddFlag('t', (val, c) => c.Target = val)
                .AddFlag('o', (val, c) =>
                {
                    c.ReportSettings.Enabled = true;
                    c.ReportSettings.Name = val!;
                    c.ReportSettings.Extension = ".html";
                })
                .AddFlag('e', (val, c) => { c.ReportSettings.Extension = YamlHelper.GetReportExtension(val!); })
                .AddFlag('y', (val, c) => c.Yaml = val);

            
            var flags = parser.Parse(args, () => new ArtilleryConfig
            {
                ShowInfo = true,
                Quit = true
            });

            // read config file
            flags.YamlConfig = flags.Yaml != null ? YamlHelper.ReadYaml(flags.Yaml) : null;
            if (flags.YamlConfig != null)
            {
                flags.Target = flags.YamlConfig.Settings.Target;
            }

            if (!string.IsNullOrWhiteSpace(flags.Target))
                ValidateTarget(flags.Target);

            // var config = flags.YamlConfig;

            // if flags are good and no yaml, we should still be running
            // if (config != null || flags.Target != null) return flags;
            
            if (flags.ShowInfo)
                WriteOutDescription();
            return flags;

        }

        private static void ValidateTarget(string target)
        {
            if (!Uri.TryCreate(target, UriKind.Absolute, out var uri))
                throw new ArgumentException($"Invalid target URL: '{target}'. Expected http(s)://host[/path].");

            if (!string.Equals(uri.Scheme, "http", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException($"Invalid target URL scheme: '{uri.Scheme}'. Use http or https.");

            if (string.IsNullOrWhiteSpace(uri.Host))
                throw new ArgumentException($"Invalid target URL: '{target}'. Host is missing, did you mean 'https://example.com'?");
        }

        private static void WriteOutDescription()
        {
            Console.Out.WriteLine(@"
Sharptillery -t https://blank.org -c 1 -n 100
    Will send 100 requests (max) towards blank.org using one virtual user
Sharptillery -t https://blank.org -c 10 -d 00:00:10 -o report
    Will send as many requests possible with 10 clients for 10 seconds, will create html report

Version: 0.3.5


Flags:
-b <json>           : content in json format
-m <http method>    : GET,PUT,POST
-t <url>            : Whole path to send to target
-n <number>         : Max number of requests to send
-c <number>         : Virtual users. Parallel clients sending requests.
-d HH:MM:SS         : Duration to run for
-T HH:MM:SS         : Per-request timeout (default: 00:00:05)
-A                 : Auto-tune to find max sustainable RPS (duration mode only)
-M <number>         : Auto-tune max clients (default: 256)
-S <number>         : Auto-tune step (default: 5)
-I HH:MM:SS         : Auto-tune interval (default: 00:00:05)
-E <ratio>          : Auto-tune max error ratio, e.g. 0.02 for 2% (default: 0.02)
-r <number>         : Requests per second, the program will try to uphold this number of requests.
-o <name of report> : Outputs a report with name <name of report> in html (default)
-e <report type>    : Type of report (html/excel)
-y <yaml config>    : The yaml config file to read instead of command flags
-h <name:value>     : Add default header to each request. You can reuse the flag multiple times");
        }
    }
    public class FlagParser<T> where T : class, new()
    {
        private readonly Dictionary<char, (Action<string?, T> Callback, char[]? Exclusivness, bool RequiresValue)>
            _flags = new();

        /// <summary>
        /// Parse the incoming arguments and evaluate the flags
        /// </summary>
        /// <param name="args"></param>
        /// <param name="callback">If no flags are set at all, call this callback</param>
        /// <returns></returns>
        /// <exception cref="ArgumentException"></exception>
        public T Parse(string[] args, Func<T> callback)
        {
            /*
             * example: artillery -t https://familyhome-api.azurewebsites.net/api/User -o report
             */

            // PERF: we could do the exclusive flags with actual flags 00000 | 001000

            if (args == null || args.Length == 0) return callback();

            var res = new T();
            var usedFlags = new HashSet<char>();

            for (var i = 0; i < args.Length; i++)
            {
                var token = args[i];
                if (string.IsNullOrWhiteSpace(token))
                    continue;

                if (token.Length != 2 || token[0] != '-')
                    continue;

                var flagChar = token[1];
                if (!_flags.TryGetValue(flagChar, out var flagDef))
                    throw new ArgumentException($"Unknown flag: -{flagChar}");

                usedFlags.Add(flagChar);

                if (!flagDef.RequiresValue)
                {
                    flagDef.Callback(null, res);
                    continue;
                }

                if (i + 1 >= args.Length)
                    throw new ArgumentException($"Flag -{flagChar} requires a value");

                var value = args[i + 1];
                if (string.IsNullOrWhiteSpace(value))
                    throw new ArgumentException($"Flag -{flagChar} requires a value");

                // If the next token looks like another flag, treat it as missing value.
                if (value.Length == 2 && value[0] == '-' && _flags.ContainsKey(value[1]))
                    throw new ArgumentException($"Flag -{flagChar} requires a value");

                flagDef.Callback(value, res);
                i++; // consume value
            }

            if (usedFlags.Count == 0) return callback();

            // check exclusiveness
            foreach (var flagChar in usedFlags)
            {
                var def = _flags[flagChar];
                if (def.Exclusivness == null) continue;
                foreach (var exclusiveFlag in def.Exclusivness)
                {
                    if (!usedFlags.Contains(exclusiveFlag)) continue;
                    throw new ArgumentException($"Not allowed flag combination: {exclusiveFlag} <> {flagChar}");
                }
            }


            return res;
        }

        public FlagParser<T> AddFlag(char flag, Action<string?, T> callback, params char[]? exclusives)
        {
            _flags.Add(flag, (callback, exclusives, true));
            return this;
        }

        public FlagParser<T> AddFlagNoValue(char flag, Action<string?, T> callback, params char[]? exclusives)
        {
            _flags.Add(flag, (callback, exclusives, false));
            return this;
        }
        // public FlagParser<T> AddFlag(char flag, Action<string?, T> callback)
        // {
        //     _flags.Add(flag, (callback, null));
        //     return this;
        // }
        
        
    }
}