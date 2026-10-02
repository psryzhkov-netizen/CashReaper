using System;
using System.Globalization;
using System.IO;
using ATAS.DataFeedsCore;
using ATAS.Strategies.Chart;

namespace CashReaper
{
    public class CashReaperStrategy : ChartStrategy
    {
        private enum TradeState
        {
            Idle,
            EntryOrderSent,
            PositionOpen,
            ProtectiveOrdersActive
        }

        public bool TradingEnabled { get; set; } = false;
        public bool DebugMode { get; set; } = false;
        public bool AutoRecoveryEnabled { get; set; } = true;
        public bool SkipOldSignalOnStart { get; set; } = true;
        public bool RestoreProtectiveOrdersOnStart { get; set; } = true;
        public bool TradingTimeLimitEnabled { get; set; } = false;
        public bool StatisticsCollectorEnabled { get; set; } = false;
        public bool RiskSizingEnabled { get; set; } = false;
        public bool SeriesSizingEnabled { get; set; } = false;

        public int EntryRecoveryBars { get; set; } = 3;
        public int ProtectiveRetryBars { get; set; } = 1;
        public int TradingStopHour { get; set; } = 23;
        public int TradingStopMinute { get; set; } = 59;
        public int RangeSize { get; set; } = 5;
        public int ProtectionCalculationMode { get; set; } = 0;
        public int SeriesSizingMode { get; set; } = 0;
        public int MaxSeriesStep { get; set; } = 4;

        public int FastPeriod { get; set; } = 12;
        public int SlowPeriod { get; set; } = 26;
        public int SignalPeriod { get; set; } = 9;

        public decimal Volume { get; set; } = 0.001m;
        public decimal MinVolume { get; set; } = 0.001m;
        public decimal MaxVolume { get; set; } = 0m;
        public decimal VolumeStep { get; set; } = 0.001m;
        public decimal TakeProfitPoints { get; set; } = 400m;
        public decimal StopLossPoints { get; set; } = 200m;
        public decimal TakeProfitPricePercent { get; set; } = 0.5m;
        public decimal StopLossPricePercent { get; set; } = 0.25m;
        public decimal TakeProfitDepositPercent { get; set; } = 1m;
        public decimal StopLossDepositPercent { get; set; } = 0.5m;
        public decimal DepositReferenceValue { get; set; } = 0m;
        public decimal RiskPerTradeDepositPercent { get; set; } = 1m;
        public decimal PointValue { get; set; } = 1m;
        public decimal CommissionPerContract { get; set; } = 0m;
        public decimal CommissionPercent { get; set; } = 0m;

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

        private Order _entryOrder;
        private Order _takeProfitOrder;
        private Order _stopLossOrder;

        private OrderDirections _entryDirection;
        private TradeState _tradeState = TradeState.Idle;

        private bool _entrySent;
        private bool _protectiveOrdersSent;
        private bool _tradingStoppedByTime;

        private decimal _takeProfitPrice;
        private decimal _stopLossPrice;
        private decimal _activeVolume;
        private decimal _entryBasePrice;
        private int _lossSeriesStep;

        public CashReaperStrategy() : base(true)
        {
            Name = "CashReaper";
        }

        protected override void OnStarted()
        {
            if (SkipOldSignalOnStart)
                _lastProcessedSignalBar = Math.Max(_lastProcessedSignalBar, CurrentBar - 2);

            if (CurrentPosition == 0)
            {
                ResetTradeState();
            }
            else
            {
                _entrySent = true;
                _entryDirection = CurrentPosition > 0
                    ? OrderDirections.Buy
                    : OrderDirections.Sell;
                _tradeState = TradeState.PositionOpen;

                RaiseShowNotification(
                    $"{GetInstanceLabel()}: запущен. Есть открытая позиция: {CurrentPosition}. Новые входы заблокированы до закрытия позиции.");

                if (RestoreProtectiveOrdersOnStart)
                    RestoreProtectionForExistingPosition();
            }

            RaiseShowNotification(
                TradingEnabled
                    ? $"{GetInstanceLabel()}: запущен. Торговля ВКЛЮЧЕНА. Volume={Volume}; TP={TakeProfitPoints}; SL={StopLossPoints}; Recovery={AutoRecoveryEnabled}; Debug={DebugMode}; TimeLimit={TradingTimeLimitEnabled}; StopTime={GetStopTimeText()}; Collector={StatisticsCollectorEnabled}"
                    : $"{GetInstanceLabel()}: запущен. Торговля выключена. Volume={Volume}; TP={TakeProfitPoints}; SL={StopLossPoints}; Recovery={AutoRecoveryEnabled}; Debug={DebugMode}; TimeLimit={TradingTimeLimitEnabled}; StopTime={GetStopTimeText()}; Collector={StatisticsCollectorEnabled}");
        }

