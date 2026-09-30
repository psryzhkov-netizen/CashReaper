using System;
using ATAS.DataFeedsCore;
using ATAS.Strategies.Chart;

namespace CashReaper
{
    public class CashReaperStrategy : ChartStrategy
    {
        public bool TradingEnabled { get; set; } = false;

        public int FastPeriod { get; set; } = 12;
        public int SlowPeriod { get; set; } = 26;
        public int SignalPeriod { get; set; } = 9;

        public decimal Volume { get; set; } = 0.001m;
        public decimal TakeProfitPoints { get; set; } = 400m;

        private decimal[] _fastEma = Array.Empty<decimal>();
        private decimal[] _slowEma = Array.Empty<decimal>();
        private decimal[] _macd = Array.Empty<decimal>();
        private decimal[] _signal = Array.Empty<decimal>();
        private decimal[] _difference = Array.Empty<decimal>();

        private int _lastProcessedSignalBar = -1;

        private Order _entryOrder;
        private Order _takeProfitOrder;
        private Order _stopLossOrder;

        private OrderDirections _entryDirection;

        private bool _entrySent;
        private bool _protectiveOrdersSent;

        private decimal _takeProfitPrice;
        private decimal _stopLossPrice;

        public CashReaperStrategy() : base(true)
        {
            Name = "CashReaper";
        }

        protected override void OnStarted()
        {
            RaiseShowNotification(
                TradingEnabled
                    ? "CashReaper запущен. Реальная торговля ВКЛЮЧЕНА."
                    : "CashReaper запущен. Торговля выключена, только уведомления.");
        }

        protected override void OnStopping()
        {
            CancelProtectiveOrders();

            if (CurrentPosition != 0)
                RaiseShowNotification("CashReaper остановлен. Внимание: позиция всё ещё открыта.");

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
            CheckSignal(signalBar);
        }

        protected override void OnOrderChanged(Order order)
        {
            if (order == null)
                return;

            if (_entryOrder != null &&
                order.Id == _entryOrder.Id &&
                CurrentPosition != 0 &&
                !_protectiveOrdersSent)
            {
                _protectiveOrdersSent = true;

                RaiseShowNotification(
                    $"CashReaper: вход исполнен. Position={CurrentPosition}; TP={_takeProfitPrice}; SL={_stopLossPrice}");

                PlaceProtectiveOrders();
                return;
            }

            if (CurrentPosition == 0 && _entrySent)
            {
                CancelProtectiveOrders();

                RaiseShowNotification("CashReaper: позиция закрыта.");

                ResetTradeState();
            }
        }

        private void CheckSignal(int bar)
        {
            if (_entrySent || CurrentPosition != 0)
                return;

            var previous = GetCandle(bar - 1);
            var current = GetCandle(bar);

            var diff = _difference[bar];

            var isLong =
                IsBullish(current) &&
                IsBearish(previous) &&
                BodyEngulfs(current, previous) &&
                diff > 0;

            var isShort =
                IsBearish(current) &&
                IsBullish(previous) &&
                BodyEngulfs(current, previous) &&
                diff < 0;

            if (isLong)
                ProcessSignal(OrderDirections.Buy, "LONG", bar, diff);

            if (isShort)
                ProcessSignal(OrderDirections.Sell, "SHORT", bar, diff);
        }

        private void ProcessSignal(OrderDirections direction, string signalName, int bar, decimal diff)
        {
            var signalCandle = GetCandle(bar);

            _entryDirection = direction;

            if (direction == OrderDirections.Buy)
            {
                _stopLossPrice = signalCandle.Low;
                _takeProfitPrice = signalCandle.Close + TakeProfitPoints;
            }
            else
            {
                _stopLossPrice = signalCandle.High;
                _takeProfitPrice = signalCandle.Close - TakeProfitPoints;
            }

            var message =
                $"CashReaper {signalName} signal. " +
                $"Bar={bar}; " +
                $"Close={signalCandle.Close}; " +
                $"Difference={diff}; " +
                $"Volume={Volume}; " +
                $"TP={_takeProfitPrice}; " +
                $"SL={_stopLossPrice}";

            RaiseShowNotification(message);

            if (!TradingEnabled)
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
                QuantityToFill = Volume
            };

            _entrySent = true;

            OpenOrder(_entryOrder);

            RaiseShowNotification(
                $"CashReaper: отправлена входная заявка {direction}; Volume={Volume}");
        }

        private void PlaceProtectiveOrders()
        {
            if (CurrentPosition == 0)
                return;

            var exitDirection =
                _entryDirection == OrderDirections.Buy
                    ? OrderDirections.Sell
                    : OrderDirections.Buy;

            _takeProfitOrder = new Order
            {
                Portfolio = Portfolio,
                Security = Security,
                Direction = exitDirection,
                Type = OrderTypes.Limit,
                Price = _takeProfitPrice,
                QuantityToFill = Math.Abs(CurrentPosition)
            };

            _stopLossOrder = new Order
            {
                Portfolio = Portfolio,
                Security = Security,
                Direction = exitDirection,
                Type = OrderTypes.Stop,
                TriggerPrice = _stopLossPrice,
                Price = _stopLossPrice,
                QuantityToFill = Math.Abs(CurrentPosition)
            };

            OpenOrder(_takeProfitOrder);
            OpenOrder(_stopLossOrder);

            RaiseShowNotification(
                $"CashReaper: защитные заявки выставлены. TP={_takeProfitPrice}; SL={_stopLossPrice}");
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
            _entrySent = false;
            _protectiveOrdersSent = false;

            _takeProfitPrice = 0;
            _stopLossPrice = 0;
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
