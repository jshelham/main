#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Xml.Serialization;
using NinjaTrader.Cbi;
using NinjaTrader.NinjaScript.OptimizationFitnesses;
#endregion

namespace NinjaTrader.NinjaScript.OptimizationFitnesses
{
    /// <summary>
    /// ApexConsistencyFitness - Optimization fitness for NinjaTrader 8 Strategy Analyzer
    ///
    /// Purpose: Reward steady, sustainable trading patterns that meet Apex Trader Funding's
    /// consistency requirements for payout eligibility.
    ///
    /// Metrics Calculated:
    /// 1. 30% Windfall Compliance - No single day > 30% of total profit
    /// 2. Daily Profit Distribution - Standard deviation of daily returns
    /// 3. Win Rate Stability - Consistent execution across periods
    /// 4. Contract Size Consistency - Steady position sizing with gradual scaling
    /// 5. Trading Day Quality - Meeting minimum profit and day count requirements
    /// 6. Growth Trajectory Score - Linearity of equity curve (R-squared)
    ///
    /// Fitness Score: (Distribution_Score × Windfall_Compliance × Contract_Consistency) + Trading_Day_Bonuses
    /// Higher score = more consistent, payout-eligible trading pattern
    /// </summary>
    public class ApexConsistencyFitness : OptimizationFitness
    {
        #region Account Configuration

        /// <summary>
        /// Initial account balance for Apex $250K PA
        /// </summary>
        [Display(Name = "Initial Balance", Description = "Starting account balance", Order = 1, GroupName = "Account Settings")]
        public double InitialBalance { get; set; }

        /// <summary>
        /// Safety net balance threshold
        /// </summary>
        [Display(Name = "Safety Net", Description = "Balance where drawdown stops trailing", Order = 2, GroupName = "Account Settings")]
        public double SafetyNet { get; set; }

        /// <summary>
        /// Maximum contracts after safety net
        /// </summary>
        [Display(Name = "Max Contracts", Description = "Maximum contracts after safety net", Order = 3, GroupName = "Account Settings")]
        public int MaxContracts { get; set; }

        /// <summary>
        /// Scaled contracts before safety net
        /// </summary>
        [Display(Name = "Scaled Contracts", Description = "Max contracts before safety net", Order = 4, GroupName = "Account Settings")]
        public int ScaledContracts { get; set; }

        #endregion

        #region Consistency Thresholds

        /// <summary>
        /// Maximum percentage any single day can contribute to total profit (30%)
        /// </summary>
        [Display(Name = "Windfall Threshold", Description = "Max single day percentage of total profit", Order = 1, GroupName = "Consistency Thresholds")]
        public double WindfallThreshold { get; set; }

        /// <summary>
        /// Minimum profit per trading day to count as valid ($50)
        /// </summary>
        [Display(Name = "Min Daily Profit", Description = "Minimum profit to count as valid trading day", Order = 2, GroupName = "Consistency Thresholds")]
        public double MinDailyProfit { get; set; }

        /// <summary>
        /// Required number of trading days for payout eligibility
        /// </summary>
        [Display(Name = "Required Trading Days", Description = "Minimum trading days for payout", Order = 3, GroupName = "Consistency Thresholds")]
        public int RequiredTradingDays { get; set; }

        /// <summary>
        /// Target coefficient of variation for daily returns (lower = more consistent)
        /// </summary>
        [Display(Name = "Target CV", Description = "Target coefficient of variation", Order = 4, GroupName = "Consistency Thresholds")]
        public double TargetCoefficientOfVariation { get; set; }

        /// <summary>
        /// Maximum allowed daily loss as percentage of account
        /// </summary>
        [Display(Name = "Max Daily Loss Percent", Description = "Maximum daily loss percentage", Order = 5, GroupName = "Consistency Thresholds")]
        public double MaxDailyLossPercent { get; set; }

        #endregion

        #region Scoring Weights

        /// <summary>
        /// Weight for windfall compliance in final score
        /// </summary>
        [Display(Name = "Windfall Weight", Description = "Weight for windfall compliance", Order = 1, GroupName = "Scoring Weights")]
        public double WindfallWeight { get; set; }

        /// <summary>
        /// Weight for distribution score
        /// </summary>
        [Display(Name = "Distribution Weight", Description = "Weight for profit distribution", Order = 2, GroupName = "Scoring Weights")]
        public double DistributionWeight { get; set; }

        /// <summary>
        /// Weight for equity curve linearity
        /// </summary>
        [Display(Name = "Linearity Weight", Description = "Weight for equity curve linearity", Order = 3, GroupName = "Scoring Weights")]
        public double LinearityWeight { get; set; }

