using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.IO;
using ATAS.DataFeedsCore;
using ATAS.Indicators;
using ATAS.Strategies.Chart;

namespace CashReaper
{
    public class CashReaperStrategy : ChartStrategy
    {
        private static readonly object StatsFileLock = new object();
        private enum TradeState
        {
            Idle,
            EntryOrderSent,
            PositionOpen,
            ProtectiveOrdersActive
        }

        private enum AccountStopReason
        {
            None,
            Profit,
            Loss
        }

        public enum ProtectionMode
        {
            Points = 0,
            PricePercent = 1,
            DepositPercent = 2
        }

        public enum SeriesMode
        {
            Linear = 1,
            Martingale = 2
        }

        public enum ProfitTargetUnit
        {
            AccountCurrency = 0,
            Points = 1
        }

        public enum MoneyPnlSource
        {
            AtasPortfolio = 0,
            StrategyTradesEstimate = 1
        }

        public enum TrailingStopMode
        {
            Off = 0,
            Price = 1,
            ClosedBar = 2
        }

        [Display(GroupName = "01. Trading", Name = "Enable trading", Order = 10)]
        public bool TradingEnabled { get; set; } = true;

        [Display(GroupName = "02. Replay and diagnostics", Name = "Market Replay mode", Order = 20)]
        public bool MarketReplayMode { get; set; } = true;

        [Display(GroupName = "02. Replay and diagnostics", Name = "Collect CSV statistics", Order = 30)]
        public bool StatisticsCollectorEnabled { get; set; } = true;

        [Display(GroupName = "02. Replay and diagnostics", Name = "Collect signal footprints", Description = "Record Bid/Ask at every price level of an engulfing signal bar for offline imbalance analysis. Does not affect orders.", Order = 31)]
        public bool FootprintCollectorEnabled { get; set; } = false;

        [Display(GroupName = "02. Replay and diagnostics", Name = "Collect replay ticks", Description = "Record each OnNewTrade print for offline replay. Enable only for a diagnostic Market Replay; this can create a large CSV file.", Order = 32)]
        public bool TickCollectorEnabled { get; set; } = false;

        [Display(GroupName = "02. Replay and diagnostics", Name = "Collect Level 2 updates", Description = "Record the initial order-book snapshot and subsequent MarketDepthChanged updates in the same ordered market-event log as trades. Use only for a short diagnostic replay first.", Order = 33)]
        public bool DepthCollectorEnabled { get; set; } = false;

        [Display(GroupName = "02. Replay and diagnostics", Name = "Show CashReaper notifications", Description = "Show popup notifications from this strategy, including warnings. Does not change order handling or CSV statistics.", Order = 39)]
        public bool ShowNotifications { get; set; } = false;

        [Display(GroupName = "02. Replay and diagnostics", Name = "Debug notifications", Order = 40)]
        public bool DebugMode { get; set; } = true;

        [Display(GroupName = "03. Recovery", Name = "Auto recovery", Order = 50)]
        public bool AutoRecoveryEnabled { get; set; } = true;

        [Display(GroupName = "03. Recovery", Name = "Skip old signal on start", Order = 60)]
        public bool SkipOldSignalOnStart { get; set; } = true;

        [Display(GroupName = "03. Recovery", Name = "Restore TP/SL on start", Order = 70)]
        public bool RestoreProtectiveOrdersOnStart { get; set; } = true;

        [Display(GroupName = "04. Time filter", Name = "Use trading pause", Order = 80)]
        public bool TradingTimeLimitEnabled { get; set; } = false;

        [Display(GroupName = "04. Time filter", Name = "Close before trading pause", Description = "One minute before Pause start, cancel entries, close this strategy's position and block entries until Pause end. Requires Use trading pause.", Order = 85)]
        public bool CloseBeforeTradingPause { get; set; } = true;

        [Display(GroupName = "04. Time filter", Name = "Use session hours", Description = "Use the exchange session times entered below in robot time. ATAS chart candles do not provide a reliable exchange-session close schedule.", Order = 86)]
        public bool UseSessionHours { get; set; } = true;

        [Display(GroupName = "04. Time filter", Name = "Session open hour", Order = 87)]
        public int SessionOpenHour { get; set; } = 7;

        [Display(GroupName = "04. Time filter", Name = "Session open minute", Order = 88)]
        public int SessionOpenMinute { get; set; } = 0;

        [Display(GroupName = "04. Time filter", Name = "Session close hour", Order = 89)]
        public int SessionCloseHour { get; set; } = 2;

        [Display(GroupName = "04. Time filter", Name = "Session close minute", Order = 90)]
        public int SessionCloseMinute { get; set; } = 29;

        [Display(GroupName = "04. Time filter", Name = "Close before session end", Description = "One minute before Session close, cancel entries, flatten this strategy's position and block entries until Session open. Requires Use session hours.", Order = 91)]
        public bool CloseBeforeSessionEnd { get; set; } = true;

        [Display(GroupName = "04. Time filter", Name = "Trade on Saturday", Description = "Allow new entries on Saturday in robot time. Existing positions remain protected.", Order = 81)]
        public bool TradeOnSaturday { get; set; } = false;

        [Display(GroupName = "04. Time filter", Name = "Trade on Sunday", Description = "Allow new entries on Sunday in robot time. Existing positions remain protected.", Order = 82)]
        public bool TradeOnSunday { get; set; } = false;

        [Display(GroupName = "04. Time filter", Name = "Robot time GMT offset", Description = "Time zone used for trading pause, weekdays and daily limits. GMT+3 is Moscow time.", Order = 83)]
        public decimal RobotTimeGmtOffsetHours { get; set; } = 3m;

        [Display(GroupName = "04. Time filter", Name = "ATAS chart GMT offset", Description = "GMT offset of candle times supplied by ATAS. Use GMT+0 for UTC candle times; set GMT+3 if the chart already uses Moscow time.", Order = 84)]
        public decimal ChartTimeGmtOffsetHours { get; set; } = 0m;

        [Display(GroupName = "05. Risk sizing", Name = "Calculate volume from risk", Description = "With TP/SL enabled, volume = reference balance x risk percentage / (stop distance x point value). Without a stop order, Base volume is used instead.", Order = 120)]
        public bool RiskSizingEnabled { get; set; } = false;

        [Display(GroupName = "05a. Daily profit limit", Name = "Enable profit limit", Description = "Stop new entries until the next day in robot time and close this strategy's position when the selected daily profit target is reached.", Order = 121)]
        public bool AccountProfitTargetEnabled { get; set; } = false;

        [Display(GroupName = "05a. Daily profit limit", Name = "Calculate profit in", Description = "Account currency: ATAS ClosedPnL + OpenPnL for the selected account. Points: sum of this strategy's closed and open price movements today, without volume or commissions.", Order = 122)]
        public ProfitTargetUnit DailyProfitUnit { get; set; } = ProfitTargetUnit.AccountCurrency;

        [Display(GroupName = "05a. Daily profit limit", Name = "Profit target: account currency", Description = "Used only when Calculate profit in is AccountCurrency. Enter a positive amount.", Order = 1221)]
        public decimal AccountProfitTarget { get; set; } = 2m;

        [Display(GroupName = "05a. Daily profit limit", Name = "Profit target: points", Description = "Used only when Calculate profit in is Points. Closed trades today plus the open position's price movement; without volume or commissions. Enter a positive number.", Order = 1222)]
        public decimal DailyProfitTargetPoints { get; set; } = 2m;

        [Display(GroupName = "05b. Daily loss limit", Name = "Enable loss limit", Description = "Stop new entries until the next day in robot time and close this strategy's position when the selected daily loss limit is reached.", Order = 123)]
        public bool AccountLossLimitEnabled { get; set; } = false;

        [Display(GroupName = "05b. Daily loss limit", Name = "Calculate loss in", Description = "Account currency: ATAS ClosedPnL + OpenPnL for the selected account. Points: sum of this strategy's closed and open price movements today, without volume or commissions.", Order = 124)]
        public ProfitTargetUnit DailyLossUnit { get; set; } = ProfitTargetUnit.AccountCurrency;

        [Display(GroupName = "05b. Daily loss limit", Name = "Loss limit: account currency", Description = "Used only when Calculate loss in is AccountCurrency. Enter a positive amount, for example 2 for a -2 USDT limit.", Order = 125)]
        public decimal AccountLossLimit { get; set; } = 2m;

        [Display(GroupName = "05b. Daily loss limit", Name = "Loss limit: points", Description = "Used only when Calculate loss in is Points. Closed trades today plus the open position's price movement; without volume or commissions. Enter a positive number.", Order = 126)]
        public decimal DailyLossLimitPoints { get; set; } = 2m;

        [Display(GroupName = "05c. Money limit source", Name = "Money PnL source", Description = "ATAS portfolio uses the selected account's daily ClosedPnL + OpenPnL. Strategy trades estimates this instance's daily PnL from fills, volume and Point value; use it to test money limits in Replay when portfolio PnL stays zero.", Order = 127)]
        public MoneyPnlSource DailyMoneyPnlSource { get; set; } = MoneyPnlSource.AtasPortfolio;

        [Display(GroupName = "06. Series sizing", Name = "Use series sizing", Order = 150)]
        public bool SeriesSizingEnabled { get; set; } = false;

        [Display(GroupName = "03. Recovery", Name = "Entry timeout bars", Order = 90)]
        public int EntryRecoveryBars { get; set; } = 3;

        [Display(GroupName = "03. Recovery", Name = "TP/SL retry bars", Order = 100)]
        public int ProtectiveRetryBars { get; set; } = 1;

        [Display(GroupName = "03. Recovery", Name = "Post-close cooldown bars", Order = 101)]
        public int PostCloseCooldownBars { get; set; } = 2;

        [Display(GroupName = "03. Recovery", Name = "Protective cancel warning bars", Order = 102)]
        public int ProtectiveCancelRetryBars { get; set; } = 5;

        [Display(GroupName = "04. Time filter", Name = "Pause start hour", Order = 110)]
        public int TradingPauseStartHour { get; set; } = 23;

        [Display(GroupName = "04. Time filter", Name = "Pause start minute", Order = 111)]
        public int TradingPauseStartMinute { get; set; } = 59;

        [Display(GroupName = "04. Time filter", Name = "Pause end hour", Order = 112)]
        public int TradingPauseEndHour { get; set; } = 4;

        [Display(GroupName = "04. Time filter", Name = "Pause end minute", Order = 113)]
        public int TradingPauseEndMinute { get; set; } = 0;

        [Display(GroupName = "07. Signal", Name = "Range size (CSV only)", Description = "Recorded in CSV for reference; does not change the ATAS chart. Set the chart RangeX separately in ATAS.", Order = 200)]
        public int RangeSize { get; set; } = 6;

        [Display(GroupName = "07. Signal", Name = "Use MACD filter", Description = "MACD filters new entries only: long when difference > 0, short when difference < 0. An opposite body engulfing always closes the current position, even when MACD disagrees.", Order = 201)]
        public bool UseMacdFilter { get; set; } = true;

        [Display(GroupName = "07. Signal", Name = "Minimum MACD difference from zero", Description = "0 disables this additional filter. Long requires MACD difference strictly above this value; Short requires it strictly below its negative value. Applies to new and reversal entries.", Order = 235)]
        public decimal MinimumMacdDifferenceAbs { get; set; } = 0m;

        [Display(GroupName = "07. Signal", Name = "One entry per MACD difference crossing", Description = "Allow at most one filled long after difference crosses from negative to positive, and one filled short after it crosses from positive to negative. A new crossing rearms that direction.", Order = 204)]
        public bool OneEntryPerMacdCrossing { get; set; } = false;

        [Display(GroupName = "07. Signal", Name = "Use imbalance filter", Description = "Require directional diagonal footprint imbalance on the closed signal bar. If footprint data is unavailable, skip the entry.", Order = 202)]
        public bool UseImbalanceFilter { get; set; } = false;

        [Display(GroupName = "07. Signal", Name = "Minimum imbalance %", Description = "Long: maximum Ask/Bid imbalance; Short: maximum Bid/Ask imbalance on the signal bar.", Order = 203)]
        public decimal MinimumImbalancePercent { get; set; } = 10000m;

        [Display(GroupName = "07. Signal", Name = "Use Delta % filter", Description = "Require directional Delta as a percent of bar volume strictly above the threshold. If bar volume is unavailable, skip the entry.", Order = 205)]
        public bool UseDeltaFilter { get; set; } = true;

        [Display(GroupName = "07. Signal", Name = "Minimum directional Delta %", Description = "100 * Delta / Volume for long; -100 * Delta / Volume for short. Entry requires a value strictly greater than this threshold.", Order = 206)]
        public decimal MinimumDirectionalDeltaPercent { get; set; } = 60m;

        [Display(GroupName = "07. Signal", Name = "Use absolute Delta filter", Description = "Require raw Delta on the closed signal bar to exceed the threshold for long, or fall below its negative for short. Independent of Delta % filter.", Order = 207)]
        public bool UseAbsoluteDeltaFilter { get; set; } = true;

        [Display(GroupName = "07. Signal", Name = "Minimum absolute directional Delta", Description = "Raw contracts/volume units, not percent. Zero requires positive Delta for long and negative Delta for short. Entry requires strictly greater magnitude.", Order = 208)]
        public decimal MinimumAbsoluteDirectionalDelta { get; set; } = 150m;

        [Display(GroupName = "07. Signal", Name = "Use intrabar Delta recovery filter", Description = "Track every trade in the closed signal bar. Long: (final Delta - intrabar minimum) / range; Short: (intrabar maximum - final Delta) / range. Missing trade history blocks entry. Independent of the other Delta filters.", Order = 209)]
        public bool UseDeltaRecoveryFilter { get; set; } = false;

        [Display(GroupName = "07. Signal", Name = "Minimum intrabar Delta recovery", Description = "Fraction from 0 to 1, not raw Delta or percent. 0.05 requires the final directional Delta to recover at least 5% of its intrabar range from the worst point.", Order = 210)]
        public decimal MinimumIntrabarDeltaRecovery { get; set; } = 0.05m;

        [Display(GroupName = "08. TP/SL", Name = "TP/SL mode", Order = 250)]
        public ProtectionMode ProtectionCalculationMode { get; set; } = ProtectionMode.Points;

        [Display(GroupName = "08. TP/SL", Name = "Use TP/SL orders", Description = "When disabled, opposite body engulfing closes the position and may open the opposite direction after the close fill. MACD filters entries only.", Order = 251)]
        public bool UseProtectiveOrders { get; set; } = true;

        [Display(GroupName = "06. Series sizing", Name = "Series mode", Order = 160)]
        public SeriesMode SeriesSizingMode { get; set; } = SeriesMode.Martingale;

        [Display(GroupName = "06. Series sizing", Name = "Max series step", Order = 170)]
        public int MaxSeriesStep { get; set; } = 15;

        [Display(GroupName = "07. Signal", Name = "MACD fast period", Order = 210)]
        public int FastPeriod { get; set; } = 64;

        [Display(GroupName = "07. Signal", Name = "MACD slow period", Order = 220)]
        public int SlowPeriod { get; set; } = 144;

        [Display(GroupName = "07. Signal", Name = "MACD signal period", Order = 230)]
        public int SignalPeriod { get; set; } = 36;

        [Display(GroupName = "01. Trading", Name = "Base volume", Order = 11)]
        public decimal Volume { get; set; } = 50m;

        [Display(GroupName = "01. Trading", Name = "Min volume", Order = 12)]
        public decimal MinVolume { get; set; } = 2m;

        [Display(GroupName = "01. Trading", Name = "Max volume (0 = off)", Order = 13)]
        public decimal MaxVolume { get; set; } = 0m;

        [Display(GroupName = "01. Trading", Name = "Volume step", Order = 14)]
        public decimal VolumeStep { get; set; } = 2m;

        [Display(GroupName = "08. TP/SL", Name = "Take profit points", Order = 260)]
        public decimal TakeProfitPoints { get; set; } = 0.22m;

        [Display(GroupName = "08. TP/SL", Name = "Stop loss points", Order = 270)]
        public decimal StopLossPoints { get; set; } = 0.2m;

        [Display(GroupName = "08. TP/SL", Name = "Move stop to breakeven", Order = 271)]
        public bool BreakEvenEnabled { get; set; } = false;

        [Display(GroupName = "08. TP/SL", Name = "Breakeven trigger points", Description = "Favorable price movement from the actual entry fill required before moving the stop.", Order = 272)]
        public decimal BreakEvenTriggerPoints { get; set; } = 0.15m;

        [Display(GroupName = "08. TP/SL", Name = "Breakeven offset points", Description = "Lock this many price points beyond the actual entry price; zero means exactly breakeven before fees.", Order = 273)]
        public decimal BreakEvenOffsetPoints { get; set; } = 0m;

