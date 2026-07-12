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
 *
*/

using System;
using QuantConnect.Data;
using QuantConnect.Util;
using QuantConnect.Interfaces;
using QuantConnect.Data.Market;
using System.Collections.Generic;
using QuantConnect.Data.Auxiliary;

namespace QuantConnect.Lean.Engine.DataFeeds.Enumerators
{
    /// <summary>
    /// Event provider who will emit <see cref="Delisting"/> events
    /// </summary>
    public class DelistingEventProvider : ITradableDateEventProvider
    {
        // we'll use these flags to denote we've already fired off the DelistingType.Warning
        // and a DelistedType.Delisted Delisting object, the _delistingType object is save here
        // since we need to wait for the next trading day before emitting
        private bool _delisted;
        private bool _delistedWarning;
        private IMapFileProvider _mapFileProvider;
        private DateTime _startTime;
        private bool _isDailyCycleFutureOption;

        /// <summary>
        /// The delisting date
        /// </summary>
        protected ReferenceWrapper<DateTime> DelistingDate { get; set; }

        /// <summary>
        /// The current instance being used
        /// </summary>
        protected MapFile MapFile { get; private set; }

        /// <summary>
        /// The associated configuration
        /// </summary>
        protected SubscriptionDataConfig Config { get; private set; }

        /// <summary>
        /// Initializes this instance
        /// </summary>
        /// <param name="config">The <see cref="SubscriptionDataConfig"/></param>
        /// <param name="factorFileProvider">The factor file provider to use</param>
        /// <param name="mapFileProvider">The <see cref="Data.Auxiliary.MapFile"/> provider to use</param>
        /// <param name="startTime">Start date for the data request</param>
        public virtual void Initialize(
            SubscriptionDataConfig config,
            IFactorFileProvider factorFileProvider,
            IMapFileProvider mapFileProvider,
            DateTime startTime)
        {
            Config = config;
            _mapFileProvider = mapFileProvider;
            _startTime = startTime;

            _isDailyCycleFutureOption = config.Symbol.SecurityType == SecurityType.FutureOption
                && Securities.FutureOption.FutureOptionsRootRegistry.TryGetDefinition(
                    config.Symbol.ID.Symbol, config.Symbol.ID.Market, out var rootDefinition)
                && (rootDefinition.Cycle & Securities.FutureOption.FutureOptionExpiryCycles.Daily) != 0;

            InitializeMapFile();
        }

        /// <summary>
        /// Check for delistings
        /// </summary>
        /// <param name="eventArgs">The new tradable day event arguments</param>
        /// <returns>New delisting event if any</returns>
        public virtual IEnumerable<BaseData> GetEvents(NewTradableDateEventArgs eventArgs)
        {
            if (Config.Symbol == eventArgs.Symbol)
            {
                // we send the delisting warning when we reach the delisting date, here we make sure we compare using the date component
                // of the delisting date since for example some futures can trade a few hours in their delisting date, else we would skip on
                // emitting the delisting warning, which triggers us to handle liquidation once delisted
                if (!_delistedWarning && eventArgs.Date >= GetDelistingWarningDate())
                {
                    _delistedWarning = true;
                    var price = eventArgs.LastBaseData?.Price ?? 0;
                    yield return new Delisting(
                        eventArgs.Symbol,
                        DelistingDate.Value.Date,
                        price,
                        DelistingType.Warning);
                }
                if (!_delisted && eventArgs.Date > DelistingDate.Value)
                {
                    _delisted = true;
                    var price = eventArgs.LastBaseData?.Price ?? 0;
                    // delisted at EOD
                    yield return new Delisting(
                        eventArgs.Symbol,
                        DelistingDate.Value.AddDays(1),
                        price,
                        DelistingType.Delisted);
                }
            }
        }

        /// <summary>
        /// The date at which the delisting warning is emitted. For most securities this is the
        /// delisting date itself. Daily-cycle future option roots list on (or days before) their
        /// expiration date, so their whole life can be a single session: for those the warning is
        /// pulled forward to the day before expiry - clamped so it never precedes the subscription
        /// start (the contract's effective listing) - giving algorithms a chance to act while the
        /// contract still trades (design A section 5, fop fork)
        /// </summary>
        /// <returns>The date at which the delisting warning should be emitted</returns>
        protected virtual DateTime GetDelistingWarningDate()
        {
            var warningDate = DelistingDate.Value.Date;
            if (_isDailyCycleFutureOption)
            {
                var pulledForward = warningDate.AddDays(-1);
                warningDate = pulledForward > _startTime.Date ? pulledForward : _startTime.Date;

                // never emit after the delisting date itself
                if (warningDate > DelistingDate.Value.Date)
                {
                    warningDate = DelistingDate.Value.Date;
                }
            }

            return warningDate;
        }

        /// <summary>
        /// Initializes the factor file to use
        /// </summary>
        protected void InitializeMapFile()
        {
            MapFile = _mapFileProvider.ResolveMapFile(Config);
            DelistingDate = new ReferenceWrapper<DateTime>(Config.Symbol.GetDelistingDate(MapFile));
        }
    }
}