        /// <summary>
        /// Weight for trading day quality
        /// </summary>
        [Display(Name = "Trading Day Weight", Description = "Weight for trading day quality", Order = 4, GroupName = "Scoring Weights")]
        public double TradingDayWeight { get; set; }

        /// <summary>
        /// Weight for contract consistency
        /// </summary>
        [Display(Name = "Contract Weight", Description = "Weight for contract consistency", Order = 5, GroupName = "Scoring Weights")]
        public double ContractWeight { get; set; }

        #endregion

        /// <summary>
        /// Constructor - Initialize default values for Apex $250K PA
        /// </summary>
        public ApexConsistencyFitness()
        {
            // Account settings
            InitialBalance = 250000;
            SafetyNet = 256600;
            MaxContracts = 27;
            ScaledContracts = 13;

            // Consistency thresholds
            WindfallThreshold = 0.30;
            MinDailyProfit = 50;
            RequiredTradingDays = 8;
            TargetCoefficientOfVariation = 0.5;
            MaxDailyLossPercent = 0.02; // 2% of account

            // Scoring weights (should sum to approximately 1.0 for normalized output)
            WindfallWeight = 0.30;
            DistributionWeight = 0.20;
            LinearityWeight = 0.20;
            TradingDayWeight = 0.15;
            ContractWeight = 0.15;
        }

        /// <summary>
        /// Calculate the consistency fitness value for the strategy
        /// </summary>
        /// <param name="strategyBase">The strategy being optimized</param>
        /// <returns>Fitness score (higher = more consistent, payout-eligible pattern)</returns>
        public override double OnCalculatePerformanceValue(StrategyBase strategyBase)
        {
            // Validate that we have trades to analyze
            if (strategyBase == null ||
                strategyBase.SystemPerformance == null ||
                strategyBase.SystemPerformance.AllTrades == null ||
                strategyBase.SystemPerformance.AllTrades.Count == 0)
            {
                return 0;
            }

            try
            {
                // Get all trades sorted by entry time
                var trades = strategyBase.SystemPerformance.AllTrades
                    .OrderBy(t => t.Entry.Time)
                    .ToList();

                // Calculate daily P&L summary
                var dailyPnL = CalculateDailyPnL(trades);

                if (dailyPnL.Count == 0)
                {
                    return 0;
                }

                // Calculate each consistency metric (0-100 scale each)
                double windfallScore = CalculateWindfallComplianceScore(dailyPnL);
                double distributionScore = CalculateDistributionScore(dailyPnL);
                double winRateStabilityScore = CalculateWinRateStabilityScore(trades);
                double contractConsistencyScore = CalculateContractConsistencyScore(trades);
                double tradingDayScore = CalculateTradingDayQualityScore(dailyPnL);
                double linearityScore = CalculateGrowthTrajectoryScore(trades);

                // Apply weights to combine scores
                // Windfall compliance is critical - it's a multiplier, not just additive
                double baseScore = (distributionScore * DistributionWeight +
                                   linearityScore * LinearityWeight +
                                   tradingDayScore * TradingDayWeight +
                                   contractConsistencyScore * ContractWeight +
                                   winRateStabilityScore * 0.10) * 100;

                // Windfall acts as a multiplier - 0% compliance = 0 score
                double windfallMultiplier = windfallScore / 100.0;

                // Final score with windfall as gate
                double finalScore = baseScore * windfallMultiplier * WindfallWeight +
                                   baseScore * (1 - WindfallWeight);

                // Bonus for exceptional consistency
                if (windfallScore >= 95 && distributionScore >= 80)
                {
                    finalScore += 10; // Bonus for excellent consistency
                }

                return finalScore;
            }
            catch (Exception ex)
            {
                NinjaTrader.Code.Output.Process($"ApexConsistencyFitness Error: {ex.Message}", PrintTo.OutputTab1);
                return 0;
            }
        }

        #region Daily P&L Calculation

        /// <summary>
        /// Calculate daily profit/loss totals from trades
        /// </summary>
        private Dictionary<DateTime, double> CalculateDailyPnL(List<Trade> trades)
        {
            var dailyPnL = new Dictionary<DateTime, double>();

            foreach (var trade in trades)
            {
                DateTime tradeDate = trade.Exit.Time.Date;

                if (!dailyPnL.ContainsKey(tradeDate))
                {
                    dailyPnL[tradeDate] = 0;
                }

                dailyPnL[tradeDate] += trade.ProfitCurrency;
            }

            return dailyPnL;
        }

        #endregion

        #region Windfall Compliance Score