        [Display(GroupName = "08. TP/SL", Name = "Trailing stop mode", Description = "Price: follow the most favorable observed price. Closed bar: move behind each completed bar's low/high. Only tightens the stop.", Order = 274)]
        public TrailingStopMode TrailingMode { get; set; } = TrailingStopMode.Off;

        [Display(GroupName = "08. TP/SL", Name = "Trailing distance points", Description = "Distance behind the best observed price in Price mode; buffer behind the previous bar low/high in Closed bar mode.", Order = 275)]
        public decimal TrailingDistancePoints { get; set; } = 0.15m;

        [Display(GroupName = "08. TP/SL", Name = "Take profit price %", Order = 280)]
        public decimal TakeProfitPricePercent { get; set; } = 0.5m;

        [Display(GroupName = "08. TP/SL", Name = "Stop loss price %", Order = 290)]
        public decimal StopLossPricePercent { get; set; } = 0.25m;

        [Display(GroupName = "08. TP/SL", Name = "Take profit deposit %", Order = 300)]
        public decimal TakeProfitDepositPercent { get; set; } = 1m;

        [Display(GroupName = "08. TP/SL", Name = "Stop loss deposit %", Order = 310)]
        public decimal StopLossDepositPercent { get; set; } = 0.5m;

        [Display(GroupName = "05. Risk sizing", Name = "Reference balance (account currency)", Description = "Manual reference amount for risk sizing and deposit-percent TP/SL. This is not read from the account automatically.", Order = 130)]
        public decimal DepositReferenceValue { get; set; } = 0m;

        [Display(GroupName = "05. Risk sizing", Name = "Risk per trade (% of reference)", Description = "Amount at risk if the stop is filled, before slippage and commission. Used only when Calculate volume from risk is enabled.", Order = 140)]
        public decimal RiskPerTradeDepositPercent { get; set; } = 1m;

        [Display(GroupName = "05. Risk sizing", Name = "Point value (account currency per unit)", Description = "Account-currency PnL of a one-price-unit move for one volume unit. Check this value for each instrument.", Order = 141)]
        public decimal PointValue { get; set; } = 1m;

        [Display(GroupName = "09. Commission", Name = "Commission per contract", Order = 350)]
        public decimal CommissionPerContract { get; set; } = 0.45m;

        [Display(GroupName = "09. Commission", Name = "Commission %", Order = 360)]
        public decimal CommissionPercent { get; set; } = 0m;

        [Display(GroupName = "02. Replay and diagnostics", Name = "CSV file name", Order = 41)]
        public string StatisticsFileName { get; set; } = "CashReaperStats.csv";

        private decimal[] _fastEma = Array.Empty<decimal>();
        private decimal[] _slowEma = Array.Empty<decimal>();
        private decimal[] _macd = Array.Empty<decimal>();
        private decimal[] _signal = Array.Empty<decimal>();
        private decimal[] _difference = Array.Empty<decimal>();
        private int _lastFinalizedMacdBar = -1;
        private int _finalizedFastPeriod = -1;
        private int _finalizedSlowPeriod = -1;
        private int _finalizedSignalPeriod = -1;

        private readonly string _instanceId = Guid.NewGuid().ToString("N").Substring(0, 8);

        private int _lastProcessedSignalBar = -1;
        private int _lastExportedClosedBar = -1;
        private int _lastMacdCrossCheckedBar = -1;
        private int _lastNonZeroDifferenceSign;
        private int _lastPositiveCrossBar = -1;
        private int _lastNegativeCrossBar = -1;
        private int _consumedPositiveCrossBar = -1;
        private int _consumedNegativeCrossBar = -1;
        private int _pendingEntryMacdCrossBar = -1;
        private int _entrySentBar = -1;
        private int _lastProtectiveRetryBar = -1;
        private DateTime _lastTimeLimitNoticeDate = DateTime.MinValue;
        private DateTime _accountStopBlockedDate = DateTime.MinValue;
        private DateTime _dailyPointDate = DateTime.MinValue;
        private decimal _dailyClosedPnlPoints;
        private decimal _dailyOpenBasePrice;
        private DateTime _dailyAccountDate = DateTime.MinValue;
        private decimal _dailyAccountEquityBaseline;
        private decimal _dailyClosedPnlMoneyEstimate;
        private decimal _dailyCommissionMoneyEstimate;
        private decimal _moneyEstimatePosition;
        private decimal _moneyEstimateBasePrice;
        private int _entriesBlockedUntilBar = -1;
        private int _cancelRetryUntilBar = -1;
        private int _lastCancelRetryBar = -1;
        private int _lastAccountCloseAttemptBar = -1;
        private int _lastReversalCloseAttemptBar = -1;
        private int _pendingReversalBar = -1;
        private OrderDirections _pendingReversalDirection;
        private bool _reversalPending;

        private Order _entryOrder;
        private Order _emergencyOrder;
        private Order _takeProfitOrder;
        private Order _stopLossOrder;
        private Order _stopReplacementOrder;
        private Order _pendingCancelTakeProfitOrder;
        private Order _pendingCancelStopLossOrder;

        private OrderDirections _entryDirection;
        private TradeState _tradeState = TradeState.Idle;

        private bool _entrySent;
        private bool _protectiveOrdersSent;
        private bool _protectivePlacementStarted;
        private bool _closingTrade;
        private bool _tradingStoppedByTime;
        private bool _scheduleClosing;
        private string _scheduleCloseReason = "";
        private DateTime _latestScheduleTime = DateTime.MinValue;
        private bool _accountLimitInvalidNotified;
        private bool _accountStopClosing;
        private AccountStopReason _accountStopReason;
        private bool _emergencyClosing;
        private string _activeSignalKey = "";

        private decimal _takeProfitPrice;
        private decimal _stopLossPrice;
        private decimal _activeVolume;
        private decimal _entryBasePrice;
        private decimal _entryExecutionPrice;
        private decimal _bestFavorablePrice;
        private bool _stopModificationPending;
        private decimal _requestedStopPrice;
        private int _stopModificationRequestedBar = -1;
        private decimal _lastTradePnlPoints;
        private bool _exitFillObserved;
        private decimal _totalPnlPoints;
        private decimal _lastKnownPosition;
        private decimal _trackedPosition;
        private int _lossSeriesStep;
        private readonly List<string> _marketEventBuffer = new List<string>(512);
        private long _marketEventSequence;
        private bool _marketEventCollectorFailed;
        private bool _footprintCollectorLimitNotified;
        private readonly object _deltaRecoveryLock = new object();
        private DeltaRecoveryPath _deltaRecoveryCurrent;
        private DeltaRecoveryPath _deltaRecoveryPrevious;

        private sealed class DeltaRecoveryPath
        {
            public int Bar;
            public decimal Volume;
            public decimal Delta;
            public decimal Minimum;
            public decimal Maximum;
            public bool HasUnknownDirection;
        }

        public CashReaperStrategy() : base(true)
        {
            Name = "CashReaper";
        }

        protected override void OnStarted()
        {
            lock (_deltaRecoveryLock)
            {
                _deltaRecoveryCurrent = null;
                _deltaRecoveryPrevious = null;
            }
            if (DepthCollectorEnabled)
                CaptureDepthSnapshot();

            _lastFinalizedMacdBar = -1;
            _finalizedFastPeriod = -1;
            _finalizedSlowPeriod = -1;
            _finalizedSignalPeriod = -1;
            // A restart must not reuse an old positive/negative regime for another entry.
            _lastMacdCrossCheckedBar = -1;
            _lastNonZeroDifferenceSign = 0;
            _lastPositiveCrossBar = -1;
            _lastNegativeCrossBar = -1;
            _consumedPositiveCrossBar = -1;
            _consumedNegativeCrossBar = -1;
            _pendingEntryMacdCrossBar = -1;
            _trackedPosition = CurrentPosition;
            _moneyEstimatePosition = CurrentPosition;
            _lastKnownPosition = GetActivePosition();
            _latestScheduleTime = DateTime.MinValue;

            if (SkipOldSignalOnStart)
                _lastProcessedSignalBar = Math.Max(_lastProcessedSignalBar, CurrentBar - 2);

            if (GetActivePosition() == 0)
            {
                ResetTradeState();
            }
            else
            {
                _entrySent = true;
                _entryDirection = GetActivePosition() > 0
                    ? OrderDirections.Buy
                    : OrderDirections.Sell;
                _tradeState = TradeState.PositionOpen;

                ShowStrategyNotification(
                    $"{GetInstanceLabel()}: запущен. Есть открытая позиция: {GetActivePosition()}. Новые входы заблокированы до закрытия позиции.");

                if (UseProtectiveOrders && RestoreProtectiveOrdersOnStart)
                    RestoreProtectionForExistingPosition();
            }

            var context = GetTradingContextText();

            ShowStrategyNotification(
                TradingEnabled
                    ? $"{GetInstanceLabel()}: запущен. Торговля ВКЛЮЧЕНА. Volume={Volume}; TP={TakeProfitPoints}; SL={StopLossPoints}; Recovery={AutoRecoveryEnabled}; Debug={DebugMode}; Replay={MarketReplayMode}; TimePause={TradingTimeLimitEnabled}; Saturday={TradeOnSaturday}; Sunday={TradeOnSunday}; ProfitTarget={AccountProfitTargetEnabled}/{DailyProfitUnit}/{(DailyProfitUnit == ProfitTargetUnit.Points ? DailyProfitTargetPoints : AccountProfitTarget)}; LossLimit={AccountLossLimitEnabled}/{DailyLossUnit}/{(DailyLossUnit == ProfitTargetUnit.Points ? DailyLossLimitPoints : AccountLossLimit)}; MoneySource={DailyMoneyPnlSource}; Pause={GetPausePeriodText()}; Collector={StatisticsCollectorEnabled}; {context}"
                    : $"{GetInstanceLabel()}: запущен. Торговля выключена. Volume={Volume}; TP={TakeProfitPoints}; SL={StopLossPoints}; Recovery={AutoRecoveryEnabled}; Debug={DebugMode}; Replay={MarketReplayMode}; TimePause={TradingTimeLimitEnabled}; Saturday={TradeOnSaturday}; Sunday={TradeOnSunday}; ProfitTarget={AccountProfitTargetEnabled}/{DailyProfitUnit}/{(DailyProfitUnit == ProfitTargetUnit.Points ? DailyProfitTargetPoints : AccountProfitTarget)}; LossLimit={AccountLossLimitEnabled}/{DailyLossUnit}/{(DailyLossUnit == ProfitTargetUnit.Points ? DailyLossLimitPoints : AccountLossLimit)}; MoneySource={DailyMoneyPnlSource}; Pause={GetPausePeriodText()}; Collector={StatisticsCollectorEnabled}; {context}");

            RecordTradeEvent(
                "strategy_started",
                Math.Max(0, CurrentBar - 1),
                "",
                TradingEnabled,
                context,
                0m);

            RecordTradeEvent("signal_mode", Math.Max(0, CurrentBar - 1), "", true,
                $"macd_filter={UseMacdFilter}; macd_fast={FastPeriod}; macd_slow={SlowPeriod}; macd_signal={SignalPeriod}; macd_source=Close; macd_min_abs_difference={MinimumMacdDifferenceAbs}; one_entry_per_macd_crossing={OneEntryPerMacdCrossing}; imbalance_filter={UseImbalanceFilter}; imbalance_threshold_pct={MinimumImbalancePercent}; delta_filter={UseDeltaFilter}; delta_threshold_pct={MinimumDirectionalDeltaPercent}; absolute_delta_filter={UseAbsoluteDeltaFilter}; absolute_delta_threshold={MinimumAbsoluteDirectionalDelta}; delta_recovery_filter={UseDeltaRecoveryFilter}; delta_recovery_minimum={MinimumIntrabarDeltaRecovery}; delta_rule=directional_strictly_greater; notifications={ShowNotifications}; session_hours={UseSessionHours}/{SessionOpenHour}:{SessionOpenMinute}-{SessionCloseHour}:{SessionCloseMinute}; flatten_session={CloseBeforeSessionEnd}; flatten_pause={CloseBeforeTradingPause}; chart_gmt_offset={ChartTimeGmtOffsetHours}; robot_gmt_offset={RobotTimeGmtOffsetHours}; " +
                $"breakeven={BreakEvenEnabled}/{BreakEvenTriggerPoints}/{BreakEvenOffsetPoints}; trailing={TrailingMode}/{TrailingDistancePoints}; " +
                $"reversal_mode={!UseProtectiveOrders}; tp_sl_orders={UseProtectiveOrders}; risk_sizing={RiskSizingEnabled}; series_sizing={SeriesSizingEnabled}", 0m);

            if (CurrentBar > 0)
                EnforceSchedule(GetRobotTimeFromChartTime(GetCandle(CurrentBar - 1).LastTime));
        }

        protected override void OnStopping()
        {
            FlushMarketEventBuffer();
            _reversalPending = false;
            CancelProtectiveOrders();

            if (GetActivePosition() == 0)
            {
                ResetTradeState();
                ShowStrategyNotification($"{GetInstanceLabel()}: остановлен. Позиции нет; проверьте подтверждение отмены защитных заявок в ATAS.");
            }
            else
            {
                ShowStrategyNotification(
                    $"{GetInstanceLabel()}: остановлен. Внимание: позиция всё ещё открыта: {GetActivePosition()}. Отмена защитных заявок запрошена, но не подтверждена.");
            }

            base.OnStopping();
        }

        protected override void OnNewTrade(MarketDataArg arg)
        {
            EnforceScheduleFromMarketEvent(arg);

            if (UseDeltaRecoveryFilter && arg != null)
                RecordDeltaRecoveryTrade(arg);

            if (TickCollectorEnabled && arg != null)
                RecordMarketEvent("trade", arg);

            base.OnNewTrade(arg);
        }

        protected override void MarketDepthChanged(MarketDataArg arg)
        {
            EnforceScheduleFromMarketEvent(arg);

            if (DepthCollectorEnabled && arg != null)
                RecordMarketEvent("depth", arg);

            base.MarketDepthChanged(arg);
        }

        private void CaptureDepthSnapshot()
        {
            try
            {
                RecordMarketEvent("snapshot_begin", null);
                foreach (var level in MarketDepthInfo.GetMarketDepthSnapshot())
                {
                    if (level != null)
                        RecordMarketEvent("snapshot", level);
                }
                RecordMarketEvent("snapshot_end", null);
            }
            catch (Exception ex)
            {
                RaiseDebug($"Level 2 snapshot error: {ex.Message}");
            }
        }

        private void RecordMarketEvent(string kind, MarketDataArg arg)
        {
            if (_marketEventCollectorFailed)
                return;

            lock (_marketEventBuffer)
            {
                try
                {
                    _marketEventBuffer.Add(string.Join(",",
                        Csv(GetInstanceLabel()),
                        Csv(Security == null ? "" : Security.ToString()),
                        Csv(++_marketEventSequence), Csv(kind),
                        Csv(arg == null ? "" : arg.Time.ToString("O", CultureInfo.InvariantCulture)),
                        Csv(arg == null ? 0m : arg.Price),
                        Csv(arg == null ? 0m : arg.Volume),
                        Csv(arg == null ? "" : arg.Direction.ToString()),
                        Csv(arg == null ? "" : arg.DataType.ToString()),
                        Csv(Math.Max(0, CurrentBar - 1))));

                    if (_marketEventBuffer.Count >= 512)
                        FlushMarketEventBufferLocked();
                }
                catch (Exception ex)
                {
                    _marketEventCollectorFailed = true;
                    _marketEventBuffer.Clear();
                    ShowStrategyNotification($"{GetInstanceLabel()}: запись тиков/стакана остановлена из-за ошибки: {ex.Message}. Остальная статистика собирается.");
                }
            }
        }

        private void FlushMarketEventBuffer()
        {
            lock (_marketEventBuffer)
                FlushMarketEventBufferLocked();
        }

        private void FlushMarketEventBufferLocked()
        {
            if (_marketEventBuffer.Count == 0)
                return;

            try
            {
                var path = Path.Combine(Path.GetDirectoryName(GetStatisticsPath()), "CashReaperMarketEvents-v1.csv");
                lock (StatsFileLock)
                {
                    var fileExists = File.Exists(path);
                    if (fileExists && new FileInfo(path).Length >= 256L * 1024L * 1024L)
                    {
                        _marketEventCollectorFailed = true;
                        _marketEventBuffer.Clear();
                        ShowStrategyNotification($"{GetInstanceLabel()}: журнал тиков/стакана достиг 256 МБ; его запись остановлена. Остальная статистика собирается.");
                        return;
                    }
                    using (var writer = new StreamWriter(path, append: true))
                    {
                        if (!fileExists)
                            writer.WriteLine("instance,instrument,sequence,kind,time,price,volume,direction,data_type,chart_bar");
                        foreach (var line in _marketEventBuffer)
                            writer.WriteLine(line);
                    }
                }

                _marketEventBuffer.Clear();
            }
            catch (Exception ex)
            {
                _marketEventCollectorFailed = true;
                _marketEventBuffer.Clear();
                ShowStrategyNotification($"{GetInstanceLabel()}: запись тиков/стакана остановлена из-за ошибки: {ex.Message}. Остальная статистика собирается.");
            }
        }

