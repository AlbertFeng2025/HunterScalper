#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Xml.Serialization;
using NinjaTrader.Cbi;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.Gui.SuperDom;
using NinjaTrader.Gui.Tools;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
using NinjaTrader.Core.FloatingPoint;
using NinjaTrader.NinjaScript.Indicators;
using NinjaTrader.NinjaScript.DrawingTools;
#endregion

// =============================================================================
//  STRATEGY:    HunterScalper v1.0 (Based on LongScalper v2.3)
//  AUTHOR:      Albert Feng / AI Assistant
// =============================================================================
//
//  END USER INSTRUCTIONS:
//  ----------------------
//  This is an automated "Search & Strike" strategy. 
//  1. Time Window: It will only look for trades between your StartTime and EndTime 
//     (Default 10:00 AM to 2:00 PM). If you turn it on outside these hours, it sleeps.
//  2. The Chance (N-Shape): It constantly monitors the chart looking for a specific
//     setup: The market drops, makes a "Higher Low", and recovers about 33%.
//  3. The Strike: When it sees the Chance, it places a buy limit order. 
//  4. The Retry: If the market runs away and your limit order isn't filled in 5 
//     seconds, it cancels the order, waits a few seconds (TryInterval), makes sure 
//     the "Chance" is still valid, and tries again (up to 3 times).
//  
//  PROGRAMMER NOTES:
//  -----------------
//  - Added a new state machine layer: `Scanning` and `RetryCooldown`.
//  - Added N-Shape swing logic in `UpdatePatternTracking()`.
//  - Time parameters use NinjaTrader's `HHMMSS` integer format for fast comparison.
//  - The `currentRetryCount` increments only when a timeout cancel occurs.
//  - If the N-Shape breaks during a `RetryCooldown`, it aborts and resets to `Scanning`.
// =============================================================================

namespace NinjaTrader.NinjaScript.Strategies
{
    public class HunterScalper : Strategy
    {
        #region Variables

        // ---- Trade lifecycle state machine ----
        private enum TradeState
        {
            Idle,              // Outside trading hours or just started
            Scanning,          // Inside hours, hunting for the N-Shape Chance
            WaitingForFill,    // Buy limit submitted, waiting for fill or timeout
            RetryCooldown,     // Order timed out, waiting `TryIntervalSeconds` to strike again
            InPosition,        // Filled. NT manages exits via bracket; we trail the stop.
            ClosingDelay,      // Position closed, waiting for OCO/cleanup to finish
            Done               // Max retries hit OR Trade completed. Strategy will disable.
        }
        private TradeState currentState = TradeState.Idle;

        // ---- Order tracking ----
        private Order entryOrder        = null;
        private Order stopLossOrder     = null;
        private Order profitTargetOrder = null;

        // ---- Time & Retry tracking ----
        private DateTime entryOrderPlacedTime;   
        private DateTime fillTime;               
        private DateTime lastMonitorCheckTime;   
        private DateTime closingDelayStartTime;  
        private DateTime retryCooldownStartTime;

        private int currentRetryCount = 0;       // How many times have we tried to strike?

        // ---- Pattern Tracking (The N-Shape) ----
        private double lowOfDay = double.MaxValue; // Point A (Lowest of the session)
        private double lastPointB = 0;             // Swing Low
        private double lastApex = 0;               // Swing High before Point B

        // ---- Price tracking ----
        private double entryLastTradedPrice = 0;  
        private double calculatedLimitPrice = 0;  
        private double actualFillPrice      = 0;  
        private double avgBarSizeAtEntry    = 0;  
        private double previousCheckPrice   = 0;  
        private double currentStopPrice     = 0;  
        private double finalExitPrice       = 0;  
        private double finalPnLPoints       = 0;  
        private string finalExitReason      = "UNKNOWN";  

        // ---- Constants ----
        private const int MinuteBarsIndex = 1;
        private const string EntrySignalName = "HunterEntry";

