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
    /// ApexProfitabilityFitness - Optimization fitness for NinjaTrader 8 Strategy Analyzer
    ///
    /// Purpose: Maximize realistic profitability within Apex Trader Funding $250K PA constraints.
    ///
    /// Metrics Calculated:
    /// 1. Net Profit After Simulated Apex Constraints - Profit with contract scaling enforced
    /// 2. Safety Net Achievement - Bonuses for reaching $256,600 and profit goal
    /// 3. Profit Factor with Apex Risk Adjustment - Standard PF weighted by compliance
    /// 4. Drawdown-Adjusted Returns - Calmar ratio style metric
    /// 5. Payout Eligibility Score - Based on balance progression
    ///
    /// Fitness Score = (Net_Profit × Survival_Factor × Compliance_Multiplier) + Achievement_Bonuses
    /// Higher score = better profitability within Apex rules
    /// </summary>
    public class ApexProfitabilityFitness : OptimizationFitness
    {
        #region Account Configuration

        /// <summary>
        /// Initial account balance for Apex $250K PA
        /// </summary>
        [Display(Name = "Initial Balance", Description = "Starting account balance", Order = 1, GroupName = "Account Settings")]
        public double InitialBalance { get; set; }

        /// <summary>
        /// Trailing drawdown amount ($6,500 for $250K PA)
        /// </summary>
        [Display(Name = "Trailing Drawdown", Description = "Trailing drawdown threshold amount", Order = 2, GroupName = "Account Settings")]
        public double TrailingDrawdown { get; set; }

        /// <summary>
        /// Safety net balance threshold ($256,600 for $250K PA)
        /// </summary>
        [Display(Name = "Safety Net", Description = "Balance threshold where drawdown stops trailing", Order = 3, GroupName = "Account Settings")]
        public double SafetyNet { get; set; }

        /// <summary>
        /// Maximum contracts allowed after safety net reached
        /// </summary>
        [Display(Name = "Max Contracts", Description = "Maximum contracts after safety net", Order = 4, GroupName = "Account Settings")]
        public int MaxContracts { get; set; }

        /// <summary>
        /// Scaled contracts allowed before safety net reached
        /// </summary>
        [Display(Name = "Scaled Contracts", Description = "Max contracts before safety net", Order = 5, GroupName = "Account Settings")]
        public int ScaledContracts { get; set; }

        /// <summary>
        /// Profit goal for the account
        /// </summary>
        [Display(Name = "Profit Goal", Description = "Target profit amount", Order = 6, GroupName = "Account Settings")]
        public double ProfitGoal { get; set; }

        #endregion

        #region Rule Thresholds

        /// <summary>
        /// MAE percentage for new accounts (30%)
        /// </summary>
        [Display(Name = "MAE Percent New", Description = "MAE limit for new accounts", Order = 1, GroupName = "Rule Thresholds")]
        public double MaePercentNew { get; set; }

        /// <summary>
        /// MAE percentage for grown accounts (50%)
        /// </summary>
        [Display(Name = "MAE Percent Grown", Description = "MAE limit for grown accounts", Order = 2, GroupName = "Rule Thresholds")]
        public double MaePercentGrown { get; set; }

        /// <summary>
        /// Maximum risk-to-reward ratio (5:1)
        /// </summary>
        [Display(Name = "Max Risk Reward Ratio", Description = "Maximum risk-to-reward ratio", Order = 3, GroupName = "Rule Thresholds")]
        public double MaxRiskRewardRatio { get; set; }

        /// <summary>
        /// Profit threshold where 50% MAE applies ($13,200)
        /// </summary>
        [Display(Name = "Doubled Safety Net Threshold", Description = "Profit where 50% MAE applies", Order = 4, GroupName = "Rule Thresholds")]
        public double DoubledSafetyNetThreshold { get; set; }

        /// <summary>
        /// Maximum MAE for new accounts ($1,950)
        /// </summary>
        [Display(Name = "Max MAE New Account", Description = "Absolute max MAE for new accounts", Order = 5, GroupName = "Rule Thresholds")]
        public double MaxMaeNewAccount { get; set; }

        #endregion

        #region Bonus Configuration

        /// <summary>
        /// Bonus points for reaching safety net
        /// </summary>
        [Display(Name = "Safety Net Bonus", Description = "Bonus for reaching safety net", Order = 1, GroupName = "Bonus Settings")]
        public double SafetyNetBonus { get; set; }

        /// <summary>
        /// Bonus points for reaching profit goal
        /// </summary>
        [Display(Name = "Profit Goal Bonus", Description = "Bonus for reaching profit goal", Order = 2, GroupName = "Bonus Settings")]
        public double ProfitGoalBonus { get; set; }

        /// <summary>
        /// Minimum compliance multiplier (0.5 = 50% penalty for poor compliance)
        /// </summary>
        [Display(Name = "Min Compliance Multiplier", Description = "Minimum compliance multiplier", Order = 3, GroupName = "Bonus Settings")]
        public double MinComplianceMultiplier { get; set; }

        /// <summary>
        /// Profit scaling factor (normalizes profit to fitness scale)
        /// </summary>
        [Display(Name = "Profit Scale Factor", Description = "Scales profit to fitness range", Order = 4, GroupName = "Bonus Settings")]
        public double ProfitScaleFactor { get; set; }

        #endregion

        /// <summary>
        /// Constructor - Initialize default values for Apex $250K PA
        /// </summary>
        public ApexProfitabilityFitness()
        {
            // Account settings
            InitialBalance = 250000;
            TrailingDrawdown = 6500;
            SafetyNet = 256600;
            MaxContracts = 27;
            ScaledContracts = 13;
            ProfitGoal = 15000;

            // Rule thresholds
            MaePercentNew = 0.30;
            MaePercentGrown = 0.50;
            MaxRiskRewardRatio = 5.0;
            DoubledSafetyNetThreshold = 13200;
            MaxMaeNewAccount = 1950;

            // Bonus configuration
            SafetyNetBonus = 25;
            ProfitGoalBonus = 50;
            MinComplianceMultiplier = 0.5;
            ProfitScaleFactor = 0.01; // 1% of profit as fitness points
        }

        /// <summary>
        /// Calculate the profitability fitness value for the strategy
        /// </summary>
        /// <param name="strategyBase">The strategy being optimized</param>
        /// <returns>Fitness score (higher = better profitability within Apex rules)</returns>
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

                // Check if account survives trailing drawdown
                var (survives, finalBalance, peakBalance, reachedSafetyNet) = SimulateAccountProgression(trades);

                if (!survives)
                {
                    // Account blown - return 0 or negative
                    return -50;
                }

                // Calculate net profit with Apex constraints applied
                double adjustedProfit = CalculateApexAdjustedProfit(trades, reachedSafetyNet);

                // Calculate compliance multiplier
                double complianceMultiplier = CalculateComplianceMultiplier(trades);

                // Calculate profit factor with risk adjustment
                double adjustedProfitFactor = CalculateAdjustedProfitFactor(trades);

                // Calculate Calmar ratio (drawdown-adjusted returns)
                double calmarRatio = CalculateCalmarRatio(adjustedProfit, trades);

                // Calculate achievement bonuses
                double achievementBonus = 0;
                if (reachedSafetyNet)
                {
                    achievementBonus += SafetyNetBonus;
                }
                if (finalBalance >= InitialBalance + ProfitGoal)
                {
                    achievementBonus += ProfitGoalBonus;
                }

                // Calculate payout eligibility score
                double payoutScore = CalculatePayoutEligibilityScore(finalBalance, reachedSafetyNet);

                // Combine all metrics into final fitness score
                // Formula: (Profit × ComplianceMultiplier × CalmarFactor) + Bonuses + PayoutScore
                double profitPoints = adjustedProfit * ProfitScaleFactor;
                double calmarFactor = Math.Min(calmarRatio / 2.0, 1.5); // Cap at 1.5x for exceptional Calmar

                double fitnessScore = (profitPoints * complianceMultiplier * Math.Max(calmarFactor, 0.5))
                                    + achievementBonus
                                    + payoutScore
                                    + (adjustedProfitFactor * 5); // Small bonus for good profit factor

                return fitnessScore;
            }
            catch (Exception ex)
            {
                NinjaTrader.Code.Output.Process($"ApexProfitabilityFitness Error: {ex.Message}", PrintTo.OutputTab1);
                return 0;
            }
        }

        #region Account Progression Simulation

        /// <summary>
        /// Simulate account balance progression with trailing drawdown
        /// Returns tuple: (survives, finalBalance, peakBalance, reachedSafetyNet)
        /// </summary>
        private (bool survives, double finalBalance, double peakBalance, bool reachedSafetyNet) SimulateAccountProgression(List<Trade> trades)
        {
            double currentBalance = InitialBalance;
            double peakBalance = InitialBalance;
            double drawdownFloor = InitialBalance - TrailingDrawdown;
            bool safetyNetReached = false;

            foreach (var trade in trades.OrderBy(t => t.Entry.Time))
            {
                // Check unrealized drawdown at worst point (MAE)
                double unrealizedWorst = currentBalance - Math.Abs(trade.MaeCurrency);

                if (unrealizedWorst <= drawdownFloor)
                {
                    return (false, unrealizedWorst, peakBalance, safetyNetReached);
                }

                // Update balance after trade
                currentBalance += trade.ProfitCurrency;

                // Update peak and drawdown floor
                if (currentBalance > peakBalance)
                {
                    peakBalance = currentBalance;

                    if (!safetyNetReached && peakBalance >= SafetyNet)
                    {
                        safetyNetReached = true;
                        drawdownFloor = InitialBalance + 100; // Lock at $250,100
                    }
                    else if (!safetyNetReached)
                    {
                        drawdownFloor = peakBalance - TrailingDrawdown;
                    }
                }

                // Check final balance against drawdown floor
                if (currentBalance <= drawdownFloor)
                {
                    return (false, currentBalance, peakBalance, safetyNetReached);
                }
            }

            return (true, currentBalance, peakBalance, safetyNetReached);
        }

        #endregion

        #region Apex-Adjusted Profit Calculation

        /// <summary>
        /// Calculate net profit as if trading with Apex contract scaling rules enforced
        /// Scales down oversized positions before safety net is reached
        /// </summary>
        private double CalculateApexAdjustedProfit(List<Trade> trades, bool everReachedSafetyNet)
        {
            // Group trades by day for EOD balance tracking
            var tradesByDay = trades.GroupBy(t => t.Entry.Time.Date).OrderBy(g => g.Key).ToList();

            double runningBalance = InitialBalance;
            double adjustedProfit = 0;
            bool safetyNetReached = false;

            foreach (var dayGroup in tradesByDay)
            {
                foreach (var trade in dayGroup.OrderBy(t => t.Entry.Time))
                {
                    int actualContracts = (int)trade.Quantity;
                    int allowedContracts = safetyNetReached ? MaxContracts : ScaledContracts;

                    // Calculate profit per contract
                    double profitPerContract = actualContracts > 0 ? trade.ProfitCurrency / actualContracts : 0;

                    // Apply contract scaling
                    int effectiveContracts = Math.Min(actualContracts, allowedContracts);
                    double scaledProfit = profitPerContract * effectiveContracts;

                    adjustedProfit += scaledProfit;
                }

                // Update EOD balance for contract scaling determination
                runningBalance += dayGroup.Sum(t => t.ProfitCurrency);

                if (!safetyNetReached && runningBalance >= SafetyNet)
                {
                    safetyNetReached = true;
                }
            }

            return adjustedProfit;
        }

        #endregion

        #region Compliance Multiplier Calculation

        /// <summary>
        /// Calculate compliance multiplier based on rule adherence
        /// Returns value between MinComplianceMultiplier (0.5) and 1.0
        /// </summary>
        private double CalculateComplianceMultiplier(List<Trade> trades)
        {
            if (trades.Count == 0) return 1.0;

            double maeViolationRate = CalculateMaeViolationRate(trades);
            double rrViolationRate = CalculateRiskRewardViolationRate(trades);
            double scalingViolationRate = CalculateScalingViolationRate(trades);

            // Weight violations by severity
            // MAE violations most serious (0.5 weight)
            // RR violations serious (0.3 weight)
            // Scaling violations moderate (0.2 weight)
            double totalViolationScore = (maeViolationRate * 0.5) + (rrViolationRate * 0.3) + (scalingViolationRate * 0.2);

            // Convert to multiplier (1.0 = no violations, MinComplianceMultiplier = all violations)
            double multiplier = 1.0 - (totalViolationScore * (1.0 - MinComplianceMultiplier));
            return Math.Max(MinComplianceMultiplier, multiplier);
        }

        /// <summary>
        /// Calculate rate of MAE rule violations
        /// </summary>
        private double CalculateMaeViolationRate(List<Trade> trades)
        {
            var tradesByDay = trades.GroupBy(t => t.Entry.Time.Date).OrderBy(g => g.Key).ToList();

            double runningProfit = 0;
            int violations = 0;

            foreach (var dayGroup in tradesByDay)
            {
                double startOfDayProfit = runningProfit;

                foreach (var trade in dayGroup)
                {
                    double maeLimit = CalculateMaeLimit(startOfDayProfit);
                    double tradeMae = Math.Abs(trade.MaeCurrency);

                    if (tradeMae > maeLimit)
                    {
                        violations++;
                    }
                }

                runningProfit += dayGroup.Sum(t => t.ProfitCurrency);
            }

            return (double)violations / trades.Count;
        }

        /// <summary>
        /// Calculate MAE limit based on profit level
        /// </summary>
        private double CalculateMaeLimit(double startOfDayProfit)
        {
            if (startOfDayProfit >= DoubledSafetyNetThreshold)
            {
                return startOfDayProfit * MaePercentGrown;
            }
            else if (startOfDayProfit > 0)
            {
                return Math.Min(startOfDayProfit * MaePercentNew, MaxMaeNewAccount);
            }
            else
            {
                return MaxMaeNewAccount;
            }
        }

        /// <summary>
        /// Calculate rate of risk-reward ratio violations
        /// </summary>
        private double CalculateRiskRewardViolationRate(List<Trade> trades)
        {
            int violations = 0;

            foreach (var trade in trades)
            {
                double risk = Math.Abs(trade.MaeCurrency);
                double reward = Math.Abs(trade.ProfitCurrency);
                if (reward <= 0) reward = 1;

                if (risk / reward > MaxRiskRewardRatio)
                {
                    violations++;
                }
            }

            return (double)violations / trades.Count;
        }

        /// <summary>
        /// Calculate rate of contract scaling violations
        /// </summary>
        private double CalculateScalingViolationRate(List<Trade> trades)
        {
            var tradesByDay = trades.GroupBy(t => t.Entry.Time.Date).OrderBy(g => g.Key).ToList();

            double runningBalance = InitialBalance;
            bool safetyNetReached = false;
            int violations = 0;
            int tradesBeforeSafetyNet = 0;

            foreach (var dayGroup in tradesByDay)
            {
                foreach (var trade in dayGroup)
                {
                    if (!safetyNetReached)
                    {
                        tradesBeforeSafetyNet++;
                        if ((int)trade.Quantity > ScaledContracts)
                        {
                            violations++;
                        }
                    }
                }

                runningBalance += dayGroup.Sum(t => t.ProfitCurrency);
                if (!safetyNetReached && runningBalance >= SafetyNet)
                {
                    safetyNetReached = true;
                }
            }

            return tradesBeforeSafetyNet > 0 ? (double)violations / tradesBeforeSafetyNet : 0;
        }

        #endregion

        #region Profit Factor and Calmar Ratio

        /// <summary>
        /// Calculate profit factor with risk adjustment
        /// </summary>
        private double CalculateAdjustedProfitFactor(List<Trade> trades)
        {
            double grossProfit = trades.Where(t => t.ProfitCurrency > 0).Sum(t => t.ProfitCurrency);
            double grossLoss = Math.Abs(trades.Where(t => t.ProfitCurrency < 0).Sum(t => t.ProfitCurrency));

            if (grossLoss == 0)
            {
                return grossProfit > 0 ? 10.0 : 0; // Cap at 10 for all-winning strategies
            }

            double profitFactor = grossProfit / grossLoss;

            // Cap at 10 to prevent outlier strategies from dominating
            return Math.Min(profitFactor, 10.0);
        }

        /// <summary>
        /// Calculate Calmar ratio (Net Profit / Max Drawdown)
        /// </summary>
        private double CalculateCalmarRatio(double netProfit, List<Trade> trades)
        {
            if (trades.Count == 0) return 0;

            // Calculate max drawdown from equity curve
            double peak = InitialBalance;
            double maxDrawdown = 0;
            double currentBalance = InitialBalance;

            foreach (var trade in trades.OrderBy(t => t.Entry.Time))
            {
                currentBalance += trade.ProfitCurrency;

                if (currentBalance > peak)
                {
                    peak = currentBalance;
                }

                double drawdown = peak - currentBalance;
                if (drawdown > maxDrawdown)
                {
                    maxDrawdown = drawdown;
                }
            }

            // Avoid division by zero
            if (maxDrawdown <= 0)
            {
                return netProfit > 0 ? 10.0 : 0; // Cap at 10 for no-drawdown strategies
            }

            double calmar = netProfit / maxDrawdown;

            // Cap at 10 to prevent outliers
            return Math.Min(calmar, 10.0);
        }

        #endregion

        #region Payout Eligibility Score

        /// <summary>
        /// Calculate payout eligibility score based on balance progression
        /// Accounts for safety net requirement (first 3 payouts)
        /// </summary>
        private double CalculatePayoutEligibilityScore(double finalBalance, bool reachedSafetyNet)
        {
            double score = 0;

            // Bonus for having positive balance
            if (finalBalance > InitialBalance)
            {
                double profit = finalBalance - InitialBalance;

                // Safety net is required for first 3 payouts
                if (reachedSafetyNet)
                {
                    // Full eligibility - can request payout
                    // Score based on how much above safety net
                    double aboveSafetyNet = finalBalance - SafetyNet;
                    score = 10 + Math.Min(aboveSafetyNet * 0.001, 15); // 10 base + up to 15 more
                }
                else
                {
                    // Partial eligibility - progress toward safety net
                    double progressToSafetyNet = profit / (SafetyNet - InitialBalance);
                    score = progressToSafetyNet * 10; // Up to 10 points for reaching safety net
                }
            }

            return score;
        }

        #endregion
    }
}