        protected override void OnCalculate(int bar, decimal value)
        {
            EnsureSize(bar + 1);

            var candle = GetCandle(bar);

            if (bar == 0)
            {
                _fastEma[bar] = candle.Close;
                _slowEma[bar] = candle.Close;
                _macd[bar] = 0;
                _signal[bar] = 0;
                _difference[bar] = 0;
                return;
            }

            _fastEma[bar] = CalcEma(candle.Close, _fastEma[bar - 1], FastPeriod);
            _slowEma[bar] = CalcEma(candle.Close, _slowEma[bar - 1], SlowPeriod);
            _macd[bar] = _fastEma[bar] - _slowEma[bar];
            _signal[bar] = CalcEma(_macd[bar], _signal[bar - 1], SignalPeriod);
            _difference[bar] = _macd[bar] - _signal[bar];

            ExportClosedBars(bar);

            if (bar < 2)
                return;

            var lastBar = CurrentBar - 1;

            if (bar != lastBar)
                return;

            EnforceSchedule(GetRobotTimeFromChartTime(candle.LastTime));

            UpdateDailyPointSession(GetRobotTime(lastBar).Date, candle.Close, GetActivePosition());
            // Account OpenPnL changes on every price update, not only once per bar.
            CheckAccountLimits(bar);

            var signalBar = bar - 1;
            FinalizeMacdThrough(signalBar);
            UpdateDynamicStop(candle.Close, signalBar, signalBar > _lastProcessedSignalBar);

            if (signalBar <= _lastProcessedSignalBar)
                return;

            _lastProcessedSignalBar = signalBar;

            SyncPositionState(signalBar, "before_signal_check");
            RecoverAfterConnectionGap(signalBar);
            RetryProtectiveCancellation(signalBar);
            RetryReversalClose(signalBar);
            CheckSignal(signalBar);
            SyncPositionState(signalBar, "after_signal_check");
        }

        protected override void OnOrderChanged(Order order)
        {
            if (order == null)
                return;

            RecordOrderEvent(order, "order_changed", "atas_order_changed");
            UpdateProtectiveOrderState(order);

            if (_stopModificationPending && !IsTerminalOrder(order) &&
                order.TriggerPrice == _requestedStopPrice &&
                (IsMatchingOrder(_stopLossOrder, order) || IsMatchingOrder(_stopReplacementOrder, order) ||
                 (order.Type == OrderTypes.Stop && order.Direction == GetExitDirectionForCurrentPosition() &&
                  order.QuantityToFill == Math.Abs(GetActivePosition()))))
            {
                _stopLossOrder = order;
                _stopLossPrice = _requestedStopPrice;
                _stopModificationPending = false;
                _stopReplacementOrder = null;
                RecordTradeEvent("stop_move_confirmed", _lastProcessedSignalBar,
                    order.Direction.ToString(), true, $"stop={_stopLossPrice}", order.QuantityToFill);
            }

            if (IsMatchingOrder(_emergencyOrder, order) && IsTerminalOrder(order) && GetActivePosition() != 0)
            {
                _emergencyOrder = null;
                _emergencyClosing = false;
                if (order.State == OrderStates.Failed)
                    ShowStrategyNotification($"{GetInstanceLabel()}: заявка закрытия позиции отклонена ATAS. Новые входы заблокированы; закрытие будет повторено.");
            }

            SyncPositionState(_lastProcessedSignalBar, "order_changed");

            if (IsMatchingOrder(_entryOrder, order))
            {
                if (GetActivePosition() != 0 && !_protectiveOrdersSent)
                    HandleEntryFilled();

                return;
            }

            if (IsMatchingOrder(_takeProfitOrder, order) && GetActivePosition() == 0)
            {
                HandleTradeClosed("TP");
                return;
            }

            if (IsMatchingOrder(_stopLossOrder, order) && GetActivePosition() == 0)
            {
                HandleTradeClosed("SL");
                return;
            }

            if (UseProtectiveOrders && GetActivePosition() != 0 && _entrySent &&
                !_protectiveOrdersSent && _entryExecutionPrice > 0)
            {
                RaiseDebug("Позиция есть, но защитные заявки ещё не активны. Проверяю защиту.");
                PlaceProtectiveOrders();
                return;
            }

            if (GetActivePosition() == 0 && _entrySent && _protectiveOrdersSent)
            {
                HandleTradeClosed("unknown");
            }
        }

        protected override void OnNewMyTrade(MyTrade myTrade)
        {
            if (myTrade == null)
                return;

            var previousPosition = GetActivePosition();
            var tradeBar = Math.Max(0, CurrentBar - 1);
            UpdateDailyPointSession(GetRobotTime(tradeBar).Date, GetCandle(tradeBar).Close, previousPosition);
            ApplyMyTradeToTrackedPosition(myTrade);
            var activePosition = GetActivePosition();

            var entryFill = IsSameOrder(_entryOrder, myTrade);
            if (entryFill && activePosition != 0 && _entryExecutionPrice <= 0)
            {
                _entryExecutionPrice = myTrade.Price;
                _bestFavorablePrice = _entryExecutionPrice;
                _dailyOpenBasePrice = myTrade.Price;
                _lastTradePnlPoints = 0;
                _exitFillObserved = false;
            }
            else if (entryFill && previousPosition != 0 && activePosition != 0 && !IsPositionReversed(previousPosition, activePosition) &&
                     Math.Abs(activePosition) > Math.Abs(previousPosition) && _entryExecutionPrice > 0)
            {
                _entryExecutionPrice = (_entryExecutionPrice * Math.Abs(previousPosition) + myTrade.Price * myTrade.Volume) /
                    Math.Abs(activePosition);
            }

            if (previousPosition != 0 && activePosition == 0)
            {
                _lastTradePnlPoints = CalculateClosedTradePnlPoints(previousPosition, _entryExecutionPrice, myTrade.Price);
                _exitFillObserved = true;
                _totalPnlPoints += _lastTradePnlPoints;
                _dailyClosedPnlPoints += CalculateClosedTradePnlPoints(previousPosition, _dailyOpenBasePrice, myTrade.Price);
                _dailyOpenBasePrice = 0m;
            }

            var positionReversed = IsPositionReversed(previousPosition, activePosition);

            if (positionReversed)
            {
                _lastTradePnlPoints = CalculateClosedTradePnlPoints(previousPosition, _entryExecutionPrice, myTrade.Price);
                _totalPnlPoints += _lastTradePnlPoints;
                _dailyClosedPnlPoints += CalculateClosedTradePnlPoints(previousPosition, _dailyOpenBasePrice, myTrade.Price);
                _dailyOpenBasePrice = myTrade.Price;
            }

            UpdateDailyMoneyEstimate(myTrade);

            RecordTradeEvent(
                "my_trade",
                _lastProcessedSignalBar,
                myTrade.OrderDirection.ToString(),
                true,
                $"{myTrade}; previous={previousPosition}; tracked={_trackedPosition}; current={CurrentPosition}; last_pnl_points={_lastTradePnlPoints}; total_pnl_points={_totalPnlPoints}",
                myTrade.Volume);

            // A terminal order callback may precede MyTrade and clear _emergencyClosing.
            // The reversal is nevertheless completed only after the closing fill is observed.
            if (_reversalPending && activePosition == 0)
            {
                CompleteReversalAfterFill();
                return;
            }

            if (_emergencyClosing)
            {
                if (activePosition == 0)
                {
                    RecordTradeEvent(
                        "emergency_flatten_done",
                        _lastProcessedSignalBar,
                        myTrade.OrderDirection.ToString(),
                        true,
                        myTrade.ToString(),
                        myTrade.Volume);

                    if (_accountStopClosing)
                        RecordTradeClosed(GetAccountStopOutcome());
                    else if (_scheduleClosing)
                        RecordTradeClosed(_scheduleCloseReason);

                    ResetTradeState();
                }

                return;
            }

            // An entry may fill after its cancellation was requested at the boundary.
            if (_scheduleClosing && activePosition != 0)
            {
                CancelProtectiveOrders();
                EmergencyFlattenPosition(_scheduleCloseReason);
                return;
            }

            if (!_entrySent && !_protectiveOrdersSent && activePosition != 0)
            {
                RecordTradeEvent(
                    "orphan_trade_detected",
                    _lastProcessedSignalBar,
                    myTrade.OrderDirection.ToString(),
                    false,
                    "trade_without_active_strategy_position",
                    myTrade.Volume);

                EmergencyFlattenPosition("orphan_trade");
                return;
            }

            if (positionReversed && _entrySent)
            {
                HandleProtectiveOverfill(myTrade, previousPosition, activePosition);
                return;
            }

            if (activePosition != 0 && _entrySent && !_protectiveOrdersSent && _entryExecutionPrice > 0)
            {
                HandleEntryFilled();
                return;
            }

            if (previousPosition != 0 && activePosition == 0 && _entrySent && !_reversalPending)
                HandleTradeClosed(GetTradeCloseOutcome(myTrade));
        }

        protected override void OnOrderRegisterFailed(Order order, string message)
        {
            if (order == null)
                return;

            RecordOrderEvent(order, "order_register_failed", message);

            if (_stopModificationPending && IsMatchingOrder(_stopReplacementOrder, order))
            {
                _stopModificationPending = false;
                _stopReplacementOrder = null;
                ShowStrategyNotification($"{GetInstanceLabel()}: перестановка стопа отклонена. Проверьте прежний стоп. {message}");
                return;
            }

            if (IsMatchingOrder(_emergencyOrder, order))
            {
                _emergencyOrder = null;
                _emergencyClosing = false;
                ShowStrategyNotification($"{GetInstanceLabel()}: заявка закрытия позиции отклонена. Новые входы заблокированы; закрытие будет повторено. {message}");
                return;
            }

            if (IsMatchingOrder(_entryOrder, order))
            {
                ResetTradeState();
                ShowStrategyNotification(
                    $"{GetInstanceLabel()}: входная заявка отклонена. {message}");
                return;
            }

            var takeProfitFailed = IsMatchingOrder(_takeProfitOrder, order);
            var stopLossFailed = IsMatchingOrder(_stopLossOrder, order);

            if (takeProfitFailed || stopLossFailed)
            {
                if (takeProfitFailed)
                    _takeProfitOrder = null;

                if (stopLossFailed)
                    _stopLossOrder = null;

                CancelProtectiveOrders();
                ShowStrategyNotification($"{GetInstanceLabel()}: защитная заявка отклонена. Закрываю позицию аварийно. {message}");
                EmergencyFlattenPosition("protective_order_rejected");
                return;
            }

            if (IsMatchingOrder(_pendingCancelTakeProfitOrder, order))
                _pendingCancelTakeProfitOrder = null;

            if (IsMatchingOrder(_pendingCancelStopLossOrder, order))
                _pendingCancelStopLossOrder = null;

            ShowStrategyNotification(
                $"{GetInstanceLabel()}: заявка отклонена. {message}");
        }

        protected override void OnOrderCancelFailed(Order order, string message)
        {
            if (order == null)
                return;

            RecordOrderEvent(order, "order_cancel_failed", message);
            RaiseDebug($"Не удалось отменить заявку. {message}");
        }

        private void UpdateMacdCrossingState(int bar)
        {
            if (!OneEntryPerMacdCrossing || bar < 1 || bar <= _lastMacdCrossCheckedBar)
                return;

            if (_lastMacdCrossCheckedBar < 0)
            {
                // Ignore old crosses when the strategy is attached mid-regime.
                for (var i = bar - 1; i >= 0; i--)
                {
                    if (_difference[i] == 0)
                        continue;
                    _lastNonZeroDifferenceSign = _difference[i] > 0 ? 1 : -1;
                    break;
                }
                _lastMacdCrossCheckedBar = bar - 1;
            }

            for (var i = _lastMacdCrossCheckedBar + 1; i <= bar; i++)
            {
                var sign = _difference[i] > 0 ? 1 : _difference[i] < 0 ? -1 : 0;
                if (sign == 0)
                    continue;

                if (_lastNonZeroDifferenceSign < 0 && sign > 0)
                    _lastPositiveCrossBar = i;
                else if (_lastNonZeroDifferenceSign > 0 && sign < 0)
                    _lastNegativeCrossBar = i;

                _lastNonZeroDifferenceSign = sign;
            }

            _lastMacdCrossCheckedBar = bar;
        }

        private bool CanEnterMacdCycle(OrderDirections direction, int signalBar)
        {
            if (!OneEntryPerMacdCrossing)
                return true;

            if (direction == OrderDirections.Buy)
                return _difference[signalBar] > 0 &&
                    _lastPositiveCrossBar >= 0 && _lastPositiveCrossBar <= signalBar &&
                    _lastPositiveCrossBar > _consumedPositiveCrossBar;

            return _difference[signalBar] < 0 &&
                _lastNegativeCrossBar >= 0 && _lastNegativeCrossBar <= signalBar &&
                _lastNegativeCrossBar > _consumedNegativeCrossBar;
        }

        private void CheckSignal(int bar)
        {
            UpdateMacdCrossingState(bar);

            if (_scheduleClosing)
            {
                RecordBarDecision(bar, null, null, 0, false, "", false, _scheduleCloseReason);
                return;
            }

            if (_reversalPending)
            {
                RecordBarDecision(bar, null, null, 0, false, "", false, "reversal_close_pending");
                return;
            }

            if (_pendingCancelTakeProfitOrder != null || _pendingCancelStopLossOrder != null)
            {
                RecordBarDecision(bar, null, null, 0, false, "", false, "protective_cancel_pending");
                RaiseDebug($"Вход заблокирован до подтверждения отмены защитных заявок. Bar={bar}");
                return;
            }

            if (!CheckAccountLimits(bar))
            {
                RecordBarDecision(bar, null, null, 0, false, "", false, GetAccountStopOutcome());
                return;
            }

            var previous = GetCandle(bar - 1);
            var current = GetCandle(bar);

            var diff = _difference[bar];
            var currentBullish = IsBullish(current);
            var currentBearish = IsBearish(current);
            var previousBullish = IsBullish(previous);
            var previousBearish = IsBearish(previous);
            var engulf = BodyEngulfs(current, previous);

            var bullishEngulfing =
                currentBullish &&
                previousBearish &&
                engulf;

            var bearishEngulfing =
                currentBearish &&
                previousBullish &&
                engulf;

            var isLong = bullishEngulfing && PassesMacdEntryFilter(OrderDirections.Buy, diff);
            var isShort = bearishEngulfing && PassesMacdEntryFilter(OrderDirections.Sell, diff);

            var activePosition = GetActivePosition();

            if (!UseProtectiveOrders && activePosition != 0)
            {
                if ((bullishEngulfing && activePosition < 0) ||
                    (bearishEngulfing && activePosition > 0))
                {
                    var direction = bullishEngulfing ? OrderDirections.Buy : OrderDirections.Sell;
                    RecordBarDecision(bar, current, previous, diff, engulf,
                        bullishEngulfing ? "LONG" : "SHORT", true, "opposite_engulfing_exit");
                    BeginReversal(direction, bar);
                }
                return;
            }

            if (bar <= _entriesBlockedUntilBar)
            {
                RecordBarDecision(bar, current, previous, diff, engulf, "", false, "post_close_cooldown");
                return;
            }

            if (_tradeState != TradeState.Idle || _entrySent || activePosition != 0)
                return;

            if (!IsWeekendTradingAllowed(CurrentBar - 1))
            {
                RecordBarDecision(bar, current, previous, diff, engulf, "", false, "weekend_disabled");
                return;
            }

            if (!IsTradingTimeAllowed(CurrentBar - 1))
            {
                RecordBarDecision(bar, current, previous, diff, engulf, "", false, "time_limit");
                return;
            }

            if (isLong)
            {
                if (!CanEnterMacdCycle(OrderDirections.Buy, bar))
                {
                    RecordBarDecision(bar, current, previous, diff, engulf, "LONG", false, "macd_positive_cycle_unavailable");
                    return;
                }
                if (!PassesDeltaFilter(bar, OrderDirections.Buy) ||
                    !PassesAbsoluteDeltaFilter(bar, OrderDirections.Buy))
                {
                    RecordBarDecision(bar, current, previous, diff, engulf, "LONG", false, "delta_filter");
                    return;
                }
                if (!PassesDeltaRecoveryFilter(bar, OrderDirections.Buy))
                {
                    RecordBarDecision(bar, current, previous, diff, engulf, "LONG", false, "delta_recovery_filter");
                    return;
                }
                if (!PassesImbalanceFilter(bar, OrderDirections.Buy))
                {
                    RecordBarDecision(bar, current, previous, diff, engulf, "LONG", false, "imbalance_filter");
                    return;
                }
                RecordBarDecision(bar, current, previous, diff, engulf, "LONG", true, "accepted");
                ProcessSignal(OrderDirections.Buy, "LONG", bar, diff, true);
                return;
            }

            if (isShort)
            {
                if (!CanEnterMacdCycle(OrderDirections.Sell, bar))
                {
                    RecordBarDecision(bar, current, previous, diff, engulf, "SHORT", false, "macd_negative_cycle_unavailable");
                    return;
                }
                if (!PassesDeltaFilter(bar, OrderDirections.Sell) ||
                    !PassesAbsoluteDeltaFilter(bar, OrderDirections.Sell))
                {
                    RecordBarDecision(bar, current, previous, diff, engulf, "SHORT", false, "delta_filter");
                    return;
                }
                if (!PassesDeltaRecoveryFilter(bar, OrderDirections.Sell))
                {
                    RecordBarDecision(bar, current, previous, diff, engulf, "SHORT", false, "delta_recovery_filter");
                    return;
                }
                if (!PassesImbalanceFilter(bar, OrderDirections.Sell))
                {
                    RecordBarDecision(bar, current, previous, diff, engulf, "SHORT", false, "imbalance_filter");
                    return;
                }
                RecordBarDecision(bar, current, previous, diff, engulf, "SHORT", true, "accepted");
                ProcessSignal(OrderDirections.Sell, "SHORT", bar, diff, true);
                return;
            }

            var rejectReason = GetRejectReason(currentBullish, currentBearish, previousBullish, previousBearish, engulf, diff);

            RecordBarDecision(bar, current, previous, diff, engulf, "", false, rejectReason);

            RaiseDebug(
                $"Сигнала нет. Bar={bar}; CurrentBull={currentBullish}; CurrentBear={currentBearish}; PreviousBull={previousBullish}; PreviousBear={previousBearish}; Engulf={engulf}; Diff={diff}");
        }