        protected override void OnStopping()
        {
            CancelProtectiveOrders();

            if (CurrentPosition == 0)
            {
                ResetTradeState();
                RaiseShowNotification($"{GetInstanceLabel()}: остановлен. Позиции нет, состояние сброшено.");
            }
            else
            {
                RaiseShowNotification(
                    $"{GetInstanceLabel()}: остановлен. Внимание: позиция всё ещё открыта: {CurrentPosition}. Защитные заявки стратегии сняты.");
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

            var signalBar = bar - 1;

            if (signalBar <= _lastProcessedSignalBar)
                return;

            _lastProcessedSignalBar = signalBar;

            RecoverAfterConnectionGap(signalBar);
            CheckSignal(signalBar);
        }

        protected override void OnOrderChanged(Order order)
        {
            if (order == null)
                return;

            if (_entryOrder != null && order.Id == _entryOrder.Id)
            {
                if (CurrentPosition != 0 && !_protectiveOrdersSent)
                    HandleEntryFilled();

                return;
            }

            if (_takeProfitOrder != null && order.Id == _takeProfitOrder.Id && CurrentPosition == 0)
            {
                HandleTradeClosed("TP");
                return;
            }

            if (_stopLossOrder != null && order.Id == _stopLossOrder.Id && CurrentPosition == 0)
            {
                HandleTradeClosed("SL");
                return;
            }

            if (CurrentPosition != 0 && _entrySent && !_protectiveOrdersSent)
            {
                RaiseDebug("Позиция есть, но защитные заявки ещё не активны. Проверяю защиту.");
                PlaceProtectiveOrders();
                return;
            }

            if (CurrentPosition == 0 && _entrySent && _protectiveOrdersSent)
            {
                HandleTradeClosed("unknown");
            }
        }

        private void CheckSignal(int bar)
        {
            if (_tradeState != TradeState.Idle || _entrySent || CurrentPosition != 0)
            {
                RaiseDebug(
                    $"Вход заблокирован. Bar={bar}; State={_tradeState}; EntrySent={_entrySent}; Position={CurrentPosition}");
                return;
            }

            if (!IsTradingTimeAllowed(bar))
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
                diff > 0;

            var isShort =
                currentBearish &&
                previousBullish &&
                engulf &&
                diff < 0;

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

            var rejectReason = GetRejectReason(currentBullish, currentBearish, previousBullish, previousBearish, engulf, diff);

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
                $"Volume={_activeVolume}; " +
                $"TP={_takeProfitPrice}; " +
                $"SL={_stopLossPrice}; " +
                $"TPPoints={TakeProfitPoints}; " +
                $"SLPoints={StopLossPoints}; " +
                $"ProtectionMode={ProtectionCalculationMode}; " +
                $"SeriesStep={_lossSeriesStep}";

            RaiseShowNotification(message);
            RecordSignal(bar, signalName, signalCandle.Close, diff, _activeVolume, "signal");

            if (!TradingEnabled)
            {
                RaiseDebug("TradingEnabled выключен. Сигнал найден, заявка не отправлена.");
                return;
            }

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

                RaiseShowNotification(
                    $"{GetInstanceLabel()}: отправлена входная заявка {direction}; Volume={_activeVolume}");
            }
            catch (Exception ex)
            {
                ResetTradeState();
                RaiseShowNotification(
                    $"{GetInstanceLabel()}: входная заявка не отправлена. Состояние сброшено. Ошибка: {ex.Message}");
            }
        }

