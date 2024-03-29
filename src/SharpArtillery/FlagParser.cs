﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
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
                .AddFlag('v', (val, c) => 
                {
                    c.ShowInfo = true;
                })
                .AddFlag('b', (val, c) =>
                {
                    val = val.Replace('\'', '"');
                    c.JsonContent = JsonSerializer.Deserialize<object>(val);
                })
                .AddFlag('m', (val, c) => c.Method = val)
                .AddFlag('h', (val, c) =>
                {
                    var values = val.Split(':');
                    c.Headers.Add(values[0], values[1]);
                })
                .AddFlag('n', (val, c) => c.MaxRequests = int.Parse(val, CultureInfo.InvariantCulture))
                .AddFlag('r', (val, c) => c.ConstantRps = int.Parse(val, CultureInfo.InvariantCulture),
                    'n')
                .AddFlag('d',
                    (val, c) => c.Duration = TimeSpan.Parse(val, CultureInfo.InvariantCulture),
                    'n')
                .AddFlag('c', (val, c) => c.Clients = int.Parse(val, CultureInfo.InvariantCulture))
                .AddFlag('t', (val, c) => c.Target = val)
                .AddFlag('o', (val, c) =>
                {
                    c.ReportSettings.Enabled = true;
                    c.ReportSettings.Name = val;
                    c.ReportSettings.Extension = ".html";
                })
                .AddFlag('e', (val, c) => { c.ReportSettings.Extension = YamlHelper.GetReportExtension(val); })
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

            // var config = flags.YamlConfig;

            // if flags are good and no yaml, we should still be running
            // if (config != null || flags.Target != null) return flags;
            
            if (flags.ShowInfo)
                WriteOutDescription();
            return flags;

        }

        private static void WriteOutDescription()
        {
            Console.Out.WriteLine(@"
Sharptillery -t https://blank.org -c 1 -n 100
    Will send 100 requests (max) towards blank.org using one virtual user
Sharptillery -t https://blank.org -c 10 -d 00:00:10 -o report
    Will send as many requests possible with 10 clients for 10 seconds, will create html report

Version: 0.3.3

Flags:
-b <json>           : content in json format
-m <http method>    : GET,PUT,POST
-t <url>            : Whole path to send to target
-n <number>         : Max number of requests to send
-c <number>         : Virtual users. Parallel clients sending requests.
-d HH:MM:SS         : Duration to run for
-r <number>         : Requests per second, the program will try to uphold this number of requests.
-o <name of report> : Outputs a report with name <name of report> in html (default)
-e <report type>    : Type of report (html/excel)
-y <yaml config>    : The yaml config file to read instead of command flags
-h <name:value>     : Add default header to each request. You can reuse the flag multiple times");
        }
    }
    public class FlagParser<T> where T : class, new()
    {
        private readonly Dictionary<char, (Action<string, T> Callback, char[]? Exclusivness)> _flags = new();

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

            var res = new T();
            var arguments = string.Join(" ", args);
            var usedFlags = new List<KeyValuePair<char, (Action<string, T> Callback, char[]? Exclusivness)>>();
            foreach (var flag in _flags)
            {
                var regStr = $"-{flag.Key}\\s([^\\s]*)";
                var targetRegex = new Regex(regStr, RegexOptions.ECMAScript | RegexOptions.IgnoreCase);
                var matches = targetRegex.Matches(arguments);
                foreach (Match match in matches)
                {
                    if (!match.Success) continue;
                    
                    usedFlags.Add(flag);
                    flag.Value.Callback(match.Groups[1].Value, res);    
                }
            }

            if (usedFlags.Count == 0) return callback();

            // check exclusiveness
            foreach (var flag in usedFlags)
            {
                if (flag.Value.Exclusivness == null) continue;
                foreach (var exclusiveFlag in flag.Value.Exclusivness)
                {
                    if (usedFlags.All(f => f.Key != exclusiveFlag)) continue;
                    var foundExclusive = usedFlags.FirstOrDefault(f => f.Key == exclusiveFlag);
                    throw new ArgumentException(
                        $"Not allowed flag combination: {foundExclusive.Key} <> {flag.Key}");
                }
            }


            return res;
        }

        public FlagParser<T> AddFlag(char flag, Action<string?, T> callback, params char[]? exclusives)
        {
            _flags.Add(flag, (callback, exclusives));
            return this;
        }
        // public FlagParser<T> AddFlag(char flag, Action<string?, T> callback)
        // {
        //     _flags.Add(flag, (callback, null));
        //     return this;
        // }
        
        
    }
}