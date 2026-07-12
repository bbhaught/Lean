/*
 * QUANTCONNECT.COM - Democratizing Finance, Empowering Individuals.
 * Lean Algorithmic Trading Engine v2.0. Copyright 2014 QuantConnect Corporation.
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
*/

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;
using QuantConnect.Util;

namespace QuantConnect.Tests.Common.Securities.FutureOption
{
    /// <summary>
    /// Deterministic generator for point-in-time weekly/daily future option universe fixtures
    /// (fop-weeklies fork P4, design B issue 8). Fabricates per-date universe CSV files in the LEAN
    /// universe format (header, underlying future line, then expiry,strike,right,OHLCV,OI rows) for
    /// ES option roots around January 2020, matching the sample ES future and monthly option data
    /// shipped in the repository Data folder.
    ///
    /// Listing-date realism: files for a contract only exist from about two weeks before its expiry,
    /// so requesting a chain before the first universe file must yield no contracts (point-in-time).
    ///
    /// The generated files are checked into Data/futureoption/cme/universes/ and guarded by
    /// <see cref="CheckedInFixturesMatchGenerator"/>; regenerate them with the explicit
    /// <see cref="RegenerateCheckedInFixtures"/> test after changing this generator
    /// </summary>
    [TestFixture, Category("FopFork")]
    public static class FutureOptionUniverseTestDataGenerator
    {
        /// <summary>
        /// The market of all generated fixtures
        /// </summary>
        public const string Market = QuantConnect.Market.CME;

        /// <summary>
        /// The synthetic daily-cycle test root. It has no production registry seed: tests inject it via
        /// FutureOptionsRootRegistry.SetDefinitionsForTesting to exercise the daily-root stale-universe
        /// fallback gating. Its universe files are inert for every other test
        /// </summary>
        public const string DailyTestRoot = "ESD";

        /// <summary>
        /// The March 2020 E-mini S&P 500 future underlying all generated chains, matching the
        /// repository sample data (Data/futureoption/cme/universes/es/202003)
        /// </summary>
        public static Symbol UnderlyingFuture { get; } = Symbol.CreateFuture(
            QuantConnect.Securities.Futures.Indices.SP500EMini, Market, new DateTime(2020, 3, 20));

        private static readonly decimal[] _strikes = { 3150m, 3200m, 3250m, 3300m, 3350m };

        /// <summary>
        /// A single generated universe root: the option root ticker and the option expiries its files contain.
        /// File dates are the trading dates in [listing date, expiry] intersected with the generation window
        /// </summary>
        private sealed class RootFixture
        {
            public string Root { get; }
            public DateTime[] Expiries { get; }

            public RootFixture(string root, params DateTime[] expiries)
            {
                Root = root;
                Expiries = expiries;
            }
        }

        /// <summary>
        /// The generation window: the trading dates for which the repository sample data has ES
        /// monthly universe files
        /// </summary>
        private static readonly DateTime[] _tradingDates =
        {
            new DateTime(2020, 1, 2),
            new DateTime(2020, 1, 3),
            new DateTime(2020, 1, 6),
            new DateTime(2020, 1, 7),
            new DateTime(2020, 1, 8)
        };

        private static readonly RootFixture[] _fixtures =
        {
            // ES Friday weekly, week 2: expires 2020-01-10, listed from 2019-12-27 (all window dates)
            new RootFixture("EW2", new DateTime(2020, 1, 10)),
            // ES Friday weekly, week 3: expires 2020-01-17, listed from 2020-01-06 (point-in-time:
            // absent on 2020-01-02 and 2020-01-03)
            new RootFixture("EW3", new DateTime(2020, 1, 17)),
            // ES Monday weekly, week 1: expires 2020-01-06 (expires inside the window)
            new RootFixture("E1A", new DateTime(2020, 1, 6)),
            // ES Tuesday weekly, week 1: expires 2020-01-07. Its registry ListedSince is 2022-04-01
            // (Tuesday weeklies launched April 2022), so chain providers must skip it in 2020 even
            // though these files exist
            new RootFixture("E1B", new DateTime(2020, 1, 7)),
            // synthetic daily root: one expiry per next trading day, listed two trading days ahead
            new RootFixture(DailyTestRoot,
                new DateTime(2020, 1, 6),
                new DateTime(2020, 1, 7),
                new DateTime(2020, 1, 8),
                new DateTime(2020, 1, 9),
                new DateTime(2020, 1, 10))
        };