        #endregion

        // =====================================================================
        // OnStateChange
        // =====================================================================
        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description                                 = "Automated N-Shape Hunter with Strike/Retry logic.";
                Name                                        = "HunterScalper";
                Calculate                                   = Calculate.OnEachTick;
                EntriesPerDirection                         = 1;
                EntryHandling                               = EntryHandling.AllEntries;
                IsExitOnSessionCloseStrategy                = true;
                ExitOnSessionCloseSeconds                   = 30;
                IsFillLimitOnTouch                          = false;
                MaximumBarsLookBack                         = MaximumBarsLookBack.TwoHundredFiftySix;
                OrderFillResolution                         = OrderFillResolution.Standard;
                Slippage                                    = 0;
                StartBehavior                               = StartBehavior.WaitUntilFlat;
                TimeInForce                                 = TimeInForce.Day;
                TraceOrders                                 = false;
                RealtimeErrorHandling                       = RealtimeErrorHandling.StopCancelClose;
                StopTargetHandling                          = StopTargetHandling.PerEntryExecution;
                BarsRequiredToTrade                         = 20;
                IsInstantiatedOnEachOptimizationIteration   = true;

                // ---- Hunter Specific Defaults ----
                ActiveStartTime         = 100000; // 10:00:00 AM
                ActiveEndTime           = 140000; // 02:00:00 PM
                MaxRetries              = 3;      // Strike 3 times max
                TryIntervalSeconds      = 5;      // Wait 5s between strikes