        private void ProcessSignal(OrderDirections direction, string signalName, int bar, decimal diff,
            bool filtersChecked = false)
        {
            if (!PassesMacdEntryFilter(direction, diff))
            {
                RecordTradeEvent("entry_blocked_macd", bar, direction.ToString(), false,
                    $"difference={diff}; minimum_abs_difference={MinimumMacdDifferenceAbs}", 0m);
                return;
            }

            if (!CanEnterMacdCycle(direction, bar))
            {
                RecordTradeEvent("entry_blocked", bar, direction.ToString(), false,
                    "macd_crossing_cycle_unavailable", 0m);
                return;
            }

            if (!filtersChecked)
            {
                if (!PassesDeltaFilter(bar, direction) || !PassesAbsoluteDeltaFilter(bar, direction) ||
                    !PassesDeltaRecoveryFilter(bar, direction) ||
                    !PassesImbalanceFilter(bar, direction))
                    return;
            }

            var signalCandle = GetCandle(bar);

            _entryDirection = direction;
            _entryBasePrice = signalCandle.Close;
            // The signal close is only a sizing estimate. Live TP/SL prices are set
            // after the market entry has actually filled.
            var stopDistance = CalculateStopLossDistance(_entryBasePrice, Volume);
            _activeVolume = CalculateOrderVolume(stopDistance);
            _takeProfitPrice = 0m;
            _stopLossPrice = 0m;

            var message =
                $"{GetInstanceLabel()}: {signalName} signal. " +
                $"Bar={bar}; " +
                $"Close={signalCandle.Close}; " +
                $"Difference={diff}; " +
                $"Volume={_activeVolume}; " +
                $"TP/SL={(UseProtectiveOrders ? "from actual fill" : "off")}; " +
                $"TPPoints={TakeProfitPoints}; " +
                $"SLPoints={StopLossPoints}; " +
                $"ProtectionMode={ProtectionCalculationMode}; " +
                $"SeriesStep={_lossSeriesStep}";

            ShowStrategyNotification(message);
            RecordSignal(bar, signalName, signalCandle.Close, diff, _activeVolume, "signal");

            if (!TradingEnabled)
            {
                RaiseDebug("TradingEnabled выключен. Сигнал найден, заявка не отправлена.");
                return;
            }

            if (!ValidateTradingContext())
                return;

            _activeSignalKey = GetSignalKey(bar);
            _pendingEntryMacdCrossBar = OneEntryPerMacdCrossing
                ? direction == OrderDirections.Buy ? _lastPositiveCrossBar : _lastNegativeCrossBar
                : -1;
            SendEntryOrder(direction);
        }

        private void BeginReversal(OrderDirections direction, int bar)
        {
            if (!TradingEnabled || _reversalPending || _emergencyClosing || _accountStopClosing)
                return;

            _pendingReversalDirection = direction;
            _pendingReversalBar = bar;
            _reversalPending = true;
            _lastReversalCloseAttemptBar = bar;
            RecordTradeEvent("reversal_close_requested", bar, direction.ToString(), true,
                $"from_position={GetActivePosition()}; to_direction={direction}", Math.Abs(GetActivePosition()));

            // Settings may have been changed while a protected position was open.
            // Never race an existing TP/SL order against the reversal close.
            if (_takeProfitOrder != null || _stopLossOrder != null)
            {
                CancelProtectiveOrders();
                if (_pendingCancelTakeProfitOrder != null || _pendingCancelStopLossOrder != null)
                    return;
            }

            EmergencyFlattenPosition("engulfing_reversal");
        }

        private void RetryReversalClose(int bar)
        {
            if (!_reversalPending || _emergencyClosing || GetActivePosition() == 0 ||
                _pendingCancelTakeProfitOrder != null || _pendingCancelStopLossOrder != null ||
                bar <= _lastReversalCloseAttemptBar)
                return;

            _lastReversalCloseAttemptBar = bar;
            EmergencyFlattenPosition("engulfing_reversal_retry");
        }

        private void CompleteReversalAfterFill()
        {
            var direction = _pendingReversalDirection;
            var signalBar = _pendingReversalBar;
            RecordTradeClosed("engulfing_reversal");
            RecordTradeEvent("reversal_close_filled", signalBar, direction.ToString(), true,
                $"last_pnl_points={_lastTradePnlPoints}; position={GetActivePosition()}", 0m);

            if (_lastTradePnlPoints > 0)
                _lossSeriesStep = 0;
            else if (_lastTradePnlPoints < 0)
                _lossSeriesStep = Math.Min(Math.Max(MaxSeriesStep, 1), _lossSeriesStep + 1);

            var accountLimitClosing = _accountStopClosing;
            if (accountLimitClosing)
                RecordTradeClosed(GetAccountStopOutcome());

            _reversalPending = false;
            _pendingReversalBar = -1;
            ResetTradeState();

            FinalizeMacdThrough(signalBar);
            UpdateMacdCrossingState(signalBar);

            if (!PassesMacdEntryFilter(direction, _difference[signalBar]))
            {
                RecordTradeEvent("reversal_entry_blocked", signalBar, direction.ToString(), false,
                    $"macd_direction_or_distance_filter; difference={_difference[signalBar]}; minimum_abs_difference={MinimumMacdDifferenceAbs}; position_closed=1", 0m);
                return;
            }

            if (!CanEnterMacdCycle(direction, signalBar))
            {
                RecordTradeEvent("reversal_entry_blocked", signalBar, direction.ToString(), false,
                    "macd_crossing_cycle_unavailable; position_closed=1", 0m);
                return;
            }

            if (accountLimitClosing || !TradingEnabled || UseProtectiveOrders ||
                _scheduleClosing ||
                _pendingCancelTakeProfitOrder != null ||
                _pendingCancelStopLossOrder != null || !CheckAccountLimits(signalBar) ||
                !IsWeekendTradingAllowed(CurrentBar - 1) || !IsTradingTimeAllowed(CurrentBar - 1))
            {
                RecordTradeEvent("reversal_entry_blocked", signalBar, direction.ToString(), false,
                    "trading_or_limit_or_schedule_or_protective_cancel", 0m);
                return;
            }

            RecordTradeEvent("reversal_entry_requested", signalBar, direction.ToString(), true,
                "close_fill_confirmed", 0m);
            ProcessSignal(direction, direction == OrderDirections.Buy ? "LONG" : "SHORT",
                signalBar, _difference[signalBar]);
        }

        private void SendEntryOrder(OrderDirections direction)
        {
            _entryOrder = new Order
            {
                Portfolio = Portfolio,
                Security = Security,
                Direction = direction,
                Type = OrderTypes.Market,
                QuantityToFill = _activeVolume
            };

            _entrySent = true;
            _entrySentBar = _lastProcessedSignalBar;
            _tradeState = TradeState.EntryOrderSent;

            try
            {
                OpenOrder(_entryOrder);
                RecordTradeEvent("entry_order_sent", _lastProcessedSignalBar, _entryDirection.ToString(), true, "open_order_ok", _activeVolume);

                ShowStrategyNotification(
                    $"{GetInstanceLabel()}: отправлена входная заявка {direction}; Volume={_activeVolume}");
            }
            catch (Exception ex)
            {
                RecordTradeEvent("entry_order_error", _lastProcessedSignalBar, _entryDirection.ToString(), false, ex.Message, _activeVolume);
                ResetTradeState();
                ShowStrategyNotification(
                    $"{GetInstanceLabel()}: входная заявка не отправлена. Состояние сброшено. Ошибка: {ex.Message}");
            }
        }

        private bool ValidateTradingContext()
        {
            var errors = "";

            if (Portfolio == null)
                errors += "portfolio_missing;";

            if (Security == null)
                errors += "security_missing;";

            if (_activeVolume <= 0)
                errors += "volume_zero;";

            if (string.IsNullOrEmpty(errors))
                return true;

            RecordTradeEvent(
                "entry_blocked",
                _lastProcessedSignalBar,
                _entryDirection.ToString(),
                false,
                errors,
                _activeVolume);

            ShowStrategyNotification(
                $"{GetInstanceLabel()}: вход заблокирован до отправки заявки. {errors} Проверь выбранный инструмент, портфель и Replay Account.");

            ResetTradeState();
            return false;
        }

        private void HandleEntryFilled()
        {
            if (_protectivePlacementStarted || _emergencyClosing || _tradeState == TradeState.PositionOpen)
                return;

            var activePosition = GetActivePosition();

            if (_entryExecutionPrice <= 0)
                return;

            _bestFavorablePrice = _entryExecutionPrice;
            CalculateProtectionPrices(_entryExecutionPrice, _entryDirection, Math.Abs(activePosition));

            _tradeState = TradeState.PositionOpen;
            _lastKnownPosition = activePosition;
            RecordTradeEvent("entry_filled", _lastProcessedSignalBar, _entryDirection.ToString(), true, "position_open", Math.Abs(activePosition));

            if (OneEntryPerMacdCrossing && _pendingEntryMacdCrossBar >= 0)
            {
                if (_entryDirection == OrderDirections.Buy)
                    _consumedPositiveCrossBar = Math.Max(_consumedPositiveCrossBar, _pendingEntryMacdCrossBar);
                else
                    _consumedNegativeCrossBar = Math.Max(_consumedNegativeCrossBar, _pendingEntryMacdCrossBar);

                RecordTradeEvent("macd_crossing_consumed", _lastProcessedSignalBar,
                    _entryDirection.ToString(), true,
                    $"cross_bar={_pendingEntryMacdCrossBar}; entry_filled=1", Math.Abs(activePosition));
                _pendingEntryMacdCrossBar = -1;
            }

            ShowStrategyNotification(
                UseProtectiveOrders
                    ? $"{GetInstanceLabel()}: вход исполнен. Position={activePosition}; TP={_takeProfitPrice}; SL={_stopLossPrice}"
                    : $"{GetInstanceLabel()}: вход исполнен. Position={activePosition}; TP/SL отключены; выход по противоположному поглощению.");

            if (UseProtectiveOrders)
                PlaceProtectiveOrders();
        }

        private void PlaceProtectiveOrders()
        {
            if (!UseProtectiveOrders)
                return;
            if (_entryExecutionPrice <= 0)
                return;
            // OnOrderChanged can be called synchronously from OpenOrder. Never create
            // another pair while the first pair is being registered.
            if (_protectivePlacementStarted || _emergencyClosing)
                return;

            var activePosition = GetActivePosition();

            if (activePosition == 0)
                return;

            _protectivePlacementStarted = true;

            var exitDirection = GetExitDirectionForCurrentPosition();
            var quantity = Math.Abs(activePosition);

            _takeProfitOrder = new Order
            {
                Portfolio = Portfolio,
                Security = Security,
                Direction = exitDirection,
                Type = OrderTypes.Limit,
                Price = _takeProfitPrice,
                QuantityToFill = quantity
            };

            _lastProtectiveRetryBar = _lastProcessedSignalBar;

            var takeProfitSent = TryOpenOrder(_takeProfitOrder, "take-profit");
            if (!takeProfitSent || _emergencyClosing || GetActivePosition() == 0)
            {
                FailProtectivePlacement();
                return;
            }

            _stopLossOrder = new Order
            {
                Portfolio = Portfolio,
                Security = Security,
                Direction = exitDirection,
                Type = OrderTypes.Stop,
                TriggerPrice = _stopLossPrice,
                Price = _stopLossPrice,
                QuantityToFill = quantity
            };

            var stopLossSent = TryOpenOrder(_stopLossOrder, "stop-loss");
            if (!stopLossSent || _emergencyClosing || GetActivePosition() == 0)
            {
                FailProtectivePlacement();
                return;
            }

            _protectiveOrdersSent = takeProfitSent && stopLossSent;
            _tradeState = _protectiveOrdersSent
                ? TradeState.ProtectiveOrdersActive
                : TradeState.PositionOpen;

            RecordTradeEvent(
                _protectiveOrdersSent ? "protective_orders_active" : "protective_orders_partial",
                _lastProcessedSignalBar,
                exitDirection.ToString(),
                _protectiveOrdersSent,
                _protectiveOrdersSent ? "tp_sl_sent" : "tp_sl_partial",
                quantity);

            ShowStrategyNotification(
                $"{GetInstanceLabel()}: защитные заявки выставлены. TP={_takeProfitPrice}; SL={_stopLossPrice}");
        }

        private void FailProtectivePlacement()
        {
            if (_emergencyClosing)
                return;

            CancelProtectiveOrders();
            ShowStrategyNotification($"{GetInstanceLabel()}: защитные заявки не подтверждены. Закрываю позицию аварийно.");
            EmergencyFlattenPosition("protective_order_not_sent");
        }

        private void RestoreProtectionForExistingPosition()
        {
            var bar = Math.Max(0, CurrentBar - 2);

            _entrySent = true;
            _entrySentBar = bar;
            _activeVolume = Math.Abs(GetActivePosition());
            _entryExecutionPrice = AveragePrice;
            if (_entryExecutionPrice <= 0)
            {
                _entrySentBar = -1;
                ShowStrategyNotification($"{GetInstanceLabel()}: нельзя восстановить TP/SL: ATAS не вернул фактическую среднюю цену входа. Позиция остаётся открытой без новых защитных заявок; проверьте её вручную. Новые входы заблокированы.");
                return;
            }
            _entryBasePrice = _entryExecutionPrice;
            _bestFavorablePrice = _entryExecutionPrice;
            _entryDirection = GetActivePosition() > 0
                ? OrderDirections.Buy
                : OrderDirections.Sell;

            CalculateProtectionPrices(_entryExecutionPrice, _entryDirection, _activeVolume);

            ShowStrategyNotification(
                $"{GetInstanceLabel()}: восстановление сопровождения открытой позиции. EntryPrice={_entryExecutionPrice}; TP={_takeProfitPrice}; SL={_stopLossPrice}");

            PlaceProtectiveOrders();
        }

