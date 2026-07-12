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

namespace QuantConnect.Securities.FutureOption
{
    /// <summary>
    /// Consistency validator for futures-option underlying mappings. Part of the fop-weeklies
    /// fork (design B issue 2): the underlying future's contract month doubles as a data-directory
    /// key, so a wrong option-to-future mapping silently corrupts converter output paths and
    /// engine data lookups. Given an option contract and the future expiry it was resolved to
    /// (e.g. by an external data converter using the exchange's own definitions), this validator
    /// independently re-derives the underlying through
    /// <see cref="FuturesOptionsUnderlyingMapper.GetUnderlyingFutureFromFutureOption"/> and
    /// reports any mismatch. Wired into the data converter in P7; used by tests until then
    /// </summary>
    public static class UnderlyingMappingValidator
    {
        /// <summary>
        /// Validates that the given non-canonical futures-option symbol's embedded underlying is
        /// consistent with the mapper's own resolution
        /// </summary>
        /// <param name="futureOptionSymbol">Non-canonical futures-option symbol carrying its
        /// underlying future</param>
        /// <param name="error">Human-readable description of the inconsistency, null when valid</param>
        /// <returns>True when the mapping is consistent</returns>
        public static bool TryValidate(Symbol futureOptionSymbol, out string error)
        {
            ArgumentNullException.ThrowIfNull(futureOptionSymbol);

            if (futureOptionSymbol.Underlying == null)
            {
                error = $"UnderlyingMappingValidator: option symbol '{futureOptionSymbol}' has no underlying";
                return false;
            }

            return TryValidate(futureOptionSymbol, futureOptionSymbol.Underlying.ID.Date, out error);
        }

        /// <summary>
        /// Validates that resolving the given futures-option contract through the mapper yields a
        /// future with the provided expiry
        /// </summary>
        /// <param name="futureOptionSymbol">Non-canonical futures-option symbol; its ID carries
        /// the option root, market and expiration date used for re-derivation</param>
        /// <param name="resolvedFutureExpiry">The underlying future expiry to check, e.g. from an
        /// exchange definitions feed or an already-built symbol</param>
        /// <param name="error">Human-readable description of the inconsistency, null when valid</param>
        /// <returns>True when the mapping is consistent</returns>
        public static bool TryValidate(Symbol futureOptionSymbol, DateTime resolvedFutureExpiry, out string error)
        {
            ArgumentNullException.ThrowIfNull(futureOptionSymbol);

            if (futureOptionSymbol.SecurityType != SecurityType.FutureOption)
            {
                error = $"UnderlyingMappingValidator: '{futureOptionSymbol}' is not a futures option";
                return false;
            }
            if (futureOptionSymbol.IsCanonical())
            {
                error = $"UnderlyingMappingValidator: '{futureOptionSymbol}' is canonical, only " +
                    "specific contracts with an expiration date can be validated";
                return false;
            }

            var optionRoot = futureOptionSymbol.ID.Symbol;
            var market = futureOptionSymbol.ID.Market;
            var optionExpiration = futureOptionSymbol.ID.Date;

            var rederived = FuturesOptionsUnderlyingMapper.GetUnderlyingFutureFromFutureOption(
                optionRoot, market, optionExpiration, optionExpiration.Date);
            if (rederived == null)
            {
                error = "UnderlyingMappingValidator: no underlying future could be re-derived for " +
                    $"option root '{optionRoot}' (market '{market}') expiring {optionExpiration:yyyy-MM-dd}";
                return false;
            }

            // compare date components only: candidate underlying contracts are always weeks apart,
            // and externally resolved expiries (exchange definition feeds) are dates while LEAN
            // futures expiries can carry a last-trade time of day
            if (rederived.ID.Date.Date != resolvedFutureExpiry.Date)
            {
                error = "UnderlyingMappingValidator: underlying mismatch for option root " +
                    $"'{optionRoot}' (market '{market}') expiring {optionExpiration:yyyy-MM-dd}: " +
                    $"resolved future expiry {resolvedFutureExpiry:yyyy-MM-dd HH:mm:ss} but the mapper " +
                    $"derives {rederived.ID.Date:yyyy-MM-dd HH:mm:ss} ({rederived.ID.Symbol}). The underlying " +
                    "contract month keys data directories, a mismatch corrupts data paths (design B issue 2)";
                return false;
            }

            if (resolvedFutureExpiry.Date < optionExpiration.Date)
            {
                error = "UnderlyingMappingValidator: option root " +
                    $"'{optionRoot}' (market '{market}') expiring {optionExpiration:yyyy-MM-dd} resolves " +
                    $"to a future expiring earlier ({resolvedFutureExpiry:yyyy-MM-dd}), an option can " +
                    "never outlive its underlying";
                return false;
            }

            error = null;
            return true;
        }
    }
}
