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
using QuantConnect.Securities.Future;

namespace QuantConnect.Securities.Option
{
    /// <summary>
    /// Defines a margin model for future options (an option with a future as its underlying).
    /// We re-use the <see cref="FutureMarginModel"/> implementation and multiply its results
    /// by 1.5x to simulate the increased margins seen for future options.
    /// </summary>
    public class FuturesOptionsMarginModel : FutureMarginModel
    {
        /// <summary>
        /// Days to expiry at which the short-side margin ramp starts. At or beyond this many days
        /// the ramp multiplier is 1. Heuristic, not SPAN: short-dated short options carry margin
        /// well above the flat proxy as expiry approaches (design B issue 4)
        /// </summary>
        public const int ShortExpiryRampStartDays = 30;

        /// <summary>
        /// Days to expiry at which the short-side margin ramp reaches its maximum multiplier
        /// </summary>
        public const int ShortExpiryRampEndDays = 1;

        /// <summary>
        /// Maximum short-side margin ramp multiplier, applied at <see cref="ShortExpiryRampEndDays"/>
        /// days to expiry and closer. Heuristic calibration placeholder, not SPAN
        /// </summary>
        public const decimal ShortExpiryRampMaxMultiplier = 1.5m;

        private readonly Option _futureOption;

        /// <summary>
        /// Initial Overnight margin requirement for the contract effective from the date of change
        /// </summary>
        public override decimal InitialOvernightMarginRequirement => GetMarginRequirement(_futureOption, base.InitialOvernightMarginRequirement);

        /// <summary>
        /// Maintenance Overnight margin requirement for the contract effective from the date of change
        /// </summary>
        public override decimal MaintenanceOvernightMarginRequirement => GetMarginRequirement(_futureOption, base.MaintenanceOvernightMarginRequirement);

        /// <summary>
        /// Initial Intraday margin for the contract effective from the date of change
        /// </summary>
        public override decimal InitialIntradayMarginRequirement => GetMarginRequirement(_futureOption, base.InitialIntradayMarginRequirement);

        /// <summary>
        /// Maintenance Intraday margin requirement for the contract effective from the date of change
        /// </summary>
        public override decimal MaintenanceIntradayMarginRequirement => GetMarginRequirement(_futureOption, base.MaintenanceIntradayMarginRequirement);

        /// <summary>
        /// Creates an instance of FutureOptionMarginModel
        /// </summary>
        /// <param name="requiredFreeBuyingPowerPercent">The percentage used to determine the required unused buying power for the account.</param>
        /// <param name="futureOption">Option Security containing a Future security as the underlying</param>
        public FuturesOptionsMarginModel(decimal requiredFreeBuyingPowerPercent = 0, Option futureOption = null) : base(requiredFreeBuyingPowerPercent, futureOption?.Underlying)
        {
            _futureOption = futureOption;
        }

        /// <summary>
        /// Gets the margin currently alloted to the specified holding.
        /// </summary>
        /// <param name="parameters">An object containing the security</param>
        /// <returns>The maintenance margin required for the option</returns>
        /// <remarks>
        /// We fix the option to 1.5x the maintenance because of its close coupling with the underlying.
        /// The option's contract multiplier is 1x, but might be more sensitive to volatility shocks in the long
        /// run when it comes to calculating the different market scenarios attempting to simulate VaR, resulting
        /// in a margin greater than the underlying's margin.
        /// </remarks>
        public override MaintenanceMargin GetMaintenanceMargin(MaintenanceMarginParameters parameters)
        {
            // Long future option positions are premium-only: the premium is paid in full up front,
            // so no maintenance margin is required, mirroring the equity OptionMarginModel
            if (parameters.Quantity >= 0)
            {
                return MaintenanceMargin.Zero;
            }

            var underlyingRequirement = base.GetMaintenanceMargin(parameters.ForUnderlying(parameters.Quantity));
            return GetMarginRequirement(_futureOption, underlyingRequirement, PositionSide.Short);
        }

        /// <summary>
        /// The margin that must be held in order to increase the position by the provided quantity
        /// </summary>
        /// <param name="parameters">An object containing the security and quantity of shares</param>
        /// <returns>The initial margin required for the option (i.e. the equity required to enter a position for this option)</returns>
        /// <remarks>
        /// We fix the option to 1.5x the initial because of its close coupling with the underlying.
        /// The option's contract multiplier is 1x, but might be more sensitive to volatility shocks in the long
        /// run when it comes to calculating the different market scenarios attempting to simulate VaR, resulting
        /// in a margin greater than the underlying's margin.
        /// </remarks>
        public override InitialMargin GetInitialMarginRequirement(InitialMarginParameters parameters)
        {
            var security = parameters.Security;
            var premium = security.QuoteCurrency.ConversionRate
                * security.SymbolProperties.ContractMultiplier
                * security.Price
                * parameters.Quantity;

            // Long future option positions are premium-only: the initial requirement is just the
            // premium paid up front, mirroring the equity OptionMarginModel
            if (parameters.Quantity >= 0)
            {
                return new OptionInitialMargin(0m, premium);
            }

            var underlyingRequirement = base.GetInitialMarginRequirement(parameters.ForUnderlying()).Value;

            return new OptionInitialMargin(
                GetMarginRequirement(_futureOption, underlyingRequirement, PositionSide.Short), premium);
        }

