using System;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.IO;
using ATAS.DataFeedsCore;
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

        public enum MacdFilterMode
        {
            Difference = 0,
            LineCross = 1
        }

        [Display(GroupName = "01. Trading", Name = "Enable trading", Order = 10)]
        public bool TradingEnabled { get; set; } = true;

        [Display(GroupName = "02. Replay and diagnostics", Name = "Market Replay mode", Order = 20)]
        public bool MarketReplayMode { get; set; } = true;

        [Display(GroupName = "02. Replay and diagnostics", Name = "Collect CSV statistics", Order = 30)]
        public bool StatisticsCollectorEnabled { get; set; } = true;

        [Display(GroupName = "02. Replay and diagnostics", Name = "Debug notifications", Order = 40)]
        public bool DebugMode { get; set; } = true;

        [Display(GroupName = "03. Recovery", Name = "Auto recovery", Order = 50)]
        public bool AutoRecoveryEnabled { get; set; } = true;

        [Display(GroupName = "03. Recovery", Name = "Skip old signal on start", Order = 60)]
        public bool SkipOldSignalOnStart { get; set; } = true;

        [Display(GroupName = "03. Recovery", Name = "Restore TP/SL on start", Order = 70)]
        public bool RestoreProtectiveOrdersOnStart { get; set; } = true;

        [Display(GroupName = "04. Time filter", Name = "Use trading pause", Order = 80)]
        public bool TradingTimeLimitEnabled { get; set; } = true;

        [Display(GroupName = "04. Time filter", Name = "Trade on Saturday", Description = "Allow new entries on Saturday in chart time. Existing positions remain protected.", Order = 81)]
        public bool TradeOnSaturday { get; set; } = false;

        [Display(GroupName = "04. Time filter", Name = "Trade on Sunday", Description = "Allow new entries on Sunday in chart time. Existing positions remain protected.", Order = 82)]
        public bool TradeOnSunday { get; set; } = false;

        [Display(GroupName = "05. Risk sizing", Name = "Calculate volume from risk", Description = "If enabled, volume = reference balance x risk percentage / (stop distance x point value). If disabled, Base volume is used.", Order = 120)]
        public bool RiskSizingEnabled { get; set; } = false;

        [Display(GroupName = "05a. Daily profit limit", Name = "Enable profit limit", Description = "Stop new entries until the next chart day and close this strategy's position when the selected daily profit target is reached.", Order = 121)]
        public bool AccountProfitTargetEnabled { get; set; } = false;

        [Display(GroupName = "05a. Daily profit limit", Name = "Calculate profit in", Description = "Account currency: ATAS ClosedPnL + OpenPnL for the selected account. Points: sum of this strategy's closed and open price movements today, without volume or commissions.", Order = 122)]
        public ProfitTargetUnit DailyProfitUnit { get; set; } = ProfitTargetUnit.AccountCurrency;

        [Display(GroupName = "05a. Daily profit limit", Name = "Profit target: account currency", Description = "Used only when Calculate profit in is AccountCurrency. Enter a positive amount.", Order = 1221)]
        public decimal AccountProfitTarget { get; set; } = 2m;

        [Display(GroupName = "05a. Daily profit limit", Name = "Profit target: points", Description = "Used only when Calculate profit in is Points. Closed trades today plus the open position's price movement; without volume or commissions. Enter a positive number.", Order = 1222)]
        public decimal DailyProfitTargetPoints { get; set; } = 2m;

        [Display(GroupName = "05b. Daily loss limit", Name = "Enable loss limit", Description = "Stop new entries until the next chart day and close this strategy's position when the selected daily loss limit is reached.", Order = 123)]
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
        public bool SeriesSizingEnabled { get; set; } = true;

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

        [Display(GroupName = "07. Signal", Name = "Range size", Order = 200)]
        public int RangeSize { get; set; } = 5;

        [Display(GroupName = "07. Signal", Name = "MACD filter mode", Description = "Difference: long when MACD is above signal, short when below. Line cross: long only when MACD crosses above signal on the closed signal bar, short only when it crosses below. Both modes also require the existing body-engulfing pattern.", Order = 205)]
        public MacdFilterMode MacdMode { get; set; } = MacdFilterMode.Difference;

        [Display(GroupName = "08. TP/SL", Name = "TP/SL mode", Order = 250)]
        public ProtectionMode ProtectionCalculationMode { get; set; } = ProtectionMode.Points;

        [Display(GroupName = "06. Series sizing", Name = "Series mode", Order = 160)]
        public SeriesMode SeriesSizingMode { get; set; } = SeriesMode.Martingale;

        [Display(GroupName = "06. Series sizing", Name = "Max series step", Order = 170)]
        public int MaxSeriesStep { get; set; } = 15;

        [Display(GroupName = "07. Signal", Name = "MACD fast period", Order = 210)]
        public int FastPeriod { get; set; } = 12;

        [Display(GroupName = "07. Signal", Name = "MACD slow period", Order = 220)]
        public int SlowPeriod { get; set; } = 26;

        [Display(GroupName = "07. Signal", Name = "MACD signal period", Order = 230)]
        public int SignalPeriod { get; set; } = 9;

        [Display(GroupName = "01. Trading", Name = "Base volume", Order = 11)]
        public decimal Volume { get; set; } = 0.1m;

        [Display(GroupName = "01. Trading", Name = "Min volume", Order = 12)]
        public decimal MinVolume { get; set; } = 0.1m;

        [Display(GroupName = "01. Trading", Name = "Max volume (0 = off)", Order = 13)]
        public decimal MaxVolume { get; set; } = 0m;

        [Display(GroupName = "01. Trading", Name = "Volume step", Order = 14)]
        public decimal VolumeStep { get; set; } = 0.1m;

        [Display(GroupName = "08. TP/SL", Name = "Take profit points", Order = 260)]
        public decimal TakeProfitPoints { get; set; } = 0.31m;

        [Display(GroupName = "08. TP/SL", Name = "Stop loss points", Order = 270)]
        public decimal StopLossPoints { get; set; } = 0.25m;

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
        public decimal CommissionPerContract { get; set; } = 0m;

        [Display(GroupName = "09. Commission", Name = "Commission %", Order = 360)]
        public decimal CommissionPercent { get; set; } = 0m;

        [Display(GroupName = "02. Replay and diagnostics", Name = "CSV file name", Order = 41)]
        public string StatisticsFileName { get; set; } = "CashReaperStats.csv";

        private decimal[] _fastEma = Array.Empty<decimal>();
        private decimal[] _slowEma = Array.Empty<decimal>();
        private decimal[] _macd = Array.Empty<decimal>();
        private decimal[] _signal = Array.Empty<decimal>();
        private decimal[] _difference = Array.Empty<decimal>();

        private readonly string _instanceId = Guid.NewGuid().ToString("N").Substring(0, 8);

        private int _lastProcessedSignalBar = -1;
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

        private Order _entryOrder;
        private Order _emergencyOrder;
        private Order _takeProfitOrder;
        private Order _stopLossOrder;
        private Order _pendingCancelTakeProfitOrder;
        private Order _pendingCancelStopLossOrder;

        private OrderDirections _entryDirection;
        private TradeState _tradeState = TradeState.Idle;

        private bool _entrySent;
        private bool _protectiveOrdersSent;
        private bool _protectivePlacementStarted;
        private bool _closingTrade;
        private bool _tradingStoppedByTime;
        private bool _accountLimitInvalidNotified;
        private bool _accountStopClosing;
        private AccountStopReason _accountStopReason;
        private bool _emergencyClosing;

        private decimal _takeProfitPrice;
        private decimal _stopLossPrice;
        private decimal _activeVolume;
        private decimal _entryBasePrice;
        private decimal _entryExecutionPrice;
        private decimal _lastTradePnlPoints;
        private decimal _totalPnlPoints;
        private decimal _lastKnownPosition;
        private decimal _trackedPosition;
        private int _lossSeriesStep;

        public CashReaperStrategy() : base(true)
        {
            Name = "CashReaper";
        }

        protected override void OnStarted()
        {
            _trackedPosition = CurrentPosition;
            _moneyEstimatePosition = CurrentPosition;
            _lastKnownPosition = GetActivePosition();

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

                RaiseShowNotification(
                    $"{GetInstanceLabel()}: запущен. Есть открытая позиция: {GetActivePosition()}. Новые входы заблокированы до закрытия позиции.");

                if (RestoreProtectiveOrdersOnStart)
                    RestoreProtectionForExistingPosition();
            }

            var context = GetTradingContextText();

            RaiseShowNotification(
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
        }

        protected override void OnStopping()
        {
            CancelProtectiveOrders();

            if (GetActivePosition() == 0)
            {
                ResetTradeState();
                RaiseShowNotification($"{GetInstanceLabel()}: остановлен. Позиции нет; проверьте подтверждение отмены защитных заявок в ATAS.");
            }
            else
            {
                RaiseShowNotification(
                    $"{GetInstanceLabel()}: остановлен. Внимание: позиция всё ещё открыта: {GetActivePosition()}. Отмена защитных заявок запрошена, но не подтверждена.");
            }

            base.OnStopping();
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

            if (bar < 2)
                return;

            var lastBar = CurrentBar - 1;

            if (bar != lastBar)
                return;

            UpdateDailyPointSession(GetTerminalTime(lastBar).Date, candle.Close, GetActivePosition());
            // Account OpenPnL changes on every price update, not only once per bar.
            CheckAccountLimits(bar);

            var signalBar = bar - 1;

            if (signalBar <= _lastProcessedSignalBar)
                return;

            _lastProcessedSignalBar = signalBar;

            SyncPositionState(signalBar, "before_signal_check");
            RecoverAfterConnectionGap(signalBar);
            RetryProtectiveCancellation(signalBar);
            CheckSignal(signalBar);
            SyncPositionState(signalBar, "after_signal_check");
        }

        protected override void OnOrderChanged(Order order)
        {
            if (order == null)
                return;

            RecordOrderEvent(order, "order_changed", "atas_order_changed");
            UpdateProtectiveOrderState(order);

            if (IsMatchingOrder(_emergencyOrder, order) && IsTerminalOrder(order) && GetActivePosition() != 0)
            {
                _emergencyOrder = null;
                _emergencyClosing = false;
                if (order.State == OrderStates.Failed)
                    RaiseShowNotification($"{GetInstanceLabel()}: заявка закрытия позиции отклонена ATAS. Новые входы заблокированы; закрытие будет повторено.");
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

            if (GetActivePosition() != 0 && _entrySent && !_protectiveOrdersSent)
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
            UpdateDailyPointSession(GetTerminalTime(tradeBar).Date, GetCandle(tradeBar).Close, previousPosition);
            ApplyMyTradeToTrackedPosition(myTrade);
            var activePosition = GetActivePosition();

            if (previousPosition == 0 && activePosition != 0)
            {
                _entryExecutionPrice = myTrade.Price;
                _dailyOpenBasePrice = myTrade.Price;
                _lastTradePnlPoints = 0;
            }

            if (previousPosition != 0 && activePosition == 0)
            {
                _lastTradePnlPoints = CalculateClosedTradePnlPoints(previousPosition, _entryExecutionPrice, myTrade.Price);
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

                    ResetTradeState();
                }

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

            if (previousPosition == 0 && activePosition != 0 && _entrySent && !_protectiveOrdersSent)
            {
                HandleEntryFilled();
                return;
            }

            if (previousPosition != 0 && activePosition == 0 && _entrySent)
                HandleTradeClosed(GetTradeCloseOutcome(myTrade));
        }

        protected override void OnOrderRegisterFailed(Order order, string message)
        {
            if (order == null)
                return;

            RecordOrderEvent(order, "order_register_failed", message);

            if (IsMatchingOrder(_emergencyOrder, order))
            {
                _emergencyOrder = null;
                _emergencyClosing = false;
                RaiseShowNotification($"{GetInstanceLabel()}: заявка закрытия позиции отклонена. Новые входы заблокированы; закрытие будет повторено. {message}");
                return;
            }

            if (IsMatchingOrder(_entryOrder, order))
            {
                ResetTradeState();
                RaiseShowNotification(
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
                RaiseShowNotification($"{GetInstanceLabel()}: защитная заявка отклонена. Закрываю позицию аварийно. {message}");
                EmergencyFlattenPosition("protective_order_rejected");
                return;
            }

            if (IsMatchingOrder(_pendingCancelTakeProfitOrder, order))
                _pendingCancelTakeProfitOrder = null;

            if (IsMatchingOrder(_pendingCancelStopLossOrder, order))
                _pendingCancelStopLossOrder = null;

            RaiseShowNotification(
                $"{GetInstanceLabel()}: заявка отклонена. {message}");
        }

        protected override void OnOrderCancelFailed(Order order, string message)
        {
            if (order == null)
                return;

            RecordOrderEvent(order, "order_cancel_failed", message);
            RaiseDebug($"Не удалось отменить заявку. {message}");
        }

        private void CheckSignal(int bar)
        {
            if (_pendingCancelTakeProfitOrder != null || _pendingCancelStopLossOrder != null)
            {
                RecordBarDecision(bar, null, null, 0, false, "", false, "protective_cancel_pending");
                RaiseDebug($"Вход заблокирован до подтверждения отмены защитных заявок. Bar={bar}");
                return;
            }

            if (bar <= _entriesBlockedUntilBar)
            {
                RecordBarDecision(bar, null, null, 0, false, "", false, "post_close_cooldown");
                RaiseDebug($"Вход заблокирован паузой после закрытия. Bar={bar}; BlockedUntil={_entriesBlockedUntilBar}");
                return;
            }

            if (_tradeState != TradeState.Idle || _entrySent || GetActivePosition() != 0)
            {
                RaiseDebug(
                    $"Вход заблокирован. Bar={bar}; State={_tradeState}; EntrySent={_entrySent}; Position={GetActivePosition()}");
                return;
            }

            if (!IsWeekendTradingAllowed(CurrentBar - 1))
            {
                RecordBarDecision(bar, null, null, 0, false, "", false, "weekend_disabled");
                return;
            }

            if (!CheckAccountLimits(bar))
            {
                RecordBarDecision(bar, null, null, 0, false, "", false, GetAccountStopOutcome());
                return;
            }

            if (!IsTradingTimeAllowed(CurrentBar - 1))
            {
                RecordBarDecision(bar, null, null, 0, false, "", false, "time_limit");
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

            var isLong =
                currentBullish &&
                previousBearish &&
                engulf &&
                IsLongMacdSignal(bar);

            var isShort =
                currentBearish &&
                previousBullish &&
                engulf &&
                IsShortMacdSignal(bar);

            if (isLong)
            {
                RecordBarDecision(bar, current, previous, diff, engulf, "LONG", true, "accepted");
                ProcessSignal(OrderDirections.Buy, "LONG", bar, diff);
                return;
            }

            if (isShort)
            {
                RecordBarDecision(bar, current, previous, diff, engulf, "SHORT", true, "accepted");
                ProcessSignal(OrderDirections.Sell, "SHORT", bar, diff);
                return;
            }

            var rejectReason = GetRejectReason(bar, currentBullish, currentBearish, previousBullish, previousBearish, engulf);

            RecordBarDecision(bar, current, previous, diff, engulf, "", false, rejectReason);

            RaiseDebug(
                $"Сигнала нет. Bar={bar}; CurrentBull={currentBullish}; CurrentBear={currentBearish}; PreviousBull={previousBullish}; PreviousBear={previousBearish}; Engulf={engulf}; Diff={diff}");
        }

        private void ProcessSignal(OrderDirections direction, string signalName, int bar, decimal diff)
        {
            var signalCandle = GetCandle(bar);

            _entryDirection = direction;
            _entryBasePrice = signalCandle.Close;
            CalculateProtectionPrices(_entryBasePrice, direction, Volume);

            var stopDistance = Math.Abs(_entryBasePrice - _stopLossPrice);
            _activeVolume = CalculateOrderVolume(stopDistance);

            CalculateProtectionPrices(_entryBasePrice, direction, _activeVolume);

            var message =
                $"{GetInstanceLabel()}: {signalName} signal. " +
                $"Bar={bar}; " +
                $"Close={signalCandle.Close}; " +
                $"Difference={diff}; " +
                $"MACDFilter={MacdMode}; " +
                $"Volume={_activeVolume}; " +
                $"TP={_takeProfitPrice}; " +
                $"SL={_stopLossPrice}; " +
                $"TPPoints={TakeProfitPoints}; " +
                $"SLPoints={StopLossPoints}; " +
                $"ProtectionMode={ProtectionCalculationMode}; " +
                $"SeriesStep={_lossSeriesStep}";

            RaiseShowNotification(message);
            RecordSignal(bar, signalName, signalCandle.Close, diff, _activeVolume,
                $"signal; macd_filter={MacdMode}; previous_difference={_difference[bar - 1]}");

            if (!TradingEnabled)
            {
                RaiseDebug("TradingEnabled выключен. Сигнал найден, заявка не отправлена.");
                return;
            }

            if (!ValidateTradingContext())
                return;

            SendEntryOrder(direction);
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

                RaiseShowNotification(
                    $"{GetInstanceLabel()}: отправлена входная заявка {direction}; Volume={_activeVolume}");
            }
            catch (Exception ex)
            {
                RecordTradeEvent("entry_order_error", _lastProcessedSignalBar, _entryDirection.ToString(), false, ex.Message, _activeVolume);
                ResetTradeState();
                RaiseShowNotification(
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

            RaiseShowNotification(
                $"{GetInstanceLabel()}: вход заблокирован до отправки заявки. {errors} Проверь выбранный инструмент, портфель и Replay Account.");

            ResetTradeState();
            return false;
        }

        private void HandleEntryFilled()
        {
            if (_protectivePlacementStarted || _emergencyClosing)
                return;

            var activePosition = GetActivePosition();

            _tradeState = TradeState.PositionOpen;
            _lastKnownPosition = activePosition;
            RecordTradeEvent("entry_filled", _lastProcessedSignalBar, _entryDirection.ToString(), true, "position_open", Math.Abs(activePosition));

            RaiseShowNotification(
                $"{GetInstanceLabel()}: вход исполнен. Position={activePosition}; TP={_takeProfitPrice}; SL={_stopLossPrice}");

            PlaceProtectiveOrders();
        }

        private void PlaceProtectiveOrders()
        {
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

            RaiseShowNotification(
                $"{GetInstanceLabel()}: защитные заявки выставлены. TP={_takeProfitPrice}; SL={_stopLossPrice}");
        }

        private void FailProtectivePlacement()
        {
            if (_emergencyClosing)
                return;

            CancelProtectiveOrders();
            RaiseShowNotification($"{GetInstanceLabel()}: защитные заявки не подтверждены. Закрываю позицию аварийно.");
            EmergencyFlattenPosition("protective_order_not_sent");
        }

        private void RestoreProtectionForExistingPosition()
        {
            var bar = Math.Max(0, CurrentBar - 2);
            var candle = GetCandle(bar);

            _entrySent = true;
            _entrySentBar = bar;
            _activeVolume = Math.Abs(GetActivePosition());
            _entryBasePrice = candle.Close;
            _entryDirection = GetActivePosition() > 0
                ? OrderDirections.Buy
                : OrderDirections.Sell;

            CalculateProtectionPrices(candle.Close, _entryDirection, _activeVolume);

            RaiseShowNotification(
                $"{GetInstanceLabel()}: восстановление сопровождения открытой позиции. BasePrice={candle.Close}; TP={_takeProfitPrice}; SL={_stopLossPrice}");

            PlaceProtectiveOrders();
        }

        private void RecoverAfterConnectionGap(int signalBar)
        {
            if (!AutoRecoveryEnabled)
                return;

            if (GetActivePosition() != 0 && _entrySent && !_protectiveOrdersSent)
            {
                if (_lastProtectiveRetryBar >= 0 &&
                    signalBar - _lastProtectiveRetryBar < ProtectiveRetryBars)
                    return;

                RaiseShowNotification(
                    $"{GetInstanceLabel()}: восстановление после разрыва. Позиция есть, защитные заявки проверяются заново.");

                PlaceProtectiveOrders();
                return;
            }

            if (GetActivePosition() == 0 && _tradeState == TradeState.ProtectiveOrdersActive)
            {
                RaiseShowNotification(
                    $"{GetInstanceLabel()}: позиция закрыта после восстановления связи. Состояние сброшено.");

                HandleTradeClosed("unknown");
                return;
            }

            if (!_entrySent || _protectiveOrdersSent || GetActivePosition() != 0 || _entrySentBar < 0)
                return;

            if (signalBar - _entrySentBar < EntryRecoveryBars)
                return;

            RecordTradeEvent("entry_order_timeout", signalBar, _entryDirection.ToString(), false,
                $"position_not_opened; order_id={_entryOrder?.Id}; order_state={_entryOrder?.State}; portfolio={Portfolio}; money_source={DailyMoneyPnlSource}",
                _activeVolume);
            ResetTradeState();

            RaiseShowNotification(
                $"{GetInstanceLabel()}: восстановление после разрыва или отклонённой заявки. Позиции нет, состояние сброшено, новые сигналы разрешены.");
        }

        private void HandleTradeClosed(string outcome)
        {
            if (_closingTrade)
                return;

            _closingTrade = true;
            CancelProtectiveOrders();
            _entriesBlockedUntilBar = Math.Max(_entriesBlockedUntilBar, _lastProcessedSignalBar + Math.Max(PostCloseCooldownBars, 1));

            if (outcome == "TP")
                _lossSeriesStep = 0;
            else if (outcome == "SL")
                _lossSeriesStep = Math.Min(Math.Max(MaxSeriesStep, 1), _lossSeriesStep + 1);

            RecordTradeClosed(outcome);
            _lastKnownPosition = GetActivePosition();

            RaiseShowNotification(
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

            if (outcome == "TP")
                _lossSeriesStep = 0;
            else if (outcome == "SL")
                _lossSeriesStep = Math.Min(Math.Max(MaxSeriesStep, 1), _lossSeriesStep + 1);

            RecordTradeEvent(
                "protective_residual_detected",
                _lastProcessedSignalBar,
                myTrade.OrderDirection.ToString(),
                false,
                $"previous={previousPosition}; residual={activePosition}; outcome={outcome}; last_pnl_points={_lastTradePnlPoints}; total_pnl_points={_totalPnlPoints}",
                Math.Abs(activePosition));

            RecordTradeClosed($"{outcome}_residual");

            RaiseShowNotification(
                $"{GetInstanceLabel()}: защитная заявка оставила лишнюю позицию. Previous={previousPosition}; Extra={activePosition}. Закрываю остаток аварийно.");

            _lastKnownPosition = activePosition;
            EmergencyFlattenPosition("protective_residual");
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

            if (RiskSizingEnabled && DepositReferenceValue > 0 && RiskPerTradeDepositPercent > 0 && PointValue > 0 && stopDistance > 0)
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

                RaiseShowNotification(reason == "account_profit_target" || reason == "account_loss_limit"
                    ? $"{GetInstanceLabel()}: дневной денежный лимит счёта достигнут. Отправлено досрочное закрытие {closeDirection}; Volume={Math.Abs(activePosition)}."
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

                RaiseShowNotification(
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
                RaiseShowNotification(
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
                RaiseShowNotification($"{GetInstanceLabel()}: отмена защитной заявки не подтверждена ATAS. Новые входы заблокированы; проверьте заявки вручную.");
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

            if (previousPosition != 0 && activePosition == 0 && _entrySent)
                HandleTradeClosed(reason);
        }

        private void ResetTradeState()
        {
            _entryOrder = null;
            _emergencyOrder = null;
            _takeProfitOrder = null;
            _stopLossOrder = null;

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
        }

        private void RaiseDebug(string message)
        {
            if (!DebugMode)
                return;

            RaiseShowNotification($"{GetInstanceLabel()}: DEBUG: {message}");
        }

        private string GetRejectReason(
            int bar,
            bool currentBullish,
            bool currentBearish,
            bool previousBullish,
            bool previousBearish,
            bool engulf)
        {
            if (!engulf)
                return "no_body_engulf";

            var diff = _difference[bar];

            if (MacdMode == MacdFilterMode.Difference && diff == 0)
                return "macd_zero";

            if (currentBullish && previousBearish && !IsLongMacdSignal(bar))
                return MacdMode == MacdFilterMode.LineCross
                    ? $"long_macd_cross_filter; previous_difference={_difference[bar - 1]}; difference={diff}"
                    : "long_macd_filter";

            if (currentBearish && previousBullish && !IsShortMacdSignal(bar))
                return MacdMode == MacdFilterMode.LineCross
                    ? $"short_macd_cross_filter; previous_difference={_difference[bar - 1]}; difference={diff}"
                    : "short_macd_filter";

            if (!currentBullish && !currentBearish)
                return "doji_current";

            if (!previousBullish && !previousBearish)
                return "doji_previous";

            return "bar_direction_filter";
        }

        private bool IsLongMacdSignal(int bar)
        {
            return MacdMode == MacdFilterMode.LineCross
                ? bar > 0 && _difference[bar - 1] <= 0 && _difference[bar] > 0
                : _difference[bar] > 0;
        }

        private bool IsShortMacdSignal(int bar)
        {
            return MacdMode == MacdFilterMode.LineCross
                ? bar > 0 && _difference[bar - 1] >= 0 && _difference[bar] < 0
                : _difference[bar] < 0;
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
                        Csv(""),
                        Csv(CurrentBar > 0 ? GetTerminalTime(CurrentBar - 1).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) : ""),
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
                        Csv(_moneyEstimatePosition)));
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
            return "time,instance,instrument,event,bar,state,open,high,low,close,body,range,macd_difference,body_engulf,signal,direction,accepted,reason,volume,tp,sl,tp_points,sl_points,protection_mode,series_step,range_size,commission_per_contract,commission_percent,portfolio,connector,market_replay_mode,trading_enabled,current_position,entry_price,unrealized_pnl_points,last_trade_pnl_points,total_pnl_points,delta,delta_volume,cvd,imbalance,chart_time,account_currency,atas_closed_pnl,atas_open_pnl,daily_chart_date,atas_daily_equity_baseline,atas_daily_pnl,strategy_daily_closed_estimate,strategy_daily_commission_estimate,strategy_daily_net_estimate,point_value,money_pnl_source,profit_limit_enabled,profit_limit_unit,profit_limit_value,loss_limit_enabled,loss_limit_unit,loss_limit_value,blocked_chart_date,entry_order_id,entry_order_state,estimate_position";
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
            var diagnosticName = Path.GetFileNameWithoutExtension(fileName) + "-money-diagnostics" +
                (string.IsNullOrEmpty(Path.GetExtension(fileName)) ? ".csv" : Path.GetExtension(fileName));
            return Path.Combine(directory, diagnosticName);
        }

        private string Csv(object value)
        {
            var text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
            text = text.Replace("\"", "\"\"");
            return $"\"{text}\"";
        }

        private bool IsWeekendTradingAllowed(int bar)
        {
            var day = GetTerminalTime(bar).DayOfWeek;
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

            var chartDate = GetTerminalTime(CurrentBar - 1).Date;
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
                    RaiseShowNotification($"{GetInstanceLabel()}: {invalidReason}. Новые входы заблокированы.");
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
            RaiseShowNotification(notification);

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
            if (!TradingTimeLimitEnabled)
                return true;

            var terminalTime = GetTerminalTime(bar);
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

                RaiseShowNotification(
                    $"{GetInstanceLabel()}: включена пауза торговли. TerminalTime={terminalTime:HH:mm}; Pause={GetPausePeriodText()}. Новые входы запрещены.");
            }

            return false;
        }

        private DateTime GetTerminalTime(int bar)
        {
            try
            {
                dynamic candle = GetCandle(bar);
                return candle.Time;
            }
            catch
            {
                return DateTime.Now;
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
                $"MACDFilter={MacdMode}; " +
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
