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

using QuantConnect.Securities.FutureOption;

namespace QuantConnect.Securities.Future
{
    /// <summary>
    /// Provides conversions from a GLOBEX Futures ticker to a GLOBEX Futures Options ticker.
    /// Thin shim over <see cref="FutureOptionsRootRegistry"/>, which owns the (now one-to-many)
    /// root data; the legacy monthly mappings and behavior are preserved exactly
    /// </summary>
    public static class FuturesOptionsSymbolMappings
    {
        /// <summary>
        /// Returns the futures options ticker for the given futures ticker.
        /// </summary>
        /// <param name="futureTicker">Future GLOBEX ticker to get Future Option GLOBEX ticker for</param>
        /// <returns>Future option ticker. Defaults to future ticker provided if no entry is found</returns>
        public static string Map(string futureTicker)
        {
            return FutureOptionsRootRegistry.Map(futureTicker);
        }

        /// <summary>
        /// Maps a futures options ticker to its underlying future's ticker
        /// </summary>
        /// <param name="futureOptionTicker">Future option ticker to map to the underlying</param>
        /// <returns>Future ticker</returns>
        public static string MapFromOption(string futureOptionTicker)
        {
            return FutureOptionsRootRegistry.MapFromOption(futureOptionTicker);
        }
    }
}