        /// <summary>
        /// Get's the margin requirement for a future option based on the underlying future margin requirement and the position side to trade.
        /// FOPs margin requirement is an 'S' curve based on the underlying requirement around it's current price, see https://en.wikipedia.org/wiki/Logistic_function
        /// </summary>
        /// <param name="option">The future option contract to trade</param>
        /// <param name="underlyingRequirement">The underlying future associated margin requirement</param>
        /// <param name="positionSide">The position side to trade, long by default. This is because short positions require higher margin requirements</param>
        public static int GetMarginRequirement(Option option, decimal underlyingRequirement, PositionSide positionSide = PositionSide.Long)
        {
            var maximumValue = underlyingRequirement;
            var curveGrowthRate = -7.8m;
            var underlyingPrice = option.Underlying.Price;

            // If the underlying price is 0, we can't calculate a margin requirement, so return the underlying requirement.
            // This could be removed after GH issue #6523 is resolved.
            if (option.Underlying == null || option.Underlying.Price == 0m)
            {
                return 0;
            }

            var expiryRampMultiplier = 1m;
            if (positionSide == PositionSide.Short)
            {
                expiryRampMultiplier = GetShortExpiryRampMultiplier(option);
                if (option.Right == OptionRight.Call)
                {
                    // going short the curve growth rate is slower
                    curveGrowthRate = -4m;
                    // curve shifted to the right -> causes a margin requirement increase
                    underlyingPrice *= 1.5m;
                }
                else
                {
                    // higher max requirements
                    maximumValue *= 1.25m;
                    // puts are inverter from calls
                    curveGrowthRate = 2.4m;
                    // curve shifted to the left -> causes a margin requirement increase
                    underlyingPrice *= 0.30m;
                }
            }
            else
            {
                if (option.Right == OptionRight.Put)
                {
                    // fastest change rate
                    curveGrowthRate = 9m;
                }
                else
                {
                    maximumValue *= 1.20m;
                }
            }

            // we normalize the curve growth rate by dividing by the underlyings price
            // this way, contracts with different order of magnitude price and strike (like CL & ES) share this logic
            var denominator = Math.Pow(Math.E, (double) (-curveGrowthRate * (option.ScaledStrikePrice - underlyingPrice) / underlyingPrice));

            if (double.IsInfinity(denominator))
            {
                return 0;
            }
            if (denominator.IsNaNOrZero())
            {
                return (int) (expiryRampMultiplier * maximumValue);
            }

            return (int) (expiryRampMultiplier * maximumValue / (1 + denominator).SafeDecimalCast());
        }

        /// <summary>
        /// Gets the short-side days-to-expiry margin ramp multiplier: 1 at or beyond
        /// <see cref="ShortExpiryRampStartDays"/> days to expiry, rising linearly to
        /// <see cref="ShortExpiryRampMaxMultiplier"/> at <see cref="ShortExpiryRampEndDays"/>
        /// days and closer. This is a documented heuristic, not SPAN: it approximates the margin
        /// expansion clearing houses apply to short-dated short options (design B issue 4).
        /// Covered-pair offsets (e.g. short call against long future) require position-group
        /// buying power models and are deferred to a later phase; short positions are margined naked
        /// </summary>
        /// <param name="option">The future option contract to trade</param>
        /// <returns>The multiplier to apply to the short-side margin requirement</returns>
        public static decimal GetShortExpiryRampMultiplier(Option option)
        {
            if (!option.HasLocalTimeKeeper)
            {
                // no algorithm clock available (e.g. detached security instances): no ramp
                return 1m;
            }

            var daysToExpiry = (option.Symbol.ID.Date.Date - option.LocalTime.Date).Days;
            if (daysToExpiry >= ShortExpiryRampStartDays)
            {
                return 1m;
            }

            if (daysToExpiry <= ShortExpiryRampEndDays)
            {
                return ShortExpiryRampMaxMultiplier;
            }

            return 1m + (ShortExpiryRampMaxMultiplier - 1m)
                * (ShortExpiryRampStartDays - daysToExpiry)
                / (ShortExpiryRampStartDays - ShortExpiryRampEndDays);
        }
    }
}