        private void RecoverAfterConnectionGap(int signalBar)
        {
            if (!AutoRecoveryEnabled)
                return;

            if (UseProtectiveOrders && GetActivePosition() != 0 && _entrySent && !_protectiveOrdersSent)
            {
                if (_entryExecutionPrice <= 0)
                {
                    if (_entrySentBar < 0 && AveragePrice > 0)
                    {
                        _entryExecutionPrice = AveragePrice;
                        _entryBasePrice = _entryExecutionPrice;
                        _bestFavorablePrice = _entryExecutionPrice;
                        _activeVolume = Math.Abs(GetActivePosition());
                        CalculateProtectionPrices(_entryExecutionPrice, _entryDirection, _activeVolume);
                        PlaceProtectiveOrders();
                        return;
                    }
                    if (_entrySentBar >= 0 && signalBar - _entrySentBar >= Math.Max(EntryRecoveryBars, 1))
                        EmergencyFlattenPosition("entry_price_unavailable");
                    return;
                }
                if (_lastProtectiveRetryBar >= 0 &&
                    signalBar - _lastProtectiveRetryBar < ProtectiveRetryBars)
                    return;

                ShowStrategyNotification(
                    $"{GetInstanceLabel()}: восстановление после разрыва. Позиция есть, защитные заявки проверяются заново.");

                PlaceProtectiveOrders();
                return;
            }

            if (GetActivePosition() == 0 && _tradeState == TradeState.ProtectiveOrdersActive)
            {
                ShowStrategyNotification(
                    $"{GetInstanceLabel()}: позиция закрыта после восстановления связи. Состояние сброшено.");

                HandleTradeClosed("unknown");
                return;
            }

            if (_reversalPending || !_entrySent || _protectiveOrdersSent || GetActivePosition() != 0 || _entrySentBar < 0)
                return;

            if (signalBar - _entrySentBar < EntryRecoveryBars)
                return;

            RecordTradeEvent("entry_order_timeout", signalBar, _entryDirection.ToString(), false,
                $"position_not_opened; order_id={_entryOrder?.Id}; order_state={_entryOrder?.State}; portfolio={Portfolio}; money_source={DailyMoneyPnlSource}",
                _activeVolume);
            ResetTradeState();

            ShowStrategyNotification(
                $"{GetInstanceLabel()}: восстановление после разрыва или отклонённой заявки. Позиции нет, состояние сброшено, новые сигналы разрешены.");
        }

        private void HandleTradeClosed(string outcome)
        {
            if (_closingTrade)
                return;

            _closingTrade = true;
            CancelProtectiveOrders();
            _entriesBlockedUntilBar = Math.Max(_entriesBlockedUntilBar, _lastProcessedSignalBar + Math.Max(PostCloseCooldownBars, 1));

            UpdateSeriesAfterExit(outcome);

            RecordTradeClosed(outcome);
            _lastKnownPosition = GetActivePosition();

            ShowStrategyNotification(
                $"{GetInstanceLabel()}: позиция закрыта. Outcome={outcome}; NextSeriesStep={_lossSeriesStep}");

            ResetTradeState();
        }

        private void HandleProtectiveOverfill(MyTrade myTrade, decimal previousPosition, decimal activePosition)
        {
            if (_closingTrade)
                return;

            _closingTrade = true;
            CancelProtectiveOrders();
            _entriesBlockedUntilBar = Math.Max(_entriesBlockedUntilBar, _lastProcessedSignalBar + Math.Max(PostCloseCooldownBars, 1));

            var outcome = GetTradeCloseOutcome(myTrade);

            UpdateSeriesAfterExit(outcome);

            RecordTradeEvent(
                "protective_residual_detected",
                _lastProcessedSignalBar,
                myTrade.OrderDirection.ToString(),
                false,
                $"previous={previousPosition}; residual={activePosition}; outcome={outcome}; last_pnl_points={_lastTradePnlPoints}; total_pnl_points={_totalPnlPoints}",
                Math.Abs(activePosition));

            RecordTradeClosed($"{outcome}_residual");

            ShowStrategyNotification(
                $"{GetInstanceLabel()}: защитная заявка оставила лишнюю позицию. Previous={previousPosition}; Extra={activePosition}. Закрываю остаток аварийно.");

            _lastKnownPosition = activePosition;
            EmergencyFlattenPosition("protective_residual");
        }

        private void UpdateSeriesAfterExit(string outcome)
        {
            // A trailing or breakeven stop can close at a profit.
            if (_exitFillObserved)
            {
                if (_lastTradePnlPoints > 0)
                    _lossSeriesStep = 0;
                else if (_lastTradePnlPoints < 0)
                    _lossSeriesStep = Math.Min(Math.Max(MaxSeriesStep, 1), _lossSeriesStep + 1);
            }
            else if (outcome == "TP")
                _lossSeriesStep = 0;
            else if (outcome == "SL")
                _lossSeriesStep = Math.Min(Math.Max(MaxSeriesStep, 1), _lossSeriesStep + 1);
        }

        private void CalculateProtectionPrices(decimal basePrice, OrderDirections direction, decimal orderVolume)
        {
            var takeDistance = CalculateTakeProfitDistance(basePrice, orderVolume);
            var stopDistance = CalculateStopLossDistance(basePrice, orderVolume);

            if (direction == OrderDirections.Buy)
            {
                _stopLossPrice = basePrice - stopDistance;
                _takeProfitPrice = basePrice + takeDistance;
            }
            else
            {
                _stopLossPrice = basePrice + stopDistance;
                _takeProfitPrice = basePrice - takeDistance;
            }

            if (TickSize > 0)
            {
                _stopLossPrice = direction == OrderDirections.Buy
                    ? Math.Floor(_stopLossPrice / TickSize) * TickSize
                    : Math.Ceiling(_stopLossPrice / TickSize) * TickSize;
                _takeProfitPrice = direction == OrderDirections.Buy
                    ? Math.Ceiling(_takeProfitPrice / TickSize) * TickSize
                    : Math.Floor(_takeProfitPrice / TickSize) * TickSize;
            }
        }

        private void UpdateDynamicStop(decimal marketPrice, int closedBar, bool newClosedBar)
        {
            if (!UseProtectiveOrders || !_protectiveOrdersSent || _stopLossOrder == null ||
                GetActivePosition() == 0 ||
                _entryExecutionPrice <= 0 || marketPrice <= 0 || TickSize <= 0 ||
                _emergencyClosing || _accountStopClosing || _reversalPending)
                return;

            if (_stopModificationPending)
            {
                if (_stopModificationRequestedBar >= 0 && closedBar - _stopModificationRequestedBar >= 3)
                {
                    _stopModificationRequestedBar = -1;
                    if (IsTerminalOrder(_stopLossOrder))
                    {
                        ShowStrategyNotification($"{GetInstanceLabel()}: после перестановки нет подтверждённого активного стопа. Закрываю позицию.");
                        CancelProtectiveOrders();
                        EmergencyFlattenPosition("stop_modification_unconfirmed");
                    }
                    else
                        ShowStrategyNotification($"{GetInstanceLabel()}: перестановка стопа не подтверждена ATAS. Действует прежний стоп; проверьте заявку.");
                }
                return;
            }

            if (IsTerminalOrder(_stopLossOrder))
                return;

            var isLong = GetActivePosition() > 0;
            _bestFavorablePrice = _bestFavorablePrice == 0 ? marketPrice :
                isLong ? Math.Max(_bestFavorablePrice, marketPrice) : Math.Min(_bestFavorablePrice, marketPrice);

            var candidate = _stopLossPrice;
            if (BreakEvenEnabled && BreakEvenTriggerPoints > 0 && BreakEvenOffsetPoints >= 0 &&
                (isLong ? _bestFavorablePrice - _entryExecutionPrice : _entryExecutionPrice - _bestFavorablePrice) >= BreakEvenTriggerPoints)
            {
                var breakeven = _entryExecutionPrice + (isLong ? BreakEvenOffsetPoints : -BreakEvenOffsetPoints);
                candidate = isLong ? Math.Max(candidate, breakeven) : Math.Min(candidate, breakeven);
            }

            if (TrailingDistancePoints > 0)
            {
                if (TrailingMode == TrailingStopMode.Price)
                {
                    var trailing = _bestFavorablePrice + (isLong ? -TrailingDistancePoints : TrailingDistancePoints);
                    candidate = isLong ? Math.Max(candidate, trailing) : Math.Min(candidate, trailing);
                }
                else if (TrailingMode == TrailingStopMode.ClosedBar && newClosedBar && closedBar > _entrySentBar)
                {
                    var completed = GetCandle(closedBar);
                    var trailing = (isLong ? completed.Low - TrailingDistancePoints : completed.High + TrailingDistancePoints);
                    candidate = isLong ? Math.Max(candidate, trailing) : Math.Min(candidate, trailing);
                }
            }

            candidate = isLong
                ? Math.Floor(candidate / TickSize) * TickSize
                : Math.Ceiling(candidate / TickSize) * TickSize;

            // A stop must only tighten, and it must still be beyond the current market price.
            if (isLong ? candidate <= _stopLossPrice || candidate >= marketPrice - TickSize
                       : candidate >= _stopLossPrice || candidate <= marketPrice + TickSize)
                return;

            var originalStop = _stopLossOrder;
            var previousStopPrice = _stopLossPrice;
            var stopDirection = originalStop.Direction;
            var stopQuantity = originalStop.QuantityToFill;
            var modified = new Order
            {
                Portfolio = Portfolio,
                Security = Security,
                Direction = stopDirection,
                Type = OrderTypes.Stop,
                TriggerPrice = candidate,
                Price = candidate,
                QuantityToFill = stopQuantity
            };

            _requestedStopPrice = candidate;
            _stopModificationPending = true;
            _stopModificationRequestedBar = closedBar;
            _stopReplacementOrder = modified;
            try
            {
                ModifyOrder(originalStop, modified);
                RecordTradeEvent("stop_move_requested", closedBar, stopDirection.ToString(),
                    true, $"from={previousStopPrice}; to={candidate}; mode={TrailingMode}; breakeven={BreakEvenEnabled}",
                    stopQuantity);
            }
            catch (Exception ex)
            {
                _stopModificationPending = false;
                _stopReplacementOrder = null;
                RecordTradeEvent("stop_move_error", closedBar, stopDirection.ToString(),
                    false, ex.Message, stopQuantity);
                ShowStrategyNotification($"{GetInstanceLabel()}: перестановка стопа отклонена. Старый стоп остаётся: {ex.Message}");
            }
        }

        private decimal CalculateTakeProfitDistance(decimal basePrice, decimal orderVolume)
        {
            if (ProtectionCalculationMode == ProtectionMode.PricePercent)
                return Math.Abs(basePrice) * TakeProfitPricePercent / 100m;

            if (ProtectionCalculationMode == ProtectionMode.DepositPercent && DepositReferenceValue > 0 && PointValue > 0 && orderVolume > 0)
                return DepositReferenceValue * TakeProfitDepositPercent / 100m / (orderVolume * PointValue);

            return TakeProfitPoints;
        }

        private decimal CalculateStopLossDistance(decimal basePrice, decimal orderVolume)
        {
            if (ProtectionCalculationMode == ProtectionMode.PricePercent)
                return Math.Abs(basePrice) * StopLossPricePercent / 100m;

            if (ProtectionCalculationMode == ProtectionMode.DepositPercent && DepositReferenceValue > 0 && PointValue > 0 && orderVolume > 0)
                return DepositReferenceValue * StopLossDepositPercent / 100m / (orderVolume * PointValue);

            return StopLossPoints;
        }

        private decimal CalculateOrderVolume(decimal stopDistance)
        {
            var baseVolume = Volume;

            if (UseProtectiveOrders && RiskSizingEnabled && DepositReferenceValue > 0 && RiskPerTradeDepositPercent > 0 && PointValue > 0 && stopDistance > 0)
            {
                var riskMoney = DepositReferenceValue * RiskPerTradeDepositPercent / 100m;
                baseVolume = riskMoney / (stopDistance * PointValue);
            }

            if (SeriesSizingEnabled)
                baseVolume *= GetSeriesMultiplier();

            return NormalizeVolume(baseVolume);
        }

        private decimal GetSeriesMultiplier()
        {
            if (_lossSeriesStep <= 0)
                return 1m;

            var step = Math.Min(_lossSeriesStep, Math.Max(MaxSeriesStep, 1));

            if (SeriesSizingMode == SeriesMode.Martingale)
                return (decimal)Math.Pow(2, step);

            return 1m + step;
        }

        private decimal NormalizeVolume(decimal value)
        {
            var volume = value;

            if (VolumeStep > 0)
                volume = Math.Floor(volume / VolumeStep) * VolumeStep;

            if (volume < MinVolume)
                volume = MinVolume;

            if (MaxVolume > 0 && volume > MaxVolume)
                volume = MaxVolume;

            return volume;
        }

        private OrderDirections GetExitDirectionForCurrentPosition()
        {
            return GetActivePosition() > 0
                ? OrderDirections.Sell
                : OrderDirections.Buy;
        }

        private decimal GetActivePosition()
        {
            return CurrentPosition != 0
                ? CurrentPosition
                : _trackedPosition;
        }

        private static bool IsPositionReversed(decimal previousPosition, decimal activePosition)
        {
            return previousPosition != 0 &&
                activePosition != 0 &&
                Math.Sign(previousPosition) != Math.Sign(activePosition);
        }

        private void ApplyMyTradeToTrackedPosition(MyTrade myTrade)
        {
            var signedVolume = myTrade.OrderDirection == OrderDirections.Buy
                ? myTrade.Volume
                : -myTrade.Volume;

            _trackedPosition += signedVolume;

            if (Math.Abs(_trackedPosition) < 0.00000001m)
                _trackedPosition = 0;
        }

        private string GetTradeCloseOutcome(MyTrade myTrade)
        {
            if (IsSameOrder(_takeProfitOrder, myTrade))
                return "TP";

            if (IsSameOrder(_stopLossOrder, myTrade))
                return "SL";

            return "unknown";
        }

        private bool IsSameOrder(Order order, MyTrade myTrade)
        {
            if (order == null || myTrade == null)
                return false;

            return !string.IsNullOrEmpty(order.Id) && order.Id == myTrade.OrderId;
        }

        private static bool IsMatchingOrder(Order tracked, Order update)
        {
            return tracked != null && update != null &&
                (ReferenceEquals(tracked, update) ||
                 (!string.IsNullOrEmpty(tracked.Id) && tracked.Id == update.Id));
        }

        private static bool IsTerminalOrder(Order order)
        {
            return order.State == OrderStates.Done || order.State == OrderStates.Failed;
        }

        private void UpdateProtectiveOrderState(Order order)
        {
            if (IsMatchingOrder(_takeProfitOrder, order))
                _takeProfitOrder = order;

            if (IsMatchingOrder(_stopLossOrder, order))
                _stopLossOrder = order;

            if (IsMatchingOrder(_pendingCancelTakeProfitOrder, order))
                _pendingCancelTakeProfitOrder = IsTerminalOrder(order) ? null : order;

            if (IsMatchingOrder(_pendingCancelStopLossOrder, order))
                _pendingCancelStopLossOrder = IsTerminalOrder(order) ? null : order;

            if (_pendingCancelTakeProfitOrder == null && _pendingCancelStopLossOrder == null)
                _cancelRetryUntilBar = -1;
        }

        private decimal CalculateClosedTradePnlPoints(decimal position, decimal entryPrice, decimal exitPrice)
        {
            if (entryPrice == 0 || exitPrice == 0 || position == 0)
                return 0m;

            return position > 0
                ? exitPrice - entryPrice
                : entryPrice - exitPrice;
        }

        private decimal CalculateUnrealizedPnlPoints(decimal marketPrice)
        {
            var activePosition = GetActivePosition();

            if (activePosition == 0 || _entryExecutionPrice == 0 || marketPrice == 0)
                return 0m;

            return activePosition > 0
                ? marketPrice - _entryExecutionPrice
                : _entryExecutionPrice - marketPrice;
        }

        private void EmergencyFlattenPosition(string reason)
        {
            if (_emergencyClosing)
                return;

            var activePosition = GetActivePosition();

            if (activePosition == 0)
                return;

            var closeDirection = activePosition > 0
                ? OrderDirections.Sell
                : OrderDirections.Buy;

            var order = new Order
            {
                Portfolio = Portfolio,
                Security = Security,
                Direction = closeDirection,
                Type = OrderTypes.Market,
                QuantityToFill = Math.Abs(activePosition)
            };

            _emergencyOrder = order;
            _emergencyClosing = true;
            _entriesBlockedUntilBar = Math.Max(_entriesBlockedUntilBar, _lastProcessedSignalBar + Math.Max(PostCloseCooldownBars, 1));

            try
            {
                OpenOrder(order);

                if (!_emergencyClosing)
                    return;

                RecordTradeEvent(
                    "emergency_flatten_sent",
                    _lastProcessedSignalBar,
                    closeDirection.ToString(),
                    true,
                    reason,
                    Math.Abs(activePosition));

                ShowStrategyNotification(reason.StartsWith("engulfing_reversal")
                    ? $"{GetInstanceLabel()}: противоположное поглощение. Закрываю текущую позицию {closeDirection}; Volume={Math.Abs(activePosition)}."
                    : reason == "account_profit_target" || reason == "account_loss_limit"
                    ? $"{GetInstanceLabel()}: дневной денежный лимит счёта достигнут. Отправлено досрочное закрытие {closeDirection}; Volume={Math.Abs(activePosition)}."
                    : reason == "session_close" || reason == "trading_pause"
                    ? $"{GetInstanceLabel()}: граница торгового времени. Отправлено закрытие {closeDirection}; Volume={Math.Abs(activePosition)}. Ожидаю подтверждения исполнения."
                    : $"{GetInstanceLabel()}: обнаружено исполнение без активной сделки стратегии. Отправляю аварийное закрытие {closeDirection}; Volume={Math.Abs(activePosition)}.");
            }
            catch (Exception ex)
            {
                _emergencyClosing = false;
                _emergencyOrder = null;

                RecordTradeEvent(
                    "emergency_flatten_error",
                    _lastProcessedSignalBar,
                    closeDirection.ToString(),
                    false,
                    ex.Message,
                    Math.Abs(activePosition));

                ShowStrategyNotification(
                    $"{GetInstanceLabel()}: не удалось аварийно закрыть неожиданную позицию. Ошибка: {ex.Message}");
            }
        }

