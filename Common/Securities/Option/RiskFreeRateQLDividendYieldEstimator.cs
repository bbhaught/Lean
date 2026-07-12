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
using QuantConnect.Data;
using QuantConnect.Data.Market;

namespace QuantConnect.Securities.Option
{
    /// <summary>
    /// Dividend yield estimator that always returns the risk-free rate produced by the wrapped
    /// <see cref="IQLRiskFreeRateEstimator"/>. Setting the dividend yield equal to the risk-free
    /// rate gives the underlying process zero drift, which prices options on FUTURES correctly:
    /// a futures price is already a forward, so the Black-Scholes-Merton process with q = r
    /// degenerates to the Black-76 model (analytic engines) and to a driftless binomial tree
    /// (American-exercise engines). Used as the default dividend yield estimator for future
    /// options (design B issue 9: default FOP price model = Black-76 on the future, not
    /// BSM-on-spot)
    /// </summary>
    public class RiskFreeRateQLDividendYieldEstimator : IQLDividendYieldEstimator
    {
        private readonly IQLRiskFreeRateEstimator _riskFreeRateEstimator;

        /// <summary>
        /// Creates a new estimator coupled to the given risk-free rate estimator
        /// </summary>
        /// <param name="riskFreeRateEstimator">The risk-free rate estimator to mirror. Defaults to
        /// <see cref="FedRateQLRiskFreeRateEstimator"/>, matching the price model default</param>
        public RiskFreeRateQLDividendYieldEstimator(IQLRiskFreeRateEstimator riskFreeRateEstimator = null)
        {
            _riskFreeRateEstimator = riskFreeRateEstimator ?? new FedRateQLRiskFreeRateEstimator();
        }

        /// <summary>
        /// Returns the current risk-free rate as the dividend yield, producing a zero-drift
        /// underlying process (Black-76 for futures underlyings)
        /// </summary>
        /// <param name="security">The option security object</param>
        /// <param name="slice">The current data slice. This can be used to access other information
        /// available to the algorithm</param>
        /// <param name="contract">The option contract to evaluate</param>
        /// <returns>The estimate</returns>
        public double Estimate(Security security, Slice slice, OptionContract contract)
        {
            return Convert.ToDouble(_riskFreeRateEstimator.Estimate(security, slice, contract));
        }
    }
}
