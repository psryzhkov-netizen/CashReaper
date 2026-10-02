using System;
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

        public int EntryRecoveryBars { get; set; } = 3;
        public int ProtectiveRetryBars { get; set; } = 1;
        public int TradingStopHour { get; set; } = 23;
        public int TradingStopMinute { get; set; } = 59;

        public int FastPeriod { get; set; } = 12;
        public int SlowPeriod { get; set; } = 26;
        public int SignalPeriod { get; set; } = 9;

        public decimal Volume { get; set; } = 0.001m;
        public decimal TakeProfitPoints { get; set; } = 400m;
        public decimal StopLossPoints { get; set; } = 200m;

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
                    ? $"{GetInstanceLabel()}: запущен. Торговля ВКЛЮЧЕНА. Volume={Volume}; TP={TakeProfitPoints}; SL={StopLossPoints}; Recovery={AutoRecoveryEnabled}; Debug={DebugMode}; TimeLimit={TradingTimeLimitEnabled}; StopTime={GetStopTimeText()}"
                    : $"{GetInstanceLabel()}: запущен. Торговля выключена. Volume={Volume}; TP={TakeProfitPoints}; SL={StopLossPoints}; Recovery={AutoRecoveryEnabled}; Debug={DebugMode}; TimeLimit={TradingTimeLimitEnabled}; StopTime={GetStopTimeText()}");
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

            if (CurrentPosition != 0 && _entrySent && !_protectiveOrdersSent)
            {
                RaiseDebug("Позиция есть, но защитные заявки ещё не активны. Проверяю защиту.");
                PlaceProtectiveOrders();
                return;
            }

            if (CurrentPosition == 0 && _entrySent && _protectiveOrdersSent)
            {
                CancelProtectiveOrders();

                RaiseShowNotification($"{GetInstanceLabel()}: позиция закрыта.");

                ResetTradeState();
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
                return;

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
                ProcessSignal(OrderDirections.Buy, "LONG", bar, diff);
                return;
            }

            if (isShort)
            {
                ProcessSignal(OrderDirections.Sell, "SHORT", bar, diff);
                return;
            }

            RaiseDebug(
                $"Сигнала нет. Bar={bar}; CurrentBull={currentBullish}; CurrentBear={currentBearish}; PreviousBull={previousBullish}; PreviousBear={previousBearish}; Engulf={engulf}; Diff={diff}");
        }

        private void ProcessSignal(OrderDirections direction, string signalName, int bar, decimal diff)
        {
            var signalCandle = GetCandle(bar);

            _entryDirection = direction;
            CalculateProtectionPrices(signalCandle.Close, direction);

            var message =
                $"{GetInstanceLabel()}: {signalName} signal. " +
                $"Bar={bar}; " +
                $"Close={signalCandle.Close}; " +
                $"Difference={diff}; " +
                $"Volume={Volume}; " +
                $"TP={_takeProfitPrice}; " +
                $"SL={_stopLossPrice}; " +
                $"TPPoints={TakeProfitPoints}; " +
                $"SLPoints={StopLossPoints}";

            RaiseShowNotification(message);

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
                QuantityToFill = Volume
            };

            _entrySent = true;
            _entrySentBar = _lastProcessedSignalBar;
            _tradeState = TradeState.EntryOrderSent;

            try
            {
                OpenOrder(_entryOrder);

                RaiseShowNotification(
                    $"{GetInstanceLabel()}: отправлена входная заявка {direction}; Volume={Volume}");
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
            _entryDirection = CurrentPosition > 0
                ? OrderDirections.Buy
                : OrderDirections.Sell;

            CalculateProtectionPrices(candle.Close, _entryDirection);

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
                CancelProtectiveOrders();

                RaiseShowNotification(
                    $"{GetInstanceLabel()}: позиция закрыта после восстановления связи. Состояние сброшено.");

                ResetTradeState();
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

        private void CalculateProtectionPrices(decimal basePrice, OrderDirections direction)
        {
            if (direction == OrderDirections.Buy)
            {
                _stopLossPrice = basePrice - StopLossPoints;
                _takeProfitPrice = basePrice + TakeProfitPoints;
            }
            else
            {
                _stopLossPrice = basePrice + StopLossPoints;
                _takeProfitPrice = basePrice - TakeProfitPoints;
            }
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