        private bool TryOpenOrder(Order order, string orderName)
        {
            try
            {
                OpenOrder(order);
                RecordOrderEvent(order, $"{orderName}_sent", "open_order_ok");
                return true;
            }
            catch (Exception ex)
            {
                RecordOrderEvent(order, $"{orderName}_error", ex.Message);
                ShowStrategyNotification(
                    $"{GetInstanceLabel()}: не удалось отправить {orderName}. Ошибка: {ex.Message}");

                return false;
            }
        }

        private void CancelProtectiveOrders()
        {
            if (_takeProfitOrder != null && !IsTerminalOrder(_takeProfitOrder))
                _pendingCancelTakeProfitOrder = _takeProfitOrder;

            if (_stopLossOrder != null && !IsTerminalOrder(_stopLossOrder))
                _pendingCancelStopLossOrder = _stopLossOrder;

            TryCancelOrder(_pendingCancelTakeProfitOrder);
            TryCancelOrder(_pendingCancelStopLossOrder);

            if (_pendingCancelTakeProfitOrder != null || _pendingCancelStopLossOrder != null)
            {
                _lastCancelRetryBar = -1;
                _cancelRetryUntilBar = _lastProcessedSignalBar + Math.Max(ProtectiveCancelRetryBars, 1);
            }

            _takeProfitOrder = null;
            _stopLossOrder = null;
            _stopReplacementOrder = null;
            _protectiveOrdersSent = false;
        }

        private void RetryProtectiveCancellation(int signalBar)
        {
            if (_pendingCancelTakeProfitOrder == null && _pendingCancelStopLossOrder == null)
                return;

            if (signalBar <= _lastCancelRetryBar)
                return;

            _lastCancelRetryBar = signalBar;

            if (_cancelRetryUntilBar >= 0 && signalBar >= _cancelRetryUntilBar)
            {
                ShowStrategyNotification($"{GetInstanceLabel()}: отмена защитной заявки не подтверждена ATAS. Новые входы заблокированы; проверьте заявки вручную.");
                _cancelRetryUntilBar = -1;
            }

            TryCancelOrder(_pendingCancelTakeProfitOrder, "protective_cancel_retry");
            TryCancelOrder(_pendingCancelStopLossOrder, "protective_cancel_retry");
        }

        private void TryCancelOrder(Order order, string eventType = "protective_cancel_requested")
        {
            if (order == null || IsTerminalOrder(order))
                return;

            try
            {
                CancelOrder(order);
                RecordOrderEvent(order, eventType, "cancel_request_sent");
            }
            catch (Exception ex)
            {
                RecordOrderEvent(order, "protective_cancel_error", ex.Message);
                RaiseDebug($"Не удалось отправить отмену защитной заявки. {ex.Message}");
            }
        }

        private void SyncPositionState(int bar, string reason)
        {
            if (CurrentPosition != 0 || !MarketReplayMode)
                _trackedPosition = CurrentPosition;

            if (GetActivePosition() == _lastKnownPosition)
                return;

            var previousPosition = _lastKnownPosition;
            var activePosition = GetActivePosition();

            _lastKnownPosition = activePosition;

            RecordTradeEvent(
                "position_changed",
                bar,
                activePosition > 0 ? OrderDirections.Buy.ToString() : activePosition < 0 ? OrderDirections.Sell.ToString() : _entryDirection.ToString(),
                true,
                $"{reason}; previous={previousPosition}; current={activePosition}",
                Math.Abs(activePosition));

            if (IsPositionReversed(previousPosition, activePosition) && _entrySent)
            {
                CancelProtectiveOrders();
                _entriesBlockedUntilBar = Math.Max(_entriesBlockedUntilBar, bar + Math.Max(PostCloseCooldownBars, 1));

                RecordTradeEvent(
                    "unexpected_residual_position_detected",
                    bar,
                    activePosition > 0 ? OrderDirections.Buy.ToString() : OrderDirections.Sell.ToString(),
                    false,
                    $"{reason}; previous={previousPosition}; residual={activePosition}",
                    Math.Abs(activePosition));

                EmergencyFlattenPosition("unexpected_residual");
                return;
            }

            if (previousPosition == 0 && activePosition != 0 && _entrySent && !_protectiveOrdersSent)
            {
                HandleEntryFilled();
                return;
            }

            if (previousPosition != 0 && activePosition == 0 && _entrySent && !_reversalPending)
                HandleTradeClosed(reason);
        }

        private void ResetTradeState()
        {
            _entryOrder = null;
            _emergencyOrder = null;
            _takeProfitOrder = null;
            _stopLossOrder = null;
            _stopReplacementOrder = null;

            _entryDirection = default;
            _tradeState = TradeState.Idle;
            _entrySent = false;
            _protectiveOrdersSent = false;
            _protectivePlacementStarted = false;
            _closingTrade = false;
            _accountStopClosing = false;
            _tradingStoppedByTime = false;
            _emergencyClosing = false;
            _entrySentBar = -1;
            _lastProtectiveRetryBar = -1;
            _lastAccountCloseAttemptBar = -1;
            _trackedPosition = CurrentPosition;
            _lastKnownPosition = GetActivePosition();

            _takeProfitPrice = 0;
            _stopLossPrice = 0;
            _entryExecutionPrice = 0;
            _exitFillObserved = false;
            _bestFavorablePrice = 0;
            _stopModificationPending = false;
            _requestedStopPrice = 0;
            _stopModificationRequestedBar = -1;
            _activeSignalKey = "";
            _pendingEntryMacdCrossBar = -1;
        }

        private void ShowStrategyNotification(string message)
        {
            if (ShowNotifications)
                RaiseShowNotification(message);
        }

        private void RaiseDebug(string message)
        {
            if (!DebugMode)
                return;

            ShowStrategyNotification($"{GetInstanceLabel()}: DEBUG: {message}");
        }

        private string GetRejectReason(
            bool currentBullish,
            bool currentBearish,
            bool previousBullish,
            bool previousBearish,
            bool engulf,
            decimal diff)
        {
            if (!engulf)
                return "no_body_engulf";

            if (UseMacdFilter && diff == 0)
                return "macd_zero";

            if (UseMacdFilter && currentBullish && previousBearish && diff <= 0)
                return "long_macd_filter";

            if (UseMacdFilter && currentBearish && previousBullish && diff >= 0)
                return "short_macd_filter";

            if (MinimumMacdDifferenceAbs > 0m && currentBullish && previousBearish &&
                diff <= MinimumMacdDifferenceAbs)
                return "long_macd_distance_filter";

            if (MinimumMacdDifferenceAbs > 0m && currentBearish && previousBullish &&
                diff >= -MinimumMacdDifferenceAbs)
                return "short_macd_distance_filter";

            if (!currentBullish && !currentBearish)
                return "doji_current";

            if (!previousBullish && !previousBearish)
                return "doji_previous";

            return "bar_direction_filter";
        }

        private bool PassesMacdEntryFilter(OrderDirections direction, decimal difference)
        {
            if ((UseMacdFilter || OneEntryPerMacdCrossing) &&
                (direction == OrderDirections.Buy ? difference <= 0m : difference >= 0m))
                return false;

            if (MinimumMacdDifferenceAbs <= 0m)
                return true;

            return direction == OrderDirections.Buy
                ? difference > MinimumMacdDifferenceAbs
                : difference < -MinimumMacdDifferenceAbs;
        }