        /// <summary>
        /// Calculate 30% Windfall Compliance Score
        /// Apex Rule: No single day can account for more than 30% of total profit at payout
        /// Score: 100 if compliant, scaled down based on violation severity
        /// </summary>
        private double CalculateWindfallComplianceScore(Dictionary<DateTime, double> dailyPnL)
        {
            if (dailyPnL.Count == 0) return 100;

            double totalProfit = dailyPnL.Values.Sum();

            // If no profit or negative, windfall doesn't apply
            if (totalProfit <= 0) return 100;

            // Find the best single day
            double bestDayProfit = dailyPnL.Values.Max();

            // If best day is negative or zero, no windfall concern
            if (bestDayProfit <= 0) return 100;

            // Calculate the percentage this day represents
            double bestDayPercentage = bestDayProfit / totalProfit;

            if (bestDayPercentage <= WindfallThreshold)
            {
                // Compliant - full score
                return 100;
            }
            else
            {
                // Calculate how far over threshold
                double overage = bestDayPercentage - WindfallThreshold;

                // Calculate minimum additional profit needed to become compliant
                // Formula: Best Day / 0.3 = Required Total Profit
                double requiredTotalProfit = bestDayProfit / WindfallThreshold;
                double additionalProfitNeeded = requiredTotalProfit - totalProfit;

                // Score reduction based on severity
                // If overage is 0.1 (40% instead of 30%), significant penalty
                // If overage is 0.7 (100% - one day has all profit), nearly zero score
                double penaltyFactor = overage / (1 - WindfallThreshold); // Normalize overage
                double score = 100 * (1 - penaltyFactor);

                // Additional penalty based on how much more profit is needed
                double effortPenalty = Math.Min(additionalProfitNeeded / totalProfit * 20, 30);
                score -= effortPenalty;

                return Math.Max(0, score);
            }
        }

        #endregion

        #region Distribution Score

        /// <summary>
        /// Calculate Daily Profit Distribution Score
        /// Measures consistency of daily returns using coefficient of variation
        /// Lower CV = more consistent = higher score
        /// </summary>
        private double CalculateDistributionScore(Dictionary<DateTime, double> dailyPnL)
        {
            if (dailyPnL.Count < 2) return 50; // Not enough data

            var profitDays = dailyPnL.Values.Where(p => p != 0).ToList();

            if (profitDays.Count < 2) return 50;

            // Calculate mean and standard deviation
            double mean = profitDays.Average();
            double variance = profitDays.Sum(p => Math.Pow(p - mean, 2)) / profitDays.Count;
            double stdDev = Math.Sqrt(variance);

            // Calculate coefficient of variation (CV)
            // CV = StdDev / |Mean| - lower is more consistent
            double cv = mean != 0 ? stdDev / Math.Abs(mean) : 0;

            // Score based on how CV compares to target
            // CV of 0 = perfect consistency = 100
            // CV of TargetCV = acceptable = 70
            // CV of 2 × TargetCV = poor = 40
            // CV of 3 × TargetCV or more = very poor = 0
            if (cv <= TargetCoefficientOfVariation * 0.5)
            {
                return 100; // Excellent consistency
            }
            else if (cv <= TargetCoefficientOfVariation)
            {
                // Good to acceptable
                double ratio = cv / TargetCoefficientOfVariation;
                return 100 - (ratio * 30); // 70-100 range
            }
            else if (cv <= TargetCoefficientOfVariation * 2)
            {
                // Acceptable to poor
                double ratio = (cv - TargetCoefficientOfVariation) / TargetCoefficientOfVariation;
                return 70 - (ratio * 30); // 40-70 range
            }
            else
            {
                // Poor to very poor
                double ratio = (cv - TargetCoefficientOfVariation * 2) / TargetCoefficientOfVariation;
                return Math.Max(0, 40 - (ratio * 40)); // 0-40 range
            }
        }

        #endregion

        #region Win Rate Stability Score

