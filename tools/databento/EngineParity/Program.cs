/*
 * fop-weeklies fork P5-lite/P7 pilot tool.
 *
 * EngineParity: runs the fork's P2 expiry engine and P3 underlying resolver over a CSV of
 * observed exchange instruments (extracted from Databento definition records by
 * tools/databento/validate_registry.py) and emits the engine's answers so the Python validator
 * can compare them 1:1 against the exchange's own expiration dates and underlying instrument ids.
 *
 * This is THE design-B issue-2 parity harness on real data: the converter re-derives the
 * underlying from the definitions' underlying instrument id (authoritative), and this tool proves
 * the engine reproduces the same mapping so data paths and engine lookups agree.
 *
 * Usage:
 *   dotnet run --project tools/databento/EngineParity -- \
 *       --data-folder /path/to/Lean/Data --input in.csv --output out.csv
 *
 * Input CSV (header required):
 *   optionTicker,market,contractKeyMonth,exchangeExpiry
 *     optionTicker     option root, e.g. ES, EW3, E1A
 *     market           lean market, e.g. cme
 *     contractKeyMonth option contract key month yyyyMM (for weeklies: the expiry's calendar
 *                      month; for standard roots: the option's contract month)
 *     exchangeExpiry   the exchange expiration DATE yyyyMMdd from the definitions record
 *
 * Output CSV:
 *   optionTicker,market,contractKeyMonth,exchangeExpiry,engineExpiry,engineUnderlyingMonth,engineUnderlyingExpiry,error
 *     engineExpiry           P2 expiry engine result (yyyyMMdd) for (root, contractKeyMonth)
 *     engineUnderlyingMonth  P3 resolver underlying future contract month (yyyyMM) resolved from
 *                            the EXACT exchange expiration date
 *     engineUnderlyingExpiry the engine's expiry date for that underlying future (yyyyMMdd)
 *     error                  exception text when the engine throws (strict unknown-root, etc.)
 */

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using QuantConnect;
using QuantConnect.Configuration;
using QuantConnect.Securities.Future;
using QuantConnect.Securities.FutureOption;

namespace QuantConnect.Tools.Databento.EngineParity
{
    /// <summary>
    /// Console entry point for the registry/engine parity run, see file header for the contract.
    /// </summary>
    public static class Program
    {
        /// <summary>
        /// Runs the parity export. See file header for the CLI contract.
        /// </summary>
        public static int Main(string[] args)
        {
            string dataFolder = null;
            string input = null;
            string output = null;
            for (var i = 0; i < args.Length - 1; i++)
            {
                switch (args[i])
                {
                    case "--data-folder": dataFolder = args[i + 1]; break;
                    case "--input": input = args[i + 1]; break;
                    case "--output": output = args[i + 1]; break;
                }
            }

            if (dataFolder == null || input == null || output == null)
            {
                Console.Error.WriteLine("usage: EngineParity --data-folder <lean data dir> --input <csv> --output <csv>");
                return 2;
            }

            Config.Set("data-folder", dataFolder);
            Globals.Reset();
            FutureOptionsRootRegistry.Reset();

            var lines = File.ReadAllLines(input);
            using var writer = new StreamWriter(output);
            writer.WriteLine("optionTicker,market,contractKeyMonth,exchangeExpiry,engineExpiry,engineUnderlyingMonth,engineUnderlyingExpiry,error");

            // memoize per unique key; the extraction contains one row per (root, market, key, expiry)
            var seen = new HashSet<string>();
            for (var i = 1; i < lines.Length; i++)
            {
                var line = lines[i].Trim();
                if (line.Length == 0)
                {
                    continue;
                }

                var parts = line.Split(',');
                var root = parts[0];
                var market = parts[1];
                var keyMonth = DateTime.ParseExact(parts[2], "yyyyMM", CultureInfo.InvariantCulture);
                var exchangeExpiry = DateTime.ParseExact(parts[3], "yyyyMMdd", CultureInfo.InvariantCulture);
                if (!seen.Add(line))
                {
                    continue;
                }

                var engineExpiry = "";
                var engineUnderlyingMonth = "";
                var engineUnderlyingExpiry = "";
                var error = "";
                try
                {
                    var futureTicker = FutureOptionsRootRegistry.MapFromOption(root);
                    var canonicalFuture = Symbol.Create(futureTicker, SecurityType.Future, market);
                    var canonicalOption = Symbol.CreateCanonicalOption(canonicalFuture, root, market, null);

                    var expiry = FuturesOptionsExpiryFunctions.FuturesOptionExpiry(canonicalOption, keyMonth);
                    engineExpiry = expiry.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

                    var underlying = FuturesOptionsUnderlyingMapper.GetUnderlyingFutureFromFutureOption(
                        root, market, exchangeExpiry, exchangeExpiry);
                    if (underlying != null)
                    {
                        var contractMonth = FuturesExpiryUtilityFunctions.GetFutureContractMonth(underlying);
                        engineUnderlyingMonth = contractMonth.ToString("yyyyMM", CultureInfo.InvariantCulture);
                        engineUnderlyingExpiry = underlying.ID.Date.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
                    }
                    else
                    {
                        error = "underlying-null";
                    }
                }
                catch (Exception exception)
                {
                    error = exception.GetType().Name + ": " + exception.Message.Replace(',', ';').Replace('\n', ' ');
                }

                writer.WriteLine(string.Join(",",
                    root, market,
                    parts[2], parts[3],
                    engineExpiry, engineUnderlyingMonth, engineUnderlyingExpiry, error));
            }

            Console.WriteLine("EngineParity: wrote " + output);
            return 0;
        }
    }
}