        // Export every completed chart bar, including bars skipped by trading-state checks.
        // The next bar proves that the preceding bar is closed; historical callbacks also
        // backfill any loaded bars whose OnCalculate callback was not delivered individually.
        private void ExportClosedBars(int bar)
        {
            if (!StatisticsCollectorEnabled)
                return;

            var lastClosedBar = Math.Min(bar - 1, CurrentBar - 2);
            if (lastClosedBar <= _lastExportedClosedBar)
                return;

            try
            {
                var path = Path.Combine(Path.GetDirectoryName(GetStatisticsPath()),
                    "CashReaperBars-full-v1.csv");
                lock (StatsFileLock)
                {
                    var fileExists = File.Exists(path);
                    using (var writer = new StreamWriter(path, append: true))
                    {
                        if (!fileExists)
                            writer.WriteLine("instance,instrument,bar,bar_last_time,open,high,low,close,volume,ticks,bid,ask,delta,range_size,tick_size");

                        for (var i = _lastExportedClosedBar + 1; i <= lastClosedBar; i++)
                        {
                            var candle = GetCandle(i);
                            writer.WriteLine(string.Join(",",
                                Csv(GetInstanceLabel()),
                                Csv(Security == null ? "" : Security.ToString()),
                                Csv(i),
                                Csv(candle.LastTime.ToString("O", CultureInfo.InvariantCulture)),
                                Csv(candle.Open), Csv(candle.High), Csv(candle.Low), Csv(candle.Close),
                                Csv(candle.Volume), Csv(candle.Ticks), Csv(candle.Bid),
                                Csv(candle.Ask), Csv(candle.Delta),
                                Csv(RangeSize), Csv(TickSize)));
                            _lastExportedClosedBar = i;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                RaiseDebug($"Full bar collector error: {ex.Message}");
            }
        }

        private void RecordBarDecision(
            int bar,
            dynamic current,
            dynamic previous,
            decimal diff,
            bool engulf,
            string signalName,
            bool accepted,
            string reason)
        {
            if (!StatisticsCollectorEnabled)
                return;

            var close = current == null ? 0m : current.Close;
            var open = current == null ? 0m : current.Open;
            var high = current == null ? 0m : current.High;
            var low = current == null ? 0m : current.Low;
            var body = Math.Abs(close - open);
            var range = Math.Abs(high - low);

            WriteStatsLine(
                "bar",
                bar,
                signalName,
                accepted,
                reason,
                "",
                open,
                high,
                low,
                close,
                body,
                range,
                diff,
                engulf,
                0m);

            if (engulf && FootprintCollectorEnabled)
                WriteFootprintSnapshot(bar);
        }

        private void RecordSignal(int bar, string signalName, decimal close, decimal diff, decimal volume, string reason)
        {
            if (!StatisticsCollectorEnabled)
                return;

            WriteStatsLine(
                "signal",
                bar,
                signalName,
                true,
                reason,
                _entryDirection.ToString(),
                0m,
                0m,
                0m,
                close,
                0m,
                0m,
                diff,
                true,
                volume);
        }

        private void RecordTradeClosed(string outcome)
        {
            if (!StatisticsCollectorEnabled)
                return;

            WriteStatsLine(
                "trade_closed",
                _lastProcessedSignalBar,
                "",
                true,
                outcome,
                _entryDirection.ToString(),
                0m,
                0m,
                0m,
                0m,
                0m,
                0m,
                0m,
                false,
                _activeVolume);
        }

        private void RecordTradeEvent(
            string eventType,
            int bar,
            string direction,
            bool accepted,
            string reason,
            decimal volume)
        {
            if (!StatisticsCollectorEnabled)
                return;

            WriteStatsLine(
                eventType,
                bar,
                "",
                accepted,
                reason,
                direction,
                0m,
                0m,
                0m,
                _entryBasePrice,
                0m,
                0m,
                0m,
                false,
                volume);
        }

        private void RecordOrderEvent(Order order, string eventType, string reason)
        {
            if (!StatisticsCollectorEnabled || order == null)
                return;

            RecordTradeEvent(
                eventType,
                _lastProcessedSignalBar,
                order.Direction.ToString(),
                eventType.IndexOf("error", StringComparison.OrdinalIgnoreCase) < 0,
                $"{reason}; order_id={order.Id}; order_state={order.State}",
                order.QuantityToFill);
        }

        private string GetSignalKey(int bar)
        {
            if (bar < 0 || bar >= CurrentBar)
                return "";

            return _instanceId + ":" + bar.ToString(CultureInfo.InvariantCulture) + ":" +
                   GetCandle(bar).LastTime.Ticks.ToString(CultureInfo.InvariantCulture);
        }

        private void RecordDeltaRecoveryTrade(MarketDataArg arg)
        {
            var bar = CurrentBar - 1;
            if (bar < 0 || arg.Volume <= 0m)
                return;

            var tradeDirection = arg.Direction.ToString();
            lock (_deltaRecoveryLock)
            {
                if (_deltaRecoveryCurrent == null || _deltaRecoveryCurrent.Bar != bar)
                {
                    _deltaRecoveryPrevious = _deltaRecoveryCurrent;
                    _deltaRecoveryCurrent = new DeltaRecoveryPath { Bar = bar };
                }

                var path = _deltaRecoveryCurrent;
                path.Volume += arg.Volume;
                if (tradeDirection == "Buy")
                    path.Delta += arg.Volume;
                else if (tradeDirection == "Sell")
                    path.Delta -= arg.Volume;
                else
                    path.HasUnknownDirection = true;

                if (path.Delta < path.Minimum)
                    path.Minimum = path.Delta;
                if (path.Delta > path.Maximum)
                    path.Maximum = path.Delta;
            }
        }

        private bool PassesDeltaRecoveryFilter(int bar, OrderDirections direction)
        {
            if (!UseDeltaRecoveryFilter)
                return true;

            var status = "ok";
            decimal? recovery = null;
            decimal tickVolume = 0m;
            decimal tickDelta = 0m;
            decimal candleVolume = 0m;
            decimal candleDelta = 0m;
            try
            {
                if (bar < 0 || bar >= CurrentBar)
                    status = "unavailable";
                else
                {
                    var candle = GetCandle(bar);
                    candleVolume = candle.Volume;
                    candleDelta = candle.Delta;
                    lock (_deltaRecoveryLock)
                    {
                        var path = _deltaRecoveryCurrent != null && _deltaRecoveryCurrent.Bar == bar
                            ? _deltaRecoveryCurrent
                            : _deltaRecoveryPrevious != null && _deltaRecoveryPrevious.Bar == bar
                                ? _deltaRecoveryPrevious : null;
                        if (path == null || path.Volume <= 0m)
                            status = "no_trade_history";
                        else
                        {
                            tickVolume = path.Volume;
                            tickDelta = path.Delta;
                            if (path.HasUnknownDirection)
                                status = "unknown_trade_direction";
                            else if (Math.Abs(path.Volume - candleVolume) > 0.01m ||
                                     Math.Abs(path.Delta - candleDelta) > 0.01m)
                                status = "incomplete_trade_history";
                            else if (path.Maximum <= path.Minimum)
                                status = "zero_delta_range";
                            else
                                recovery = direction == OrderDirections.Buy
                                    ? (path.Delta - path.Minimum) / (path.Maximum - path.Minimum)
                                    : (path.Maximum - path.Delta) / (path.Maximum - path.Minimum);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                status = "read_error:" + ex.GetType().Name;
            }

            if (status == "ok" && recovery.HasValue && MinimumIntrabarDeltaRecovery >= 0m &&
                MinimumIntrabarDeltaRecovery <= 1m && recovery.Value >= MinimumIntrabarDeltaRecovery)
                return true;

            RecordTradeEvent("entry_blocked_delta_recovery", bar, direction.ToString(), false,
                $"status={status}; recovery={recovery}; threshold={MinimumIntrabarDeltaRecovery}; tick_volume={tickVolume}; candle_volume={candleVolume}; tick_delta={tickDelta}; candle_delta={candleDelta}", 0m);
            return false;
        }

        private bool PassesDeltaFilter(int bar, OrderDirections direction)
        {
            if (!UseDeltaFilter)
                return true;

            var status = "ok";
            decimal delta = 0m;
            decimal volume = 0m;
            decimal? directionalPercent = null;
            try
            {
                if (bar < 0 || bar >= CurrentBar)
                    status = "unavailable";
                else
                {
                    var candle = GetCandle(bar);
                    delta = candle.Delta;
                    volume = candle.Volume;
                    if (volume <= 0m)
                        status = "no_volume";
                    else if (delta > volume || delta < -volume)
                        status = "invalid_delta";
                    else
                        directionalPercent = 100m * (direction == OrderDirections.Buy ? delta : -delta) / volume;
                }
            }
            catch (Exception ex)
            {
                status = "read_error:" + ex.GetType().Name;
            }

            if (status == "ok" && directionalPercent.HasValue &&
                directionalPercent.Value > MinimumDirectionalDeltaPercent)
                return true;

            RecordTradeEvent("entry_blocked_delta", bar, direction.ToString(), false,
                $"status={status}; directional_pct={directionalPercent}; threshold_pct={MinimumDirectionalDeltaPercent}; delta={delta}; volume={volume}", 0m);
            return false;
        }

        private bool PassesAbsoluteDeltaFilter(int bar, OrderDirections direction)
        {
            if (!UseAbsoluteDeltaFilter)
                return true;

            var status = "ok";
            decimal delta = 0m;
            try
            {
                if (bar < 0 || bar >= CurrentBar)
                    status = "unavailable";
                else
                    delta = GetCandle(bar).Delta;
            }
            catch (Exception ex)
            {
                status = "read_error:" + ex.GetType().Name;
            }

            var directionalDelta = direction == OrderDirections.Buy ? delta : -delta;
            if (status == "ok" && MinimumAbsoluteDirectionalDelta >= 0m &&
                directionalDelta > MinimumAbsoluteDirectionalDelta)
                return true;

            RecordTradeEvent("entry_blocked_absolute_delta", bar, direction.ToString(), false,
                $"status={status}; directional_delta={directionalDelta}; threshold={MinimumAbsoluteDirectionalDelta}", 0m);
            return false;
        }

        private bool PassesImbalanceFilter(int bar, OrderDirections direction)
        {
            if (!UseImbalanceFilter)
                return true;

            string status;
            decimal? directionalPercent = null;
            try
            {
                var stats = GetImbalanceStats(bar);
                status = stats.Status;
                directionalPercent = direction == OrderDirections.Buy
                    ? stats.BuyMaxPercent : stats.SellMaxPercent;
            }
            catch (Exception ex)
            {
                status = "read_error:" + ex.GetType().Name;
            }

            if (MinimumImbalancePercent > 0 && status == "ok" &&
                directionalPercent.HasValue && directionalPercent.Value >= MinimumImbalancePercent)
                return true;

            RecordTradeEvent("entry_blocked_imbalance", bar, direction.ToString(), false,
                $"status={status}; directional_pct={directionalPercent}; threshold_pct={MinimumImbalancePercent}", 0m);
            return false;
        }

        private (string Status, decimal? BuyMaxPercent, decimal? SellMaxPercent, int LevelCount) GetImbalanceStats(int bar)
        {
            if (bar < 0 || bar >= CurrentBar || TickSize <= 0)
                return ("unavailable", null, null, 0);

            var levels = new Dictionary<decimal, (decimal Bid, decimal Ask)>();
            foreach (var level in GetCandle(bar).GetAllPriceLevels())
            {
                if (level != null)
                    levels[level.Price] = (level.Bid, level.Ask);
            }

            if (levels.Count == 0)
                return ("no_footprint_data", null, null, 0);

            decimal? buyMax = null;
            decimal? sellMax = null;
            foreach (var pair in levels)
            {
                // ATAS footprint imbalance compares Ask at the upper price with Bid one tick below.
                if (pair.Value.Ask > 0 && levels.TryGetValue(pair.Key - TickSize, out var lower) && lower.Bid > 0)
                {
                    var ratio = 100m * pair.Value.Ask / lower.Bid;
                    if (!buyMax.HasValue || ratio > buyMax.Value)
                        buyMax = ratio;
                }

                if (pair.Value.Bid > 0 && levels.TryGetValue(pair.Key + TickSize, out var upper) && upper.Ask > 0)
                {
                    var ratio = 100m * pair.Value.Bid / upper.Ask;
                    if (!sellMax.HasValue || ratio > sellMax.Value)
                        sellMax = ratio;
                }
            }

            return (buyMax.HasValue || sellMax.HasValue ? "ok" : "no_comparable_levels",
                    buyMax, sellMax, levels.Count);
        }

        private void WriteFootprintSnapshot(int bar)
        {
            try
            {
                var signalKey = GetSignalKey(bar);
                var candle = GetCandle(bar);
                var path = Path.Combine(Path.GetDirectoryName(GetStatisticsPath()), "CashReaperFootprint-imbalance-v1.csv");
                lock (StatsFileLock)
                {
                    var fileExists = File.Exists(path);
                    if (fileExists && new FileInfo(path).Length >= 256L * 1024L * 1024L)
                    {
                        if (!_footprintCollectorLimitNotified)
                        {
                            _footprintCollectorLimitNotified = true;
                            ShowStrategyNotification($"{GetInstanceLabel()}: журнал кластеров достиг 256 МБ; его запись остановлена. Остальная статистика собирается.");
                        }
                        return;
                    }
                    using (var writer = new StreamWriter(path, append: true))
                    {
                        if (!fileExists)
                            writer.WriteLine("signal_key,instance,instrument,bar,bar_last_time,price,bid,ask,tick_size");

                        foreach (var level in candle.GetAllPriceLevels())
                        {
                            if (level == null)
                                continue;

                            writer.WriteLine(string.Join(",",
                                Csv(signalKey), Csv(GetInstanceLabel()),
                                Csv(Security == null ? "" : Security.ToString()), Csv(bar),
                                Csv(candle.LastTime.ToString("O", CultureInfo.InvariantCulture)),
                                Csv(level.Price), Csv(level.Bid), Csv(level.Ask), Csv(TickSize)));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                RaiseDebug($"Footprint Collector error: {ex.Message}");
            }
        }

        private void WriteStatsLine(
            string eventType,
            int bar,
            string signalName,
            bool accepted,
            string reason,
            string direction,
            decimal open,
            decimal high,
            decimal low,
            decimal close,
            decimal body,
            decimal range,
            decimal diff,
            bool engulf,
            decimal volume)
        {
            try
            {
                var isSignalBar = bar >= 0 && bar < CurrentBar &&
                    (eventType == "signal" || (eventType == "bar" && engulf));
                var imbalanceStatus = "not_signal_bar";
                decimal? buyMaxPercent = null;
                decimal? sellMaxPercent = null;
                var footprintLevelCount = 0;
                if (isSignalBar)
                {
                    try
                    {
                        var stats = GetImbalanceStats(bar);
                        imbalanceStatus = stats.Status;
                        buyMaxPercent = stats.BuyMaxPercent;
                        sellMaxPercent = stats.SellMaxPercent;
                        footprintLevelCount = stats.LevelCount;
                    }
                    catch (Exception ex)
                    {
                        imbalanceStatus = "read_error:" + ex.GetType().Name;
                    }
                }

                var signalKey = isSignalBar ? GetSignalKey(bar) : _activeSignalKey;
                var signalDirection = signalName;
                if (signalDirection == "" && eventType == "bar" && engulf)
                    signalDirection = close > open ? "LONG" : close < open ? "SHORT" : "";
                decimal? directionalImbalance = signalDirection == "LONG" ? buyMaxPercent :
                    signalDirection == "SHORT" ? sellMaxPercent : null;

                var path = GetStatisticsPath();
                lock (StatsFileLock)
                {
                    var fileExists = File.Exists(path);
                    using (var writer = new StreamWriter(path, append: true))
                    {
                        if (!fileExists)
                            writer.WriteLine(GetStatisticsHeader());

                        writer.WriteLine(string.Join(",",
                        Csv(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)),
                        Csv(GetInstanceLabel()),
                        Csv(Security == null ? "" : Security.ToString()),
                        Csv(eventType),
                        Csv(bar.ToString(CultureInfo.InvariantCulture)),
                        Csv(_tradeState.ToString()),
                        Csv(open),
                        Csv(high),
                        Csv(low),
                        Csv(close),
                        Csv(body),
                        Csv(range),
                        Csv(diff),
                        Csv(engulf),
                        Csv(signalName),
                        Csv(direction),
                        Csv(accepted),
                        Csv(reason),
                        Csv(volume),
                        Csv(_takeProfitPrice),
                        Csv(_stopLossPrice),
                        Csv(TakeProfitPoints),
                        Csv(StopLossPoints),
                        Csv(ProtectionCalculationMode),
                        Csv(_lossSeriesStep),
                        Csv(RangeSize),
                        Csv(CommissionPerContract),
                        Csv(CommissionPercent),
                        Csv(Portfolio == null ? "" : Portfolio.ToString()),
                        Csv(Connector == null ? "" : Connector.ToString()),
                        Csv(MarketReplayMode),
                        Csv(TradingEnabled),
                        Csv(GetActivePosition()),
                        Csv(_entryExecutionPrice),
                        Csv(CalculateUnrealizedPnlPoints(close)),
                        Csv(_lastTradePnlPoints),
                        Csv(_totalPnlPoints),
                        Csv(""),
                        Csv(""),
                        Csv(""),
                        Csv(directionalImbalance.HasValue ? directionalImbalance.Value.ToString(CultureInfo.InvariantCulture) : ""),
                        Csv(CurrentBar > 0 ? GetRobotTime(CurrentBar - 1).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) : ""),
                        Csv(Portfolio == null ? "" : Portfolio.Currency.ToString()),
                        Csv(Portfolio == null ? 0m : Portfolio.ClosedPnL),
                        Csv(Portfolio == null ? 0m : Portfolio.OpenPnL),
                        Csv(_dailyAccountDate == DateTime.MinValue ? "" : _dailyAccountDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                        Csv(_dailyAccountEquityBaseline),
                        Csv(Portfolio == null ? 0m : Portfolio.ClosedPnL + Portfolio.OpenPnL - _dailyAccountEquityBaseline),
                        Csv(_dailyClosedPnlMoneyEstimate),
                        Csv(_dailyCommissionMoneyEstimate),
                        Csv(GetDailyStrategyMoneyEstimate(CurrentBar > 0 ? GetCandle(CurrentBar - 1).Close : 0m)),
                        Csv(PointValue),
                        Csv(DailyMoneyPnlSource),
                        Csv(AccountProfitTargetEnabled),
                        Csv(DailyProfitUnit),
                        Csv(DailyProfitUnit == ProfitTargetUnit.Points ? DailyProfitTargetPoints : AccountProfitTarget),
                        Csv(AccountLossLimitEnabled),
                        Csv(DailyLossUnit),
                        Csv(DailyLossUnit == ProfitTargetUnit.Points ? DailyLossLimitPoints : AccountLossLimit),
                        Csv(_accountStopBlockedDate == DateTime.MinValue ? "" : _accountStopBlockedDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                        Csv(_entryOrder == null ? "" : _entryOrder.Id),
                        Csv(_entryOrder == null ? "" : _entryOrder.State.ToString()),
                        Csv(_moneyEstimatePosition),
                        Csv(signalKey),
                        Csv(imbalanceStatus),
                        Csv(buyMaxPercent.HasValue ? buyMaxPercent.Value.ToString(CultureInfo.InvariantCulture) : ""),
                        Csv(sellMaxPercent.HasValue ? sellMaxPercent.Value.ToString(CultureInfo.InvariantCulture) : ""),
                        Csv(footprintLevelCount)));
                    }
                }
            }
            catch (Exception ex)
            {
                RaiseDebug($"Statistics Collector error: {ex.Message}");
            }
        }

        private string GetStatisticsHeader()
        {
            return "time,instance,instrument,event,bar,state,open,high,low,close,body,range,macd_difference,body_engulf,signal,direction,accepted,reason,volume,tp,sl,tp_points,sl_points,protection_mode,series_step,range_size,commission_per_contract,commission_percent,portfolio,connector,market_replay_mode,trading_enabled,current_position,entry_price,unrealized_pnl_points,last_trade_pnl_points,total_pnl_points,delta,delta_volume,cvd,imbalance,chart_time,account_currency,atas_closed_pnl,atas_open_pnl,daily_chart_date,atas_daily_equity_baseline,atas_daily_pnl,strategy_daily_closed_estimate,strategy_daily_commission_estimate,strategy_daily_net_estimate,point_value,money_pnl_source,profit_limit_enabled,profit_limit_unit,profit_limit_value,loss_limit_enabled,loss_limit_unit,loss_limit_value,blocked_chart_date,entry_order_id,entry_order_state,estimate_position,signal_key,imbalance_status,imbalance_buy_max_pct,imbalance_sell_max_pct,footprint_level_count";
        }

        private string GetStatisticsPath()
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "CashReaper");

            Directory.CreateDirectory(directory);

            var fileName = string.IsNullOrWhiteSpace(StatisticsFileName)
                ? "CashReaperStats.csv"
                : StatisticsFileName;

            // The schema changed; never append new columns under an old CSV header.
            var diagnosticName = Path.GetFileNameWithoutExtension(fileName) + "-imbalance-v1" +
                (string.IsNullOrEmpty(Path.GetExtension(fileName)) ? ".csv" : Path.GetExtension(fileName));
            return Path.Combine(directory, diagnosticName);
        }

        private string Csv(object value)
        {
            var text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
            // One physical line per event lets the bridge upload complete append-only chunks.
            text = text.Replace("\r", " ").Replace("\n", " ");
            text = text.Replace("\"", "\"\"");
            return $"\"{text}\"";
        }

        private bool IsWeekendTradingAllowed(int bar)
        {
            var day = GetRobotTime(bar).DayOfWeek;
            return (day != DayOfWeek.Saturday || TradeOnSaturday) &&
                   (day != DayOfWeek.Sunday || TradeOnSunday);
        }

        private bool CheckAccountLimits(int bar)
        {
            if (_accountStopClosing)
            {
                ClosePositionForAccountLimit();
                if (_accountStopClosing)
                    return false;
            }

            if (!AccountProfitTargetEnabled && !AccountLossLimitEnabled)
            {
                _accountLimitInvalidNotified = false;
                _accountStopBlockedDate = DateTime.MinValue;
                _accountStopReason = AccountStopReason.None;
                return true;
            }

            var chartDate = GetRobotTime(CurrentBar - 1).Date;
            var marketPrice = GetCandle(CurrentBar - 1).Close;
            UpdateDailyPointSession(chartDate, marketPrice, GetActivePosition());
            if (_accountStopBlockedDate == chartDate)
                return false;

            if (_accountStopBlockedDate != DateTime.MinValue)
            {
                _accountStopBlockedDate = DateTime.MinValue;
                _accountStopReason = AccountStopReason.None;
            }

            var profitInPoints = DailyProfitUnit == ProfitTargetUnit.Points;
            var lossInPoints = DailyLossUnit == ProfitTargetUnit.Points;
            var needsAccountPnl = (AccountProfitTargetEnabled && !profitInPoints) ||
                                  (AccountLossLimitEnabled && !lossInPoints);
            var invalidReason = needsAccountPnl && DailyMoneyPnlSource == MoneyPnlSource.AtasPortfolio && Portfolio == null
                ? "счёт не выбран для денежного лимита"
                : needsAccountPnl && DailyMoneyPnlSource == MoneyPnlSource.StrategyTradesEstimate && PointValue <= 0
                    ? "стоимость пункта должна быть больше нуля для расчёта денег по сделкам"
                : AccountProfitTargetEnabled && (profitInPoints ? DailyProfitTargetPoints : AccountProfitTarget) <= 0
                    ? "порог прибыли в выбранных единицах должен быть больше нуля"
                    : AccountLossLimitEnabled && (lossInPoints ? DailyLossLimitPoints : AccountLossLimit) <= 0
                        ? "порог убытка в выбранных единицах должен быть больше нуля"
                        : null;

            if (invalidReason != null)
            {
                if (!_accountLimitInvalidNotified)
                {
                    _accountLimitInvalidNotified = true;
                    RecordTradeEvent("daily_limit_invalid", bar, "", false,
                        $"{invalidReason}; profit={AccountProfitTargetEnabled}/{DailyProfitUnit}/{(profitInPoints ? DailyProfitTargetPoints : AccountProfitTarget)}; loss={AccountLossLimitEnabled}/{DailyLossUnit}/{(lossInPoints ? DailyLossLimitPoints : AccountLossLimit)}", 0m);
                    ShowStrategyNotification($"{GetInstanceLabel()}: {invalidReason}. Новые входы заблокированы.");
                }

                return false;
            }

            _accountLimitInvalidNotified = false;
            var closedPnl = Portfolio == null ? 0m : Portfolio.ClosedPnL;
            var openPnl = Portfolio == null ? 0m : Portfolio.OpenPnL;
            var atasDailyPnl = closedPnl + openPnl - _dailyAccountEquityBaseline;
            var strategyDailyPnl = GetDailyStrategyMoneyEstimate(marketPrice);
            var accountPnl = DailyMoneyPnlSource == MoneyPnlSource.StrategyTradesEstimate
                ? strategyDailyPnl
                : atasDailyPnl;
            var openPoints = GetActivePosition() == 0 ? 0m :
                CalculateClosedTradePnlPoints(GetActivePosition(), _dailyOpenBasePrice, marketPrice);
            var dailyPoints = _dailyClosedPnlPoints + openPoints;
            var reason = AccountStopReason.None;

            if (AccountProfitTargetEnabled && (profitInPoints
                    ? dailyPoints >= DailyProfitTargetPoints
                    : accountPnl >= AccountProfitTarget))
                reason = AccountStopReason.Profit;
            else if (AccountLossLimitEnabled && (lossInPoints
                         ? dailyPoints <= -DailyLossLimitPoints
                         : accountPnl <= -AccountLossLimit))
                reason = AccountStopReason.Loss;

            if (reason == AccountStopReason.None)
                return true;

            _accountStopBlockedDate = chartDate;
            _accountStopReason = reason;

            var limit = reason == AccountStopReason.Profit
                ? (profitInPoints ? DailyProfitTargetPoints : AccountProfitTarget)
                : (lossInPoints ? DailyLossLimitPoints : AccountLossLimit);
            var resultInPoints = reason == AccountStopReason.Profit ? profitInPoints : lossInPoints;
            var resultText = resultInPoints
                ? $"daily_points={dailyPoints}; closed_points={_dailyClosedPnlPoints}; open_points={openPoints}; limit={limit}; unit=points"
                : $"source={DailyMoneyPnlSource}; closed_pnl={closedPnl}; open_pnl={openPnl}; baseline={_dailyAccountEquityBaseline}; atas_daily_pnl={atasDailyPnl}; strategy_daily_pnl={strategyDailyPnl}; selected_pnl={accountPnl}; limit={limit}; currency={(Portfolio == null ? "unknown" : Portfolio.Currency.ToString())}";
            RecordTradeEvent(reason == AccountStopReason.Profit ? "account_profit_target_reached" : "account_loss_limit_reached",
                bar, "", false, resultText, 0m);
            var notification = resultInPoints
                ? $"{GetInstanceLabel()}: дневной лимит {(reason == AccountStopReason.Profit ? "прибыли" : "убытка")} достигнут: {dailyPoints} пунктов (закрытая {_dailyClosedPnlPoints}, открытая {openPoints}; лимит {limit}). Новые входы остановлены до следующего дня."
                : $"{GetInstanceLabel()}: дневной лимит {(reason == AccountStopReason.Profit ? "прибыли" : "убытка")} достигнут: {accountPnl} {(Portfolio == null ? "" : Portfolio.Currency.ToString())} (источник {DailyMoneyPnlSource}; лимит {limit}). Новые входы остановлены до следующего дня.";
            ShowStrategyNotification(notification);

            if (GetActivePosition() != 0)
                ClosePositionForAccountLimit();

            return false;
        }

        private void UpdateDailyPointSession(DateTime chartDate, decimal marketPrice, decimal position)
        {
            if (_dailyPointDate == chartDate)
                return;

            _dailyPointDate = chartDate;
            _dailyClosedPnlPoints = 0m;
            _dailyClosedPnlMoneyEstimate = 0m;
            _dailyCommissionMoneyEstimate = 0m;
            _dailyAccountDate = chartDate;
            _dailyAccountEquityBaseline = Portfolio == null ? 0m : Portfolio.ClosedPnL + Portfolio.OpenPnL;
            // A position carried over midnight contributes only the move observed today.
            _dailyOpenBasePrice = position == 0 ? 0m : marketPrice;
            _moneyEstimateBasePrice = _moneyEstimatePosition == 0 ? 0m : marketPrice;
        }

        private void UpdateDailyMoneyEstimate(MyTrade trade)
        {
            var quantity = Math.Abs(trade.Volume);
            _dailyCommissionMoneyEstimate += quantity * CommissionPerContract +
                quantity * trade.Price * CommissionPercent / 100m;

            var previousPosition = _moneyEstimatePosition;
            var signedQuantity = trade.OrderDirection == OrderDirections.Buy ? quantity : -quantity;
            var activePosition = previousPosition + signedQuantity;
            _moneyEstimatePosition = Math.Abs(activePosition) < 0.00000001m ? 0m : activePosition;

            if (previousPosition == 0)
            {
                if (_moneyEstimatePosition != 0)
                    _moneyEstimateBasePrice = trade.Price;
                return;
            }

            var sameDirection = Math.Sign(previousPosition) == Math.Sign(_moneyEstimatePosition);
            var closedQuantity = sameDirection
                ? Math.Max(0m, Math.Abs(previousPosition) - Math.Abs(_moneyEstimatePosition))
                : Math.Abs(previousPosition);

            if (closedQuantity > 0 && _moneyEstimateBasePrice != 0)
            {
                var direction = previousPosition > 0 ? 1m : -1m;
                _dailyClosedPnlMoneyEstimate +=
                    (trade.Price - _moneyEstimateBasePrice) * direction * closedQuantity * PointValue;
            }

            if (_moneyEstimatePosition == 0)
                _moneyEstimateBasePrice = 0m;
            else if (!sameDirection)
                _moneyEstimateBasePrice = trade.Price;
            else if (Math.Abs(_moneyEstimatePosition) > Math.Abs(previousPosition))
            {
                var addedQuantity = Math.Abs(_moneyEstimatePosition) - Math.Abs(previousPosition);
                _moneyEstimateBasePrice =
                    (_moneyEstimateBasePrice * Math.Abs(previousPosition) + trade.Price * addedQuantity) /
                    Math.Abs(_moneyEstimatePosition);
            }
        }

        private decimal GetDailyStrategyMoneyEstimate(decimal marketPrice)
        {
            var openMoney = _moneyEstimatePosition == 0 || _moneyEstimateBasePrice == 0
                ? 0m
                : (marketPrice - _moneyEstimateBasePrice) * _moneyEstimatePosition * PointValue;
            return _dailyClosedPnlMoneyEstimate + openMoney - _dailyCommissionMoneyEstimate;
        }

        private string GetAccountStopOutcome()
        {
            if (_accountStopReason == AccountStopReason.Profit)
                return "account_profit_target";

            if (_accountStopReason == AccountStopReason.Loss)
                return "account_loss_limit";

            return "account_limit_invalid";
        }

        private void ClosePositionForAccountLimit()
        {
            if (GetActivePosition() == 0)
            {
                if (_accountStopClosing)
                {
                    RecordTradeClosed(GetAccountStopOutcome());
                    ResetTradeState();
                }

                return;
            }

            if (!_accountStopClosing)
            {
                _accountStopClosing = true;
                _closingTrade = true;
                CancelProtectiveOrders();
            }

            if (GetActivePosition() == 0)
            {
                RecordTradeClosed(GetAccountStopOutcome());
                ResetTradeState();
                return;
            }

            var currentBar = CurrentBar - 1;
            if (_emergencyClosing || _lastAccountCloseAttemptBar == currentBar)
                return;

            _lastAccountCloseAttemptBar = currentBar;
            EmergencyFlattenPosition(GetAccountStopOutcome());
        }

        private bool IsTradingTimeAllowed(int bar)
        {
            if (UseSessionHours)
            {
                var sessionOpen = new TimeSpan(Clamp(SessionOpenHour, 0, 23), Clamp(SessionOpenMinute, 0, 59), 0);
                var sessionClose = new TimeSpan(Clamp(SessionCloseHour, 0, 23), Clamp(SessionCloseMinute, 0, 59), 0);
                var current = GetRobotTime(bar).TimeOfDay;
                if (sessionOpen == sessionClose || IsInsidePause(current, sessionClose, sessionOpen))
                    return false;
            }

            if (!TradingTimeLimitEnabled)
                return true;

            var terminalTime = GetRobotTime(bar);
            var start = new TimeSpan(
                Clamp(TradingPauseStartHour, 0, 23),
                Clamp(TradingPauseStartMinute, 0, 59),
                0);
            var end = new TimeSpan(
                Clamp(TradingPauseEndHour, 0, 23),
                Clamp(TradingPauseEndMinute, 0, 59),
                0);

            if (!IsInsidePause(terminalTime.TimeOfDay, start, end))
            {
                _tradingStoppedByTime = false;
                return true;
            }

            if (!_tradingStoppedByTime || _lastTimeLimitNoticeDate.Date != terminalTime.Date)
            {
                _tradingStoppedByTime = true;
                _lastTimeLimitNoticeDate = terminalTime.Date;

                ShowStrategyNotification(
                    $"{GetInstanceLabel()}: включена пауза торговли. RobotTime={terminalTime:HH:mm}; Pause={GetPausePeriodText()}. Новые входы запрещены.");
            }

            return false;
        }

        private DateTime GetRobotTime(int bar)
        {
            try
            {
                dynamic candle = GetCandle(bar);
                return GetRobotTimeFromChartTime((DateTime)candle.Time);
            }
            catch
            {
                var robotOffset = Math.Clamp(RobotTimeGmtOffsetHours, -12m, 14m);
                return DateTime.UtcNow.AddMinutes((double)(robotOffset * 60m));
            }
        }

        private DateTime GetRobotTimeFromChartTime(DateTime chartTime)
        {
            var robotOffset = Math.Clamp(RobotTimeGmtOffsetHours, -12m, 14m);
            var chartOffset = Math.Clamp(ChartTimeGmtOffsetHours, -12m, 14m);
            if (chartTime.Kind == DateTimeKind.Utc)
                return DateTime.SpecifyKind(chartTime.AddMinutes((double)(robotOffset * 60m)), DateTimeKind.Unspecified);
            return DateTime.SpecifyKind(chartTime, DateTimeKind.Unspecified)
                .AddMinutes((double)((robotOffset - chartOffset) * 60m));
        }

        private void EnforceScheduleFromMarketEvent(MarketDataArg arg)
        {
            if (arg == null || CurrentBar <= 0)
                return;

            var eventTime = GetRobotTimeFromChartTime(arg.Time);
            // Ignore backfilled historical prints when the strategy attaches to a live chart.
            if (eventTime >= GetRobotTime(CurrentBar - 1))
                EnforceSchedule(eventTime);
        }

        private void EnforceSchedule(DateTime robotTime)
        {
            // A historical candle callback must not reopen trading after a newer tick
            // has already reached the session/pause boundary.
            if (robotTime < _latestScheduleTime)
                return;
            _latestScheduleTime = robotTime;

            var now = robotTime.TimeOfDay;
            var reason = "";
            if (UseSessionHours && CloseBeforeSessionEnd)
            {
                var open = new TimeSpan(Clamp(SessionOpenHour, 0, 23), Clamp(SessionOpenMinute, 0, 59), 0);
                var close = new TimeSpan(Clamp(SessionCloseHour, 0, 23), Clamp(SessionCloseMinute, 0, 59), 0);
                var guard = close.Subtract(TimeSpan.FromMinutes(1));
                if (guard < TimeSpan.Zero)
                    guard = guard.Add(TimeSpan.FromDays(1));
                if (open == close || IsInsidePause(now, guard, open))
                    reason = "session_close";
            }

            if (reason == "" && TradingTimeLimitEnabled && CloseBeforeTradingPause)
            {
                var start = new TimeSpan(Clamp(TradingPauseStartHour, 0, 23), Clamp(TradingPauseStartMinute, 0, 59), 0);
                var end = new TimeSpan(Clamp(TradingPauseEndHour, 0, 23), Clamp(TradingPauseEndMinute, 0, 59), 0);
                var guard = start.Subtract(TimeSpan.FromMinutes(1));
                if (guard < TimeSpan.Zero)
                    guard = guard.Add(TimeSpan.FromDays(1));
                if (start != end && IsInsidePause(now, guard, end))
                    reason = "trading_pause";
            }

            if (reason == "")
            {
                _scheduleClosing = false;
                _scheduleCloseReason = "";
                return;
            }

            _scheduleCloseReason = reason;
            if (!_scheduleClosing)
            {
                _scheduleClosing = true;
                _reversalPending = false;
                RecordTradeEvent("schedule_boundary", Math.Max(0, CurrentBar - 1), "", false,
                    $"reason={reason}; robot_time={robotTime:O}; position={GetActivePosition()}", 0m);
                ShowStrategyNotification($"{GetInstanceLabel()}: граница {reason}, новые входы запрещены. Закрываю позицию, если она есть.");
            }

            if (_entryOrder != null && !IsTerminalOrder(_entryOrder))
                TryCancelOrder(_entryOrder, "schedule_entry_cancel_requested");

            if (GetActivePosition() != 0 && !_emergencyClosing)
            {
                CancelProtectiveOrders();
                EmergencyFlattenPosition(reason);
            }
        }

        private bool IsInsidePause(TimeSpan current, TimeSpan start, TimeSpan end)
        {
            if (start == end)
                return false;

            if (start < end)
                return current >= start && current < end;

            return current >= start || current < end;
        }

        private string GetPausePeriodText()
        {
            return
                $"{Clamp(TradingPauseStartHour, 0, 23):00}:{Clamp(TradingPauseStartMinute, 0, 59):00}" +
                "-" +
                $"{Clamp(TradingPauseEndHour, 0, 23):00}:{Clamp(TradingPauseEndMinute, 0, 59):00}";
        }

        private int Clamp(int value, int min, int max)
        {
            if (value < min)
                return min;

            if (value > max)
                return max;

            return value;
        }

        private string GetInstanceLabel()
        {
            var securityName = Security == null ? "unknown" : Security.ToString();
            return $"CashReaper[{securityName}; {_instanceId}]";
        }

        private string GetTradingContextText()
        {
            return
                $"Portfolio={(Portfolio == null ? "empty" : Portfolio.ToString())}; " +
                $"Security={(Security == null ? "empty" : Security.ToString())}; " +
                $"Connector={(Connector == null ? "empty" : Connector.ToString())}; " +
                $"Position={GetActivePosition()}";
        }

        private void EnsureSize(int size)
        {
            if (_fastEma.Length >= size)
                return;

            Array.Resize(ref _fastEma, size);
            Array.Resize(ref _slowEma, size);
            Array.Resize(ref _macd, size);
            Array.Resize(ref _signal, size);
            Array.Resize(ref _difference, size);
        }

        private void FinalizeMacdThrough(int lastClosedBar)
        {
            if (lastClosedBar < 0)
                return;

            if (_finalizedFastPeriod != FastPeriod || _finalizedSlowPeriod != SlowPeriod ||
                _finalizedSignalPeriod != SignalPeriod)
            {
                if (_lastFinalizedMacdBar >= 0)
                    RecordTradeEvent("macd_settings_changed", lastClosedBar, "", true,
                        $"old={_finalizedFastPeriod}/{_finalizedSlowPeriod}/{_finalizedSignalPeriod}; new={FastPeriod}/{SlowPeriod}/{SignalPeriod}; source=Close", 0m);

                _lastFinalizedMacdBar = -1;
                _finalizedFastPeriod = FastPeriod;
                _finalizedSlowPeriod = SlowPeriod;
                _finalizedSignalPeriod = SignalPeriod;
                _lastMacdCrossCheckedBar = -1;
                _lastNonZeroDifferenceSign = 0;
                _lastPositiveCrossBar = -1;
                _lastNegativeCrossBar = -1;
                _consumedPositiveCrossBar = -1;
                _consumedNegativeCrossBar = -1;
            }

            EnsureSize(lastClosedBar + 1);
            if (_lastFinalizedMacdBar < 0)
            {
                var firstClose = GetCandle(0).Close;
                _fastEma[0] = firstClose;
                _slowEma[0] = firstClose;
                _macd[0] = 0m;
                _signal[0] = 0m;
                _difference[0] = 0m;
            }

            for (var i = Math.Max(1, _lastFinalizedMacdBar + 1); i <= lastClosedBar; i++)
            {
                var close = GetCandle(i).Close;
                _fastEma[i] = CalcEma(close, _fastEma[i - 1], FastPeriod);
                _slowEma[i] = CalcEma(close, _slowEma[i - 1], SlowPeriod);
                _macd[i] = _fastEma[i] - _slowEma[i];
                _signal[i] = CalcEma(_macd[i], _signal[i - 1], SignalPeriod);
                _difference[i] = _macd[i] - _signal[i];
            }

            _lastFinalizedMacdBar = lastClosedBar;
        }

        private decimal CalcEma(decimal value, decimal previousEma, int period)
        {
            var k = 2m / (period + 1);
            return value * k + previousEma * (1 - k);
        }

        private bool IsBullish(dynamic candle)
        {
            return candle.Close > candle.Open;
        }

        private bool IsBearish(dynamic candle)
        {
            return candle.Close < candle.Open;
        }

        private bool BodyEngulfs(dynamic current, dynamic previous)
        {
            var currentHigh = Math.Max(current.Open, current.Close);
            var currentLow = Math.Min(current.Open, current.Close);

            var previousHigh = Math.Max(previous.Open, previous.Close);
            var previousLow = Math.Min(previous.Open, previous.Close);

            return currentHigh >= previousHigh && currentLow <= previousLow;
        }
    }
}