        private void HandleEntryFilled()
        {
            _tradeState = TradeState.PositionOpen;

            RaiseShowNotification(
                $"{GetInstanceLabel()}: вход исполнен. Position={CurrentPosition}; TP={_takeProfitPrice}; SL={_stopLossPrice}");

            PlaceProtectiveOrders();
        }

        private void PlaceProtectiveOrders()
        {
            if (CurrentPosition == 0)
                return;

            var exitDirection = GetExitDirectionForCurrentPosition();
            var quantity = Math.Abs(CurrentPosition);

            _takeProfitOrder = new Order
            {
                Portfolio = Portfolio,
                Security = Security,
                Direction = exitDirection,
                Type = OrderTypes.Limit,
                Price = _takeProfitPrice,
                QuantityToFill = quantity
            };

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

            _lastProtectiveRetryBar = _lastProcessedSignalBar;

            var takeProfitSent = TryOpenOrder(_takeProfitOrder, "take-profit");
            var stopLossSent = TryOpenOrder(_stopLossOrder, "stop-loss");

            _protectiveOrdersSent = takeProfitSent && stopLossSent;
            _tradeState = _protectiveOrdersSent
                ? TradeState.ProtectiveOrdersActive
                : TradeState.PositionOpen;

            if (!_protectiveOrdersSent)
            {
                RaiseShowNotification(
                    $"{GetInstanceLabel()}: позиция есть, но защитные заявки выставились не полностью. Буду пробовать повторно.");
                return;
            }

            RaiseShowNotification(
                $"{GetInstanceLabel()}: защитные заявки выставлены. TP={_takeProfitPrice}; SL={_stopLossPrice}");
        }

        private void RestoreProtectionForExistingPosition()
        {
            var bar = Math.Max(0, CurrentBar - 2);
            var candle = GetCandle(bar);

            _entrySent = true;
            _entrySentBar = bar;
            _activeVolume = Math.Abs(CurrentPosition);
            _entryBasePrice = candle.Close;
            _entryDirection = CurrentPosition > 0
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

            if (CurrentPosition != 0 && _entrySent && !_protectiveOrdersSent)
            {
                if (_lastProtectiveRetryBar >= 0 &&
                    signalBar - _lastProtectiveRetryBar < ProtectiveRetryBars)
                    return;

                RaiseShowNotification(
                    $"{GetInstanceLabel()}: восстановление после разрыва. Позиция есть, защитные заявки проверяются заново.");

                PlaceProtectiveOrders();
                return;
            }

            if (CurrentPosition == 0 && _tradeState == TradeState.ProtectiveOrdersActive)
            {
                RaiseShowNotification(
                    $"{GetInstanceLabel()}: позиция закрыта после восстановления связи. Состояние сброшено.");

                HandleTradeClosed("unknown");
                return;
            }

            if (!_entrySent || _protectiveOrdersSent || CurrentPosition != 0 || _entrySentBar < 0)
                return;

            if (signalBar - _entrySentBar < EntryRecoveryBars)
                return;

            ResetTradeState();

            RaiseShowNotification(
                $"{GetInstanceLabel()}: восстановление после разрыва или отклонённой заявки. Позиции нет, состояние сброшено, новые сигналы разрешены.");
        }

        private void HandleTradeClosed(string outcome)
        {
            CancelProtectiveOrders();

            if (outcome == "TP")
                _lossSeriesStep = 0;
            else if (outcome == "SL")
                _lossSeriesStep = Math.Min(Math.Max(MaxSeriesStep, 1), _lossSeriesStep + 1);

            RecordTradeClosed(outcome);

            RaiseShowNotification(
                $"{GetInstanceLabel()}: позиция закрыта. Outcome={outcome}; NextSeriesStep={_lossSeriesStep}");

            ResetTradeState();
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
            if (ProtectionCalculationMode == 1)
                return Math.Abs(basePrice) * TakeProfitPricePercent / 100m;

            if (ProtectionCalculationMode == 2 && DepositReferenceValue > 0 && PointValue > 0 && orderVolume > 0)
                return DepositReferenceValue * TakeProfitDepositPercent / 100m / (orderVolume * PointValue);

            return TakeProfitPoints;
        }