        /// <summary>
        /// Calculate Win Rate Stability Score
        /// Measures consistency of win rate across different time periods
        /// Penalizes erratic win rate swings
        /// </summary>
        private double CalculateWinRateStabilityScore(List<Trade> trades)
        {
            if (trades.Count < 10) return 50; // Not enough data for meaningful analysis

            // Overall win rate
            double overallWinRate = (double)trades.Count(t => t.ProfitCurrency > 0) / trades.Count;

            // Calculate win rate in rolling windows (groups of 10 trades)
            int windowSize = Math.Max(5, trades.Count / 5); // At least 5 windows
            var winRates = new List<double>();

            for (int i = 0; i < trades.Count - windowSize + 1; i += windowSize / 2) // 50% overlap
            {
                var window = trades.Skip(i).Take(windowSize).ToList();
                double windowWinRate = (double)window.Count(t => t.ProfitCurrency > 0) / window.Count;
                winRates.Add(windowWinRate);
            }

            if (winRates.Count < 2) return 70; // Not enough windows

            // Calculate variance of win rates
            double meanWinRate = winRates.Average();
            double variance = winRates.Sum(wr => Math.Pow(wr - meanWinRate, 2)) / winRates.Count;
            double stdDev = Math.Sqrt(variance);

            // Score based on win rate stability
            // StdDev of 0 = perfect stability = 100
            // StdDev of 0.1 = good = 80
            // StdDev of 0.2 = acceptable = 60
            // StdDev of 0.3+ = poor = 40 or less
            if (stdDev <= 0.05)
            {
                return 100;
            }
            else if (stdDev <= 0.1)
            {
                return 100 - (stdDev / 0.1 * 20); // 80-100 range
            }
            else if (stdDev <= 0.2)
            {
                return 80 - ((stdDev - 0.1) / 0.1 * 20); // 60-80 range
            }
            else if (stdDev <= 0.3)
            {
                return 60 - ((stdDev - 0.2) / 0.1 * 20); // 40-60 range
            }
            else
            {
                return Math.Max(0, 40 - ((stdDev - 0.3) / 0.1 * 20)); // 0-40 range
            }
        }

        #endregion

        #region Contract Size Consistency Score

        /// <summary>
        /// Calculate Contract Size Consistency Score
        /// Penalizes erratic position size changes
        /// Allows gradual scaling with account growth
        /// </summary>
        private double CalculateContractConsistencyScore(List<Trade> trades)
        {
            if (trades.Count < 2) return 100;

            // Group trades by day for daily contract analysis
            var tradesByDay = trades.GroupBy(t => t.Entry.Time.Date).OrderBy(g => g.Key).ToList();

            double runningBalance = InitialBalance;
            var dailyMaxContracts = new List<double>();
            var allowedContracts = new List<double>();

            foreach (var dayGroup in tradesByDay)
            {
                int maxContractsToday = dayGroup.Max(t => (int)t.Quantity);
                dailyMaxContracts.Add(maxContractsToday);

                // What was allowed based on balance
                double allowedToday = runningBalance >= SafetyNet ? MaxContracts : ScaledContracts;
                allowedContracts.Add(allowedToday);

                runningBalance += dayGroup.Sum(t => t.ProfitCurrency);
            }

            if (dailyMaxContracts.Count < 2) return 100;

            // Calculate day-over-day contract changes
            var contractChanges = new List<double>();
            for (int i = 1; i < dailyMaxContracts.Count; i++)
            {
                double change = Math.Abs(dailyMaxContracts[i] - dailyMaxContracts[i - 1]);

                // Normalize by allowed contracts to account for scaling
                double normalizedChange = change / Math.Max(allowedContracts[i], 1);
                contractChanges.Add(normalizedChange);
            }

            // Average absolute change
            double avgChange = contractChanges.Average();

            // Score based on consistency
            // 0 change = perfect = 100
            // 0.1 normalized change = good = 90
            // 0.25 normalized change = acceptable = 70
            // 0.5+ = erratic = 50 or less
            if (avgChange <= 0.05)
            {
                return 100;
            }
            else if (avgChange <= 0.1)
            {
                return 100 - (avgChange / 0.1 * 10); // 90-100
            }
            else if (avgChange <= 0.25)
            {
                return 90 - ((avgChange - 0.1) / 0.15 * 20); // 70-90
            }
            else if (avgChange <= 0.5)
            {
                return 70 - ((avgChange - 0.25) / 0.25 * 20); // 50-70
            }
            else
            {
                return Math.Max(20, 50 - ((avgChange - 0.5) / 0.5 * 30)); // 20-50
            }
        }

        #endregion

        #region Trading Day Quality Score

