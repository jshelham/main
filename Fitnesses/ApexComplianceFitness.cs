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
    /// ApexComplianceFitness - Optimization fitness for NinjaTrader 8 Strategy Analyzer
    ///
    /// Purpose: Penalize strategies that violate Apex Trader Funding $250K PA rules;
    /// reward rule-compliant trading behavior.
    ///
    /// Metrics Calculated:
    /// 1. MAE Violation Score - Tracks Maximum Adverse Excursion compliance
    /// 2. Risk-Reward Ratio Compliance - Ensures 5:1 max risk-reward ratio
    /// 3. Contract Scaling Compliance - Enforces position size limits before safety net
    /// 4. Trailing Drawdown Survival - Simulates Apex trailing drawdown mechanism
    ///
    /// Fitness Score: 100 - (MAE_Penalty + RR_Penalty + Scaling_Penalty + Drawdown_Penalty)
    /// Higher score = better compliance (100 = perfect compliance)
    /// </summary>
    public class ApexComplianceFitness : OptimizationFitness
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
        /// Scaled contracts allowed before safety net reached (half of max)
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
        /// MAE percentage for new/low profit accounts (30%)
        /// </summary>
        [Display(Name = "MAE Percent New", Description = "MAE limit as percent of profit for new accounts", Order = 1, GroupName = "Rule Thresholds")]
        public double MaePercentNew { get; set; }

        /// <summary>
        /// MAE percentage for grown accounts (50%)
        /// </summary>
        [Display(Name = "MAE Percent Grown", Description = "MAE limit as percent of profit for grown accounts", Order = 2, GroupName = "Rule Thresholds")]
        public double MaePercentGrown { get; set; }

        /// <summary>
        /// Maximum risk-to-reward ratio allowed (5:1)
        /// </summary>
        [Display(Name = "Max Risk Reward Ratio", Description = "Maximum allowed risk-to-reward ratio", Order = 3, GroupName = "Rule Thresholds")]
        public double MaxRiskRewardRatio { get; set; }

        /// <summary>
        /// Profit threshold where MAE switches to 50% ($13,200)
        /// </summary>
        [Display(Name = "Doubled Safety Net Threshold", Description = "Profit where 50% MAE applies", Order = 4, GroupName = "Rule Thresholds")]
        public double DoubledSafetyNetThreshold { get; set; }

        /// <summary>
        /// Maximum MAE for new accounts (30% of $6,500 = $1,950)
        /// </summary>
        [Display(Name = "Max MAE New Account", Description = "Absolute max MAE for new accounts", Order = 5, GroupName = "Rule Thresholds")]
        public double MaxMaeNewAccount { get; set; }

        #endregion

        #region Penalty Weights

        /// <summary>
        /// Weight multiplier for MAE violations
        /// </summary>
        [Display(Name = "MAE Penalty Weight", Description = "Weight for MAE violation penalties", Order = 1, GroupName = "Penalty Weights")]
        public double MaePenaltyWeight { get; set; }

        /// <summary>
        /// Weight multiplier for risk-reward violations
        /// </summary>
        [Display(Name = "RR Penalty Weight", Description = "Weight for risk-reward violation penalties", Order = 2, GroupName = "Penalty Weights")]
        public double RrPenaltyWeight { get; set; }

        /// <summary>
        /// Weight multiplier for contract scaling violations
        /// </summary>
        [Display(Name = "Scaling Penalty Weight", Description = "Weight for contract scaling violation penalties", Order = 3, GroupName = "Penalty Weights")]
        public double ScalingPenaltyWeight { get; set; }

        /// <summary>
        /// Weight multiplier for drawdown proximity
        /// </summary>
        [Display(Name = "Drawdown Penalty Weight", Description = "Weight for drawdown proximity penalties", Order = 4, GroupName = "Penalty Weights")]
        public double DrawdownPenaltyWeight { get; set; }

        #endregion

        /// <summary>
        /// Constructor - Initialize default values for Apex $250K PA
        /// </summary>
        public ApexComplianceFitness()
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

            // Penalty weights
            MaePenaltyWeight = 1.0;
            RrPenaltyWeight = 1.0;
            ScalingPenaltyWeight = 1.0;
            DrawdownPenaltyWeight = 1.0;
        }

        /// <summary>
        /// Calculate the compliance fitness value for the strategy
        /// </summary>
        /// <returns>Fitness score (higher = better compliance, 100 = perfect)</returns>
        protected override void OnCalculatePerformanceValue()
        {
            // Validate that we have trades to analyze
            if (SystemPerformance == null || SystemPerformance.AllTrades == null || SystemPerformance.AllTrades.Count == 0)
            {
                Value = 0;
                return;
            }

            try
            {
                // Get all trades sorted by entry time
                var trades = SystemPerformance.AllTrades
                    .OrderBy(t => t.Entry.Time)
                    .ToList();

                // Calculate each compliance metric
                double maePenalty = CalculateMaePenalty(trades);
                double rrPenalty = CalculateRiskRewardPenalty(trades);
                double scalingPenalty = CalculateScalingPenalty(trades);
                double drawdownPenalty = CalculateDrawdownPenalty(trades);

                // Apply weights
                maePenalty *= MaePenaltyWeight;
                rrPenalty *= RrPenaltyWeight;
                scalingPenalty *= ScalingPenaltyWeight;
                drawdownPenalty *= DrawdownPenaltyWeight;

                // Calculate final compliance score
                double totalPenalty = maePenalty + rrPenalty + scalingPenalty + drawdownPenalty;
                double complianceScore = 100 - totalPenalty;

                // If account would have blown, return heavy negative score
                if (drawdownPenalty >= 100)
                {
                    Value = -100;
                    return;
                }

                Value = complianceScore;
            }
            catch (Exception ex)
            {
                // Log error and return 0 fitness
                NinjaTrader.Code.Output.Process($"ApexComplianceFitness Error: {ex.Message}", PrintTo.OutputTab1);
                Value = 0;
            }
        }

        #region MAE Violation Calculation

        /// <summary>
        /// Calculate penalty for Maximum Adverse Excursion violations
        /// Apex Rule: Per-trade unrealized loss cannot exceed 30% of start-of-day profit
        /// For new accounts: Max MAE = MIN(30% × start-of-day profit, $1,950)
        /// For grown accounts (profit >= $13,200): Max MAE = 50% × start-of-day profit
        /// </summary>
        private double CalculateMaePenalty(List<Trade> trades)
        {
            if (trades.Count == 0) return 0;

            // Group trades by day to calculate start-of-day profit
            var tradesByDay = trades.GroupBy(t => t.Entry.Time.Date).OrderBy(g => g.Key).ToList();

            double runningProfit = 0;
            int totalViolations = 0;
            double totalViolationSeverity = 0;

            foreach (var dayGroup in tradesByDay)
            {
                double startOfDayProfit = runningProfit;

                foreach (var trade in dayGroup.OrderBy(t => t.Entry.Time))
                {
                    // Calculate MAE limit based on profit level
                    double maeLimit = CalculateMaeLimit(startOfDayProfit);

                    // Get trade's actual MAE (positive value representing max adverse excursion)
                    double tradeMae = Math.Abs(trade.MaeCurrency);

                    // Check for violation
                    if (tradeMae > maeLimit)
                    {
                        totalViolations++;
                        double severity = (tradeMae - maeLimit) / maeLimit;
                        totalViolationSeverity += severity;
                    }

                    // Update running profit for subsequent trades within the same day
                    // Note: Start-of-day profit remains constant for same-day trades
                }

                // Update running profit at end of day
                runningProfit += dayGroup.Sum(t => t.ProfitCurrency);
            }

            // Calculate penalty based on violation frequency and severity
            if (trades.Count == 0) return 0;

            double violationRate = (double)totalViolations / trades.Count;
            double avgSeverity = totalViolations > 0 ? totalViolationSeverity / totalViolations : 0;

            // Penalty formula: Combines frequency and severity
            // Max 50 points for violations (very serious compliance issue)
            double penalty = (violationRate * 30) + (avgSeverity * 20);
            return Math.Min(penalty, 50);
        }

        /// <summary>
        /// Calculate the MAE limit based on current profit level
        /// </summary>
        private double CalculateMaeLimit(double startOfDayProfit)
        {
            if (startOfDayProfit >= DoubledSafetyNetThreshold)
            {
                // Account has grown significantly - 50% MAE allowed
                return startOfDayProfit * MaePercentGrown;
            }
            else if (startOfDayProfit > 0)
            {
                // Normal MAE calculation - 30% of profit, capped at $1,950
                double calculatedLimit = startOfDayProfit * MaePercentNew;
                return Math.Min(calculatedLimit, MaxMaeNewAccount);
            }
            else
            {
                // No profit yet - use max MAE for new accounts
                return MaxMaeNewAccount;
            }
        }

        #endregion

        #region Risk-Reward Ratio Calculation

        /// <summary>
        /// Calculate penalty for risk-reward ratio violations
        /// Apex Rule: Stop loss cannot exceed 5× the profit target
        /// </summary>
        private double CalculateRiskRewardPenalty(List<Trade> trades)
        {
            if (trades.Count == 0) return 0;

            int violations = 0;
            double totalSeverity = 0;

            foreach (var trade in trades)
            {
                // Calculate actual risk (MAE) vs reward (profit if winning, or potential based on entry/exit)
                double risk = Math.Abs(trade.MaeCurrency);
                double reward = trade.ProfitCurrency > 0 ? trade.ProfitCurrency : Math.Abs(trade.ProfitCurrency);

                // Avoid division by zero
                if (reward <= 0) reward = 1;

                double riskRewardRatio = risk / reward;

                // Check if ratio exceeds 5:1
                if (riskRewardRatio > MaxRiskRewardRatio)
                {
                    violations++;
                    double severity = (riskRewardRatio - MaxRiskRewardRatio) / MaxRiskRewardRatio;
                    totalSeverity += severity;
                }
            }

            // Calculate penalty
            double violationRate = (double)violations / trades.Count;
            double avgSeverity = violations > 0 ? totalSeverity / violations : 0;

            // Max 30 points for risk-reward violations (serious but less than MAE)
            double penalty = (violationRate * 20) + (avgSeverity * 10);
            return Math.Min(penalty, 30);
        }

        #endregion

        #region Contract Scaling Calculation

        /// <summary>
        /// Calculate penalty for contract scaling violations
        /// Apex Rule: Before EOD balance exceeds $256,600, can only trade 13 contracts (half of 27)
        /// </summary>
        private double CalculateScalingPenalty(List<Trade> trades)
        {
            if (trades.Count == 0) return 0;

            // Track account balance progression by end of each day
            var tradesByDay = trades.GroupBy(t => t.Entry.Time.Date).OrderBy(g => g.Key).ToList();

            double runningBalance = InitialBalance;
            bool safetyNetReached = false;
            int violations = 0;
            int tradesBeforeSafetyNet = 0;

            foreach (var dayGroup in tradesByDay)
            {
                // Check each trade's contract size
                foreach (var trade in dayGroup.OrderBy(t => t.Entry.Time))
                {
                    int contractsUsed = (int)trade.Quantity;

                    if (!safetyNetReached)
                    {
                        tradesBeforeSafetyNet++;

                        // Check if using more than scaled contracts before safety net
                        if (contractsUsed > ScaledContracts)
                        {
                            violations++;
                        }
                    }
                    else
                    {
                        // After safety net, check against max contracts
                        if (contractsUsed > MaxContracts)
                        {
                            violations++;
                        }
                    }
                }

                // Update EOD balance
                runningBalance += dayGroup.Sum(t => t.ProfitCurrency);

                // Check if safety net reached at EOD
                if (!safetyNetReached && runningBalance >= SafetyNet)
                {
                    safetyNetReached = true;
                }
            }

            // Calculate penalty based on violations before safety net
            if (tradesBeforeSafetyNet == 0) return 0;

            double violationRate = (double)violations / tradesBeforeSafetyNet;

            // Max 20 points for scaling violations (important but fixable)
            return Math.Min(violationRate * 20, 20);
        }

        #endregion

        #region Trailing Drawdown Calculation

        /// <summary>
        /// Calculate penalty for trailing drawdown proximity and breaches
        /// Simulates Apex trailing drawdown mechanism:
        /// - Starts at $243,500 ($250,000 - $6,500)
        /// - Trails upward with peak unrealized balance
        /// - Stops trailing when peak reaches $256,600 (safety net)
        /// - After safety net, floor locks at $250,100
        /// </summary>
        private double CalculateDrawdownPenalty(List<Trade> trades)
        {
            if (trades.Count == 0) return 0;

            double currentBalance = InitialBalance;
            double peakBalance = InitialBalance;
            double drawdownFloor = InitialBalance - TrailingDrawdown;
            bool safetyNetReached = false;
            bool accountBlown = false;

            double minDistanceToFloor = TrailingDrawdown; // Start with max distance
            double totalRiskExposure = 0;
            int highRiskTrades = 0;

            foreach (var trade in trades.OrderBy(t => t.Entry.Time))
            {
                // Simulate unrealized P&L at worst point (MAE)
                double unrealizedWorst = currentBalance - Math.Abs(trade.MaeCurrency);

                // Check for breach at worst unrealized point
                if (unrealizedWorst <= drawdownFloor)
                {
                    accountBlown = true;
                    break;
                }

                // Track minimum distance to floor during trade
                double distanceToFloor = unrealizedWorst - drawdownFloor;
                if (distanceToFloor < minDistanceToFloor)
                {
                    minDistanceToFloor = distanceToFloor;
                }

                // Track high-risk trades (within 50% of drawdown)
                if (distanceToFloor < TrailingDrawdown * 0.5)
                {
                    highRiskTrades++;
                    totalRiskExposure += (TrailingDrawdown * 0.5 - distanceToFloor) / (TrailingDrawdown * 0.5);
                }

                // Update balance after trade
                currentBalance += trade.ProfitCurrency;

                // Update peak balance and drawdown floor
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

                // Final breach check after trade closes
                if (currentBalance <= drawdownFloor)
                {
                    accountBlown = true;
                    break;
                }
            }

            // Calculate penalty
            if (accountBlown)
            {
                // Account blown - maximum penalty (returns 100+)
                return 150;
            }

            // Calculate risk score based on proximity to drawdown
            double proximityRisk = 1 - (minDistanceToFloor / TrailingDrawdown);
            proximityRisk = Math.Max(0, Math.Min(1, proximityRisk));

            // Calculate high-risk trade penalty
            double highRiskPenalty = trades.Count > 0 ? (double)highRiskTrades / trades.Count * 10 : 0;

            // Combine proximity risk and high-risk trade penalties
            // Max 25 points for drawdown risk (if account survives)
            double penalty = (proximityRisk * 15) + highRiskPenalty;
            return Math.Min(penalty, 25);
        }

        #endregion
    }
}