        private decimal CalculateStopLossDistance(decimal basePrice, decimal orderVolume)
        {
            if (ProtectionCalculationMode == 1)
                return Math.Abs(basePrice) * StopLossPricePercent / 100m;

            if (ProtectionCalculationMode == 2 && DepositReferenceValue > 0 && PointValue > 0 && orderVolume > 0)
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

            if (SeriesSizingMode == 2)
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
            return CurrentPosition > 0
                ? OrderDirections.Sell
                : OrderDirections.Buy;
        }

        private bool TryOpenOrder(Order order, string orderName)
        {
            try
            {
                OpenOrder(order);
                return true;
            }
            catch (Exception ex)
            {
                RaiseShowNotification(
                    $"{GetInstanceLabel()}: не удалось отправить {orderName}. Ошибка: {ex.Message}");

                return false;
            }
        }

        private void CancelProtectiveOrders()
        {
            TryCancelOrder(_takeProfitOrder);
            TryCancelOrder(_stopLossOrder);

            _takeProfitOrder = null;
            _stopLossOrder = null;
            _protectiveOrdersSent = false;
        }

        private void TryCancelOrder(Order order)
        {
            if (order == null)
                return;

            try
            {
                CancelOrder(order);
            }
            catch
            {
            }
        }

        private void ResetTradeState()
        {
            _entryOrder = null;
            _takeProfitOrder = null;
            _stopLossOrder = null;

            _entryDirection = default;
            _tradeState = TradeState.Idle;
            _entrySent = false;
            _protectiveOrdersSent = false;
            _tradingStoppedByTime = false;
            _entrySentBar = -1;
            _lastProtectiveRetryBar = -1;

            _takeProfitPrice = 0;
            _stopLossPrice = 0;
        }

        private void RaiseDebug(string message)
        {
            if (!DebugMode)
                return;

            RaiseShowNotification($"{GetInstanceLabel()}: DEBUG: {message}");
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

            if (diff == 0)
                return "macd_zero";

            if (currentBullish && previousBearish && diff <= 0)
                return "long_macd_filter";

            if (currentBearish && previousBullish && diff >= 0)
                return "short_macd_filter";

            if (!currentBullish && !currentBearish)
                return "doji_current";

            if (!previousBullish && !previousBearish)
                return "doji_previous";

            return "bar_direction_filter";
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
                        Csv(""),
                        Csv(""),
                        Csv(""),
                        Csv("")));
                }
            }
            catch (Exception ex)
            {
                RaiseDebug($"Statistics Collector error: {ex.Message}");
            }
        }

        private string GetStatisticsHeader()
        {
            return "time,instance,instrument,event,bar,state,open,high,low,close,body,range,macd_difference,body_engulf,signal,direction,accepted,reason,volume,tp,sl,tp_points,sl_points,protection_mode,series_step,range_size,commission_per_contract,commission_percent,delta,delta_volume,cvd,imbalance";
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

            return Path.Combine(directory, fileName);
        }

        private string Csv(object value)
        {
            var text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
            text = text.Replace("\"", "\"\"");
            return $"\"{text}\"";
        }

        private bool IsTradingTimeAllowed(int bar)
        {
            if (!TradingTimeLimitEnabled)
                return true;

            var terminalTime = GetTerminalTime(bar);
            var stopTime = new TimeSpan(
                Clamp(TradingStopHour, 0, 23),
                Clamp(TradingStopMinute, 0, 59),
                0);

            if (terminalTime.TimeOfDay < stopTime)
            {
                _tradingStoppedByTime = false;
                return true;
            }

            if (!_tradingStoppedByTime || _lastTimeLimitNoticeDate.Date != terminalTime.Date)
            {
                _tradingStoppedByTime = true;
                _lastTimeLimitNoticeDate = terminalTime.Date;

                RaiseShowNotification(
                    $"{GetInstanceLabel()}: лимит времени торговли достигнут. TerminalTime={terminalTime:HH:mm}; StopTime={GetStopTimeText()}. Новые входы запрещены.");
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

        private string GetStopTimeText()
        {
            return $"{Clamp(TradingStopHour, 0, 23):00}:{Clamp(TradingStopMinute, 0, 59):00}";
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