        /// <summary>
        /// Calculate Trading Day Quality Score
        /// Tracks days with minimum profit, consecutive profitable days, meeting requirements
        /// </summary>
        private double CalculateTradingDayQualityScore(Dictionary<DateTime, double> dailyPnL)
        {
            if (dailyPnL.Count == 0) return 0;

            // Count qualifying trading days (minimum $50 profit)
            int qualifyingDays = dailyPnL.Values.Count(p => p >= MinDailyProfit);

            // Count total trading days
            int totalTradingDays = dailyPnL.Count;

            // Count profitable days (any profit)
            int profitableDays = dailyPnL.Values.Count(p => p > 0);

            // Calculate consecutive profitable days streak
            int maxStreak = 0;
            int currentStreak = 0;
            foreach (var day in dailyPnL.OrderBy(d => d.Key))
            {
                if (day.Value > 0)
                {
                    currentStreak++;
                    maxStreak = Math.Max(maxStreak, currentStreak);
                }
                else
                {
                    currentStreak = 0;
                }
            }

            // Score components

            // 1. Meeting 8-day trading requirement (40 points max)
            double dayRequirementScore = Math.Min((double)qualifyingDays / RequiredTradingDays, 1.0) * 40;

            // 2. Win day rate (30 points max)
            double winDayRate = (double)profitableDays / totalTradingDays;
            double winDayScore = winDayRate * 30;

            // 3. Streak bonus (20 points max)
            double streakScore = Math.Min((double)maxStreak / 5, 1.0) * 20; // 5-day streak = max

            // 4. Qualifying day rate (10 points max)
            double qualifyingRate = (double)qualifyingDays / totalTradingDays;
            double qualifyingScore = qualifyingRate * 10;

            return dayRequirementScore + winDayScore + streakScore + qualifyingScore;
        }

        #endregion

        #region Growth Trajectory Score

        /// <summary>
        /// Calculate Growth Trajectory Score (R-squared of equity curve)
        /// Measures linearity of equity curve vs ideal linear growth
        /// Penalizes "lottery style" equity curves
        /// </summary>
        private double CalculateGrowthTrajectoryScore(List<Trade> trades)
        {
            if (trades.Count < 5) return 50; // Not enough data

            // Build equity curve
            var equityCurve = new List<double>();
            double currentBalance = InitialBalance;
            equityCurve.Add(currentBalance);

            foreach (var trade in trades.OrderBy(t => t.Exit.Time))
            {
                currentBalance += trade.ProfitCurrency;
                equityCurve.Add(currentBalance);
            }

            // Calculate R-squared against linear regression
            double rSquared = CalculateRSquared(equityCurve);

            // Score based on R-squared
            // R² of 1.0 = perfect linear growth = 100
            // R² of 0.9 = very good = 90
            // R² of 0.7 = acceptable = 70
            // R² of 0.5 = lottery-style = 50
            // R² of 0.3 or less = very erratic = 30 or less
            if (rSquared >= 0.95)
            {
                return 100;
            }
            else if (rSquared >= 0.9)
            {
                return 90 + (rSquared - 0.9) / 0.05 * 10;
            }
            else if (rSquared >= 0.7)
            {
                return 70 + (rSquared - 0.7) / 0.2 * 20;
            }
            else if (rSquared >= 0.5)
            {
                return 50 + (rSquared - 0.5) / 0.2 * 20;
            }
            else if (rSquared >= 0.3)
            {
                return 30 + (rSquared - 0.3) / 0.2 * 20;
            }
            else
            {
                return Math.Max(0, rSquared / 0.3 * 30);
            }
        }

        /// <summary>
        /// Calculate R-squared value for linear regression fit
        /// </summary>
        private double CalculateRSquared(List<double> values)
        {
            if (values.Count < 2) return 0;

            int n = values.Count;

            // X values are simply 0, 1, 2, ..., n-1
            double sumX = 0;
            double sumY = 0;
            double sumXY = 0;
            double sumX2 = 0;
            double sumY2 = 0;

            for (int i = 0; i < n; i++)
            {
                double x = i;
                double y = values[i];

                sumX += x;
                sumY += y;
                sumXY += x * y;
                sumX2 += x * x;
                sumY2 += y * y;
            }

            // Calculate slope (m) and intercept (b) for y = mx + b
            double denominator = n * sumX2 - sumX * sumX;
            if (Math.Abs(denominator) < 1e-10) return 0;

            double slope = (n * sumXY - sumX * sumY) / denominator;
            double intercept = (sumY - slope * sumX) / n;

            // Calculate R-squared
            double meanY = sumY / n;
            double ssTot = 0; // Total sum of squares
            double ssRes = 0; // Residual sum of squares

            for (int i = 0; i < n; i++)
            {
                double y = values[i];
                double yPred = slope * i + intercept;

                ssTot += Math.Pow(y - meanY, 2);
                ssRes += Math.Pow(y - yPred, 2);
            }

            if (ssTot < 1e-10) return 1.0; // All values are the same

            double rSquared = 1 - (ssRes / ssTot);

            // Ensure R² is between 0 and 1
            return Math.Max(0, Math.Min(1, rSquared));
        }

        #endregion
    }
}