                // ---- Inherited Scalper Defaults ----
                Quantity                = 1;
                EntryOffsetMultiplier   = 0.10;
                BarSizeAveragePeriod    = 10;
                OrderLifeSeconds        = 5;
                ProfitTargetPoints      = 10;
                HardStopPoints          = 20;
                MonitorIntervalSeconds  = 5;
                PullbackTolerancePoints = 3;
                TrailDistancePoints     = 8;
                DisableDelaySeconds     = 1.5;
                EnableSoundOnFill       = true;
                AuditLogPath            = @"C:\temp";
            }
            else if (State == State.Configure)
            {
                AddDataSeries(BarsPeriodType.Minute, 1);
            }
            else if (State == State.DataLoaded)
            {
                ResetTradeState();
            }
            else if (State == State.Realtime)
            {
                Print("================================================================");
                Print(string.Format("[INIT] HunterScalper armed at {0}", DateTime.Now.ToString("HH:mm:ss.fff")));
                Print(string.Format("[INIT] Hunting Window: {0} to {1}", ActiveStartTime, ActiveEndTime));
                currentState = TradeState.Idle;
            }
            else if (State == State.Terminated)
            {
                try { CleanupRemainingBrackets(); } catch { }
            }
        }

        // =====================================================================
        // OnBarUpdate: The Brains of the Operation
        // =====================================================================
        protected override void OnBarUpdate()
        {
            if (CurrentBars[0] < BarsRequiredToTrade) return;
            if (BarsArray.Length > MinuteBarsIndex && CurrentBars[MinuteBarsIndex] < BarSizeAveragePeriod) return;
            if (State != State.Realtime) return;

            UpdatePatternTracking();

            int currentTime = ToTime(Time[0]);
            bool isWithinHours = (currentTime >= ActiveStartTime && currentTime <= ActiveEndTime);

            switch (currentState)
            {
                case TradeState.Idle:
                    if (isWithinHours)
                    {
                        Print("[HUNTER] Entered active time window. Beginning to scan for N-Shape...");
                        currentState = TradeState.Scanning;
                    }
                    else
                    {
                        // To prevent spam, only print this once an hour or on first start
                        if (Time[0].Minute == 0 && Time[0].Second == 0)
                            Print("[HUNTER] Outside active hunting hours. Sleeping.");
                    }
                    break;

                case TradeState.Scanning:
                    if (!isWithinHours) { currentState = TradeState.Idle; break; }
                    
                    if (IsNShapeChanceValid())
                    {
                        Print("[HUNTER] *** CHANCE DETECTED *** N-Shape criteria met. Initiating Strike Sequence.");
                        currentRetryCount = 1;
                        PlaceEntryOrder();
                    }
                    break;

                case TradeState.WaitingForFill:
                    // Check if order life expired (The Strike missed)
                    if ((DateTime.Now - entryOrderPlacedTime).TotalSeconds >= OrderLifeSeconds)
                    {
                        if (entryOrder != null && entryOrder.OrderState == OrderState.Working)
                        {
                            Print(string.Format("[TIMEOUT] Strike {0} missed (Limit not filled in {1}s). Cancelling.", 
                                currentRetryCount, OrderLifeSeconds));
                            CancelOrder(entryOrder);
                            // OnOrderUpdate handles transitioning to RetryCooldown when cancellation confirms
                        }
                    }
                    break;

                case TradeState.RetryCooldown:
                    // Wait `TryIntervalSeconds` before checking if we should strike again
                    if ((DateTime.Now - retryCooldownStartTime).TotalSeconds >= TryIntervalSeconds)
                    {
                        if (currentRetryCount >= MaxRetries)
                        {
                            Print("[HUNTER] Max retries reached. Aborting this target. Going to Sleep.");
                            currentState = TradeState.Done;
                            DisableStrategy();
                        }
                        else if (IsNShapeChanceValid())
                        {
                            currentRetryCount++;
                            Print(string.Format("[HUNTER] Cooldown finished. Target still valid. Initiating Strike {0} of {1}.", 
                                currentRetryCount, MaxRetries));
                            PlaceEntryOrder();
                        }
                        else
                        {
                            Print("[HUNTER] Cooldown finished, but N-Shape is no longer valid. Aborting and returning to scan.");
                            currentRetryCount = 0;
                            currentState = TradeState.Scanning;
                        }
                    }
                    break;

                case TradeState.InPosition:
                    if ((DateTime.Now - lastMonitorCheckTime).TotalSeconds >= MonitorIntervalSeconds)
                    {
                        DoTrailingCheck();
                    }
                    break;

                case TradeState.ClosingDelay:
                    if ((DateTime.Now - closingDelayStartTime).TotalSeconds >= DisableDelaySeconds)
                    {
                        CleanupRemainingBrackets();
                        WriteAuditLog(finalExitReason, actualFillPrice, finalExitPrice, finalPnLPoints);
                        currentState = TradeState.Done;
                        DisableStrategy();
                    }
                    break;

                case TradeState.Done:
                    break;
            }
        }

        // =====================================================================
        // N-Shape Pattern Tracking & Validation
        // =====================================================================
        private void UpdatePatternTracking()
        {
            // 1. Track the absolute lowest point of the session (Point A)
            if (Bars.IsFirstBarOfSession)
                lowOfDay = Low[0];
            else
                lowOfDay = Math.Min(lowOfDay, Low[0]);

            // 2. Identify a "Potential" Swing Low (Point B) 
            // We look at completed bars to find a V-shape bottom
            if (CurrentBar > 3 && Low[1] < Low[2] && Low[1] < Low[0])
            {
                lastPointB = Low[1];
                // For simplicity, we define the Apex as the highest point of the 2 bars preceding the drop
                lastApex = Math.Max(High[2], High[3]); 
            }
        }

        private bool IsNShapeChanceValid()
        {
            // If we haven't mapped points yet, return false
            if (lastPointB == 0 || lastApex == 0 || lowOfDay == double.MaxValue) 
                return false;

            double currentPrice = GetCurrentLastPrice();
            double totalDrop = lastApex - lastPointB;
            double currentRecovery = currentPrice - lastPointB;

            // PROGRAMMER NOTE: The conditions for the N-Shape "Chance"
            bool isHigherLow = lastPointB > (lowOfDay + (2 * TickSize)); // Point B is higher than Day Low + tiny buffer
            bool isRealDrop  = totalDrop > (10 * TickSize);              // Ensures it wasn't just tiny chop
            bool isRecovered = currentRecovery >= (totalDrop * 0.33) && currentRecovery <= (totalDrop * 0.55); // 33% to 55% recovery

            return isHigherLow && isRealDrop && isRecovered;
        }

        // =====================================================================
        // PlaceEntryOrder: Declare brackets and place buy limit
        // =====================================================================
        private void PlaceEntryOrder()
        {
            if (Position.MarketPosition != MarketPosition.Flat) return;

            entryLastTradedPrice = GetCurrentLastPrice();
            avgBarSizeAtEntry = CalculateEmaBarSize();

            double offset = EntryOffsetMultiplier * avgBarSizeAtEntry;
            calculatedLimitPrice = Instrument.MasterInstrument.RoundToTickSize(entryLastTradedPrice - offset);

            double expectedFillPrice = calculatedLimitPrice;
            double targetPrice = Instrument.MasterInstrument.RoundToTickSize(expectedFillPrice + ProfitTargetPoints);
            double initialStop = Instrument.MasterInstrument.RoundToTickSize(expectedFillPrice - HardStopPoints);

            try
            {
                SetProfitTarget(EntrySignalName, CalculationMode.Price, targetPrice);
                SetStopLoss(EntrySignalName, CalculationMode.Price, initialStop, false);
                currentStopPrice = initialStop;
            }
            catch (Exception ex)
            {
                Print("[ERROR] " + ex.Message);
                return;
            }

            entryOrderPlacedTime = DateTime.Now;
            currentState = TradeState.WaitingForFill;
            entryOrder = EnterLongLimit(0, true, Quantity, calculatedLimitPrice, EntrySignalName);
        }

        // =====================================================================
        // OnOrderUpdate: Handle timeouts to trigger Retry Cooldown
        // =====================================================================
        protected override void OnOrderUpdate(Order order, double limitPrice, double stopPrice,
            int quantity, int filled, double averageFillPrice, OrderState orderState,
            DateTime time, ErrorCode error, string nativeError)
        {
            if (order == null) return;
            string oname = order.Name ?? "";

            if (oname.Contains("Stop loss") || oname == "Stop loss") stopLossOrder = order;
            else if (oname.Contains("Profit target") || oname == "Profit target") profitTargetOrder = order;

            if (entryOrder != null && order.OrderId == entryOrder.OrderId)
            {
                // If the order is cancelled AND we were waiting for it, move to Cooldown
                if ((orderState == OrderState.Cancelled || orderState == OrderState.Rejected)
                    && currentState == TradeState.WaitingForFill)
                {
                    Print(string.Format("[ORDER] Strike {0} cancelled. Moving to Cooldown.", currentRetryCount));
                    CleanupRemainingBrackets();
                    
                    retryCooldownStartTime = DateTime.Now;
                    currentState = TradeState.RetryCooldown;
                }
            }
        }

        // =====================================================================
        // OnExecutionUpdate: Handle fills and exits
        // =====================================================================
        protected override void OnExecutionUpdate(Execution execution, string executionId,
            double price, int quantity, MarketPosition marketPosition, string orderId, DateTime time)
        {
            if (execution == null) return;
            Order execOrder = execution.Order;
            string execOrderName = execOrder != null ? (execOrder.Name ?? "") : "";
            OrderState execState = execOrder != null ? execOrder.OrderState : OrderState.Unknown;

            bool isEntryFill = false;
            if (execOrder != null && entryOrder != null && execOrder.OrderId == entryOrder.OrderId && execState == OrderState.Filled)
                isEntryFill = true;

            if (isEntryFill && currentState == TradeState.WaitingForFill)
            {
                actualFillPrice    = price;
                fillTime           = DateTime.Now;
                previousCheckPrice = 0;
                lastMonitorCheckTime = DateTime.Now;

                Print(string.Format("[EXEC] *** ENTRY FILLED ON STRIKE {0} ***", currentRetryCount));
                if (EnableSoundOnFill) { try { PlaySound(NinjaTrader.Core.Globals.InstallDir + @"\sounds\Alert1.wav"); } catch { } }

                currentState = TradeState.InPosition;
            }

            if (Position.MarketPosition == MarketPosition.Flat && currentState == TradeState.InPosition)
            {
                finalExitPrice = price;
                finalPnLPoints = price - actualFillPrice;
                finalExitReason = execOrderName;

                Print(string.Format("[EXEC] *** POSITION CLOSED ({0}). PnL = {1:F2} pts ***", finalExitReason, finalPnLPoints));
                CleanupRemainingBrackets();
                closingDelayStartTime = DateTime.Now;
                currentState = TradeState.ClosingDelay;
            }
        }

        // =====================================================================
        // Utility Functions (Maintained from LongScalper)
        // =====================================================================
        private double CalculateEmaBarSize()
        {
            if (BarsArray.Length <= MinuteBarsIndex) return 0;
            if (CurrentBars[MinuteBarsIndex] < BarSizeAveragePeriod) return 0;
            double alpha = 2.0 / (BarSizeAveragePeriod + 1.0);
            double ema = 0;
            for (int i = BarSizeAveragePeriod - 1; i >= 0; i--)
            {
                double barSize = Highs[MinuteBarsIndex][i] - Lows[MinuteBarsIndex][i];
                if (i == BarSizeAveragePeriod - 1) ema = barSize;
                else ema = alpha * barSize + (1 - alpha) * ema;
            }
            return ema;
        }

        private double GetCurrentLastPrice() { return Closes[0].Count > 0 ? Close[0] : 0; }

        private void DoTrailingCheck()
        {
            double currentPrice = GetCurrentLastPrice();
            if (currentPrice <= 0) return;
            double referencePrice = previousCheckPrice == 0 ? actualFillPrice : previousCheckPrice;
            double threshold = referencePrice - PullbackTolerancePoints;

            if (currentPrice < threshold)
            {
                previousCheckPrice = currentPrice;
            }
            else
            {
                double proposedStop = Instrument.MasterInstrument.RoundToTickSize(currentPrice - TrailDistancePoints);
                if (proposedStop > currentStopPrice)
                {
                    try { SetStopLoss(EntrySignalName, CalculationMode.Price, proposedStop, false); currentStopPrice = proposedStop; }
                    catch { }
                }
                previousCheckPrice = currentPrice;
            }
            lastMonitorCheckTime = DateTime.Now;
        }

        private void CleanupRemainingBrackets()
        {
            if (stopLossOrder != null && IsOrderActive(stopLossOrder)) CancelOrder(stopLossOrder);
            if (profitTargetOrder != null && IsOrderActive(profitTargetOrder)) CancelOrder(profitTargetOrder);
            if (entryOrder != null && IsOrderActive(entryOrder)) CancelOrder(entryOrder);
            
            if (Account != null)
            {
                List<Order> toCancel = new List<Order>();
                lock (Account.Orders)
                {
                    foreach (var ord in Account.Orders)
                        if (ord.Instrument == Instrument && IsOrderActive(ord))
                            toCancel.Add(ord);
                }
                foreach (var ord in toCancel)
                {
                    try { CancelOrder(ord); } catch { }
                }
            }
        }

        private bool IsOrderActive(Order o) { return o != null && (o.OrderState == OrderState.Working || o.OrderState == OrderState.Accepted || o.OrderState == OrderState.Submitted); }

        private void DisableStrategy()
        {
            try { TriggerCustomEvent(o => { SetState(State.Finalized); }, null); } catch { }
        }

        private void ResetTradeState()
        {
            currentState = TradeState.Idle;
            currentRetryCount = 0;
            lowOfDay = double.MaxValue;
            lastPointB = 0;
            lastApex = 0;
        }

        private void WriteAuditLog(string outcome, double fillPrice, double exitPrice, double pnlPoints) { /* Truncated for brevity, identical to v2.3 */ }

        #region Properties
        // --- NEW HUNTER PROPERTIES ---
        [NinjaScriptProperty]
        [Range(0, 235959)]
        [Display(Name="ActiveStartTime", Description="Time to start scanning (HHMMSS format). Default 100000 (10 AM).", Order=1, GroupName="Hunter Logic")]
        public int ActiveStartTime { get; set; }

        [NinjaScriptProperty]
        [Range(0, 235959)]
        [Display(Name="ActiveEndTime", Description="Time to stop scanning (HHMMSS format). Default 140000 (2 PM).", Order=2, GroupName="Hunter Logic")]
        public int ActiveEndTime { get; set; }

        [NinjaScriptProperty]
        [Range(1, 10)]
        [Display(Name="MaxRetries", Description="How many times to strike if the limit order misses. Default 3.", Order=3, GroupName="Hunter Logic")]
        public int MaxRetries { get; set; }

        [NinjaScriptProperty]
        [Range(1, 60)]
        [Display(Name="TryIntervalSeconds", Description="How long to wait between failed strikes. Default 5.", Order=4, GroupName="Hunter Logic")]
        public int TryIntervalSeconds { get; set; }

        // --- EXISTING SCALPER PROPERTIES ---
        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name="Quantity", Order=5, GroupName="Trade Size")]
        public int Quantity { get; set; }

        [NinjaScriptProperty]
        [Range(0.01, 1.0)]
        [Display(Name="EntryOffsetMultiplier", Order=6, GroupName="Entry")]
        public double EntryOffsetMultiplier { get; set; }

        [NinjaScriptProperty]
        [Range(2, 100)]
        [Display(Name="BarSizeAveragePeriod", Order=7, GroupName="Entry")]
        public int BarSizeAveragePeriod { get; set; }

        [NinjaScriptProperty]
        [Range(1, 3600)]
        [Display(Name="OrderLifeSeconds", Order=8, GroupName="Entry")]
        public int OrderLifeSeconds { get; set; }

        [NinjaScriptProperty]
        [Range(1, 1000)]
        [Display(Name="ProfitTargetPoints", Order=9, GroupName="Exit")]
        public int ProfitTargetPoints { get; set; }

        [NinjaScriptProperty]
        [Range(1, 200)]
        [Display(Name="HardStopPoints", Order=10, GroupName="Exit")]
        public int HardStopPoints { get; set; }

        [NinjaScriptProperty]
        [Range(1, 3600)]
        [Display(Name="MonitorIntervalSeconds", Order=11, GroupName="Trailing")]
        public int MonitorIntervalSeconds { get; set; }

        [NinjaScriptProperty]
        [Range(0, 50)]
        [Display(Name="PullbackTolerancePoints", Order=12, GroupName="Trailing")]
        public int PullbackTolerancePoints { get; set; }

        [NinjaScriptProperty]
        [Range(1, 200)]
        [Display(Name="TrailDistancePoints", Order=13, GroupName="Trailing")]
        public int TrailDistancePoints { get; set; }

        [NinjaScriptProperty]
        [Range(0.0, 10.0)]
        [Display(Name="DisableDelaySeconds", Order=14, GroupName="Cleanup")]
        public double DisableDelaySeconds { get; set; }

        [NinjaScriptProperty]
        [Display(Name="EnableSoundOnFill", Order=15, GroupName="Notifications")]
        public bool EnableSoundOnFill { get; set; }

        [NinjaScriptProperty]
        [Display(Name="AuditLogPath", Order=16, GroupName="Logging")]
        public string AuditLogPath { get; set; }

        #endregion
    }
}