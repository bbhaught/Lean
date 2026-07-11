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

namespace QuantConnect.Securities.FutureOption
{
    /// <summary>
    /// A parameterized expiry rule for a futures-option root. Rules compute the expiration date of
    /// a single contract identified by a <see cref="FutureOptionContractKey"/> and can enumerate
    /// all expiries within a date range. Part of the fop-weeklies fork expiry-rule engine; rule
    /// instances are created from registry root definitions by
    /// <see cref="FutureOptionExpiryRuleResolver"/>
    /// </summary>
    public interface IFutureOptionExpiryRule
    {
        /// <summary>
        /// Gets the expiration date of the contract identified by the given key.
        /// The returned value is a bare date (midnight); series expiry times of day are out of
        /// scope for the rule engine and are handled by the expiry-calendar layer
        /// </summary>
        /// <param name="contractKey">The contract to compute the expiry for</param>
        /// <param name="holidays">Exchange holidays to adjust around, sourced from the market
        /// hours database (see FuturesExpiryUtilityFunctions.GetExpirationHolidays)</param>
        /// <returns>The expiration date</returns>
        DateTime GetExpiryDate(FutureOptionContractKey contractKey, IReadOnlyCollection<DateTime> holidays);

        /// <summary>
        /// Enumerates all expiration dates of this series within the inclusive range
        /// [<paramref name="rangeStart"/>, <paramref name="rangeEnd"/>], in ascending order
        /// </summary>
        /// <param name="rangeStart">Inclusive range start</param>
        /// <param name="rangeEnd">Inclusive range end</param>
        /// <param name="holidays">Exchange holidays to adjust around</param>
        /// <returns>All expiration dates within the range</returns>
        IEnumerable<DateTime> EnumerateExpiries(DateTime rangeStart, DateTime rangeEnd, IReadOnlyCollection<DateTime> holidays);
    }
}