        /// <summary>
        /// Generates all fixture files under the given data folder and returns the written file paths
        /// </summary>
        /// <param name="dataFolder">The data folder root to generate into</param>
        /// <returns>The written file paths</returns>
        public static List<string> Generate(string dataFolder)
        {
            var written = new List<string>();
            foreach (var fixture in _fixtures)
            {
                var canonical = Symbol.CreateCanonicalOption(UnderlyingFuture, fixture.Root, Market, null);
                var directory = LeanData.GenerateUniversesDirectory(dataFolder, canonical);
                Directory.CreateDirectory(directory);

                foreach (var date in _tradingDates)
                {
                    var contracts = fixture.Expiries
                        .Where(expiry => date <= expiry && date >= ListingDate(expiry))
                        .ToList();
                    if (contracts.Count == 0)
                    {
                        continue;
                    }

                    var path = Path.Combine(directory, $"{date:yyyyMMdd}.csv");
                    File.WriteAllText(path, GenerateCsv(date, contracts));
                    written.Add(path);
                }
            }

            return written;
        }

        /// <summary>
        /// Listing-date realism: a contract's universe files exist from about two weeks (11 calendar
        /// days here so that EW3 lists exactly on 2020-01-06) before its expiry
        /// </summary>
        private static DateTime ListingDate(DateTime expiry)
        {
            return expiry.AddDays(-11);
        }

        private static string GenerateCsv(DateTime date, List<DateTime> expiries)
        {
            var builder = new StringBuilder();
            builder.AppendLine("#expiry,strike,right,open,high,low,close,volume,open_interest");

            // underlying future line: deterministic synthetic ES prices around early January 2020 levels
            var dayOffset = (date - _tradingDates[0]).Days;
            var underlyingClose = 3230m + 2m * dayOffset;
            builder.AppendLine(FormattableString.Invariant(
                $",,,{underlyingClose - 4},{underlyingClose + 6},{underlyingClose - 8},{underlyingClose},900000,"));

            foreach (var expiry in expiries)
            {
                foreach (var strike in _strikes)
                {
                    foreach (var right in new[] { 'C', 'P' })
                    {
                        var moneyness = right == 'C' ? underlyingClose - strike : strike - underlyingClose;
                        var close = Math.Max(1m, moneyness / 10m + 20m);
                        builder.AppendLine(FormattableString.Invariant(
                            $"{expiry:yyyyMMdd},{strike},{right},{close - 1},{close + 1},{close - 2},{close},100,500"));
                    }
                }
            }

            return builder.ToString();
        }

        /// <summary>
        /// Guards the checked-in fixture files: regenerating them must reproduce the committed bytes,
        /// proving the generator is deterministic and the fixtures were not edited by hand
        /// </summary>
        [Test]
        public static void CheckedInFixturesMatchGenerator()
        {
            var tempFolder = Path.Combine(Path.GetTempPath(), $"fop-universe-fixtures-{Guid.NewGuid():N}");
            try
            {
                var generated = Generate(tempFolder);
                Assert.IsNotEmpty(generated);

                foreach (var generatedPath in generated)
                {
                    var relativePath = generatedPath.Substring(tempFolder.Length).TrimStart(Path.DirectorySeparatorChar);
                    var checkedInPath = Path.Combine(Globals.DataFolder, relativePath);

                    Assert.IsTrue(File.Exists(checkedInPath), $"Missing checked-in fixture: {checkedInPath}. " +
                        "Run the explicit RegenerateCheckedInFixtures test to (re)create the fixtures");
                    Assert.AreEqual(File.ReadAllText(generatedPath), File.ReadAllText(checkedInPath),
                        $"Checked-in fixture is out of sync with the generator: {checkedInPath}");
                }
            }
            finally
            {
                if (Directory.Exists(tempFolder))
                {
                    Directory.Delete(tempFolder, recursive: true);
                }
            }
        }

        /// <summary>
        /// Regenerates the checked-in fixtures in the repository data folder. Explicit: run manually
        /// after changing the generator, then commit the resulting files
        /// </summary>
        [Test, Explicit("Writes into the repository data folder")]
        public static void RegenerateCheckedInFixtures()
        {
            var written = Generate(Globals.DataFolder);
            TestContext.Out.WriteLine($"Regenerated {written.Count} fixture files under {Globals.DataFolder}:");
            foreach (var path in written)
            {
                TestContext.Out.WriteLine($"  {path}");
            }
        }
    }
}
