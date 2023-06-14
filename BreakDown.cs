using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using StockSharp.Algo;
using StockSharp.BusinessEntities;
using StockSharp.Logging;
using StockSharp.Messages;
using StockSharp.Quik;

namespace PrismaBoy
{
    /// <summary>
    /// Класс, хранящий информацию об экстремумах утра
    /// </summary>
    public class ExtremeOfMorning
    {
        public decimal High { get; set; }
        public decimal Low { get; set; }
        public DateTime Date { get; set; }

        public ExtremeOfMorning(decimal high, decimal low, DateTime date)
        {
            High = high;
            Low = low;
            Date = date;
        }
    }

    sealed class BreakDown: MyBaseStrategy
    {
        /// <summary>
        /// Тейкпрофит2, %
        /// </summary>
        private readonly decimal _takeProfitPercent2;

        /// <summary>
        /// Тейкпрофит3, %
        /// </summary>
        private readonly decimal _takeProfitPercent3; 

        /// <summary>
        /// Размер пробоя, %
        /// </summary>
        private readonly decimal _breakPercent; 
                                                 
        /// <summary>
        /// Откат от пробоя, %
        /// </summary>
        private readonly decimal _enterPercent;    
                                              
        /// <summary>
        /// Время отсечки
        /// </summary>
        private readonly TimeOfDay _timeOff;                                                    

        /// <summary>
        /// Словарь экстремумов утра
        /// </summary>
        public readonly Dictionary<string, ExtremeOfMorning> ExtremesOfMorningDictionary;

        /// <summary>
        /// Время следующего обновления экстремумов утра
        /// </summary>
        private DateTime _nextTimeToGetExtremesOfMorning;

        /// <summary>
        /// Конструктор класса BreakDown
        /// </summary>
        public BreakDown(List<Security> securityList, Dictionary<string, decimal> securityVolumeDictionary, TimeSpan timeFrame, decimal stopLossPercent, decimal takeProfitPercent, decimal takeProfitPercent2, decimal takeProfitPercent3, decimal breakPercent, decimal enterPercent, TimeOfDay timeOff, bool loadActiveTrades)
            : base(securityList, securityVolumeDictionary, timeFrame, stopLossPercent, takeProfitPercent)
        {            
            Name = "BDown";
            IsIntraDay = false;
            CloseAllPositionsOnStop = false;
            CancelOrdersWhenStopping = false;
            StopType = StopTypes.MarketLimitLight;
            
            // В соответствии с параметрами конструктора
            _breakPercent = breakPercent;
            _enterPercent = enterPercent;
            _timeOff = timeOff;
            _takeProfitPercent2 = takeProfitPercent2;
            _takeProfitPercent3 = takeProfitPercent3;


            // Объявляем и инициализируем пустые переменные
            ExtremesOfMorningDictionary = new Dictionary<string, ExtremeOfMorning>();
            
            switch(DateTime.Today.DayOfWeek)
            {
                case DayOfWeek.Saturday:
                    _nextTimeToGetExtremesOfMorning =
                        DateTime.Today.AddHours(_timeOff.Hours).AddMinutes(_timeOff.Minutes).AddDays(2);
                    break;
                
                case DayOfWeek.Sunday:
                    _nextTimeToGetExtremesOfMorning =
                        DateTime.Today.AddHours(_timeOff.Hours).AddMinutes(_timeOff.Minutes).AddDays(1);
                        break;

                default:
                    _nextTimeToGetExtremesOfMorning = DateTime.Today.AddHours(_timeOff.Hours).AddMinutes(_timeOff.Minutes);
                    break;
            }

            if (loadActiveTrades)
            {
                LoadActiveTrades(Name);
            }
        }

        /// <summary>
        /// Событие старта стратегии
        /// </summary>
        protected override void OnStarted()
        {
            TimeToStopRobot = IsWorkContour
                                              ? new DateTime(DateTime.Now.Year, DateTime.Now.Month, DateTime.Now.Day, 23,
                                                             50, 00)
                                              : new DateTime(DateTime.Now.Year, DateTime.Now.Month, DateTime.Now.Day, 23,
                                                             49, 00);

            // Подписываемся на события прихода времени отсечки
            Security
                .WhenTimeCome(_nextTimeToGetExtremesOfMorning)
                .Do(GetExtremesOfMorning)
                .Once()
                .Apply(this);

            this.AddInfoLog("Стратегия запускается со следующими параметрами:" +
                            "\nТаймфрейм: " + TimeFrame +
                            "\nРазмер пробоя, %: " + _breakPercent +
                            "\nОткат от пробоя, %: " + _enterPercent +
                            "\nВремя отсечки: " + _nextTimeToGetExtremesOfMorning +
                            "\nСтоплосс, %: " + StopLossPercent +
                            "\nТейкпрофит, %: " + TakeProfitPercent);

            base.OnStarted();
        }

        /// <summary>
        /// Метод-обработчик прихода новой свечки
        /// </summary>
        protected override void TimeFrameCome(object sender, MainWindow.TimeFrameEventArgs e)
        {
            base.TimeFrameCome(sender, e);

            #region Фильтр времени 1

            // Если сейчас раньше времени отсечки, то ничего не предпринимаем
            if (e.MarketTime < DateTime.Today.AddHours(_timeOff.Hours).AddMinutes(_timeOff.Minutes))
            {
                return;
            }

            #endregion

            if (ExtremesOfMorningDictionary.All(extreme => extreme.Value.Date != DateTime.Today.Date))
            {
                this.AddInfoLog("Отсутствует диапазон утренних цен сегодняшнего дня! Рассчитываем диапазон.");
                GetExtremesOfMorning();
            }

            foreach (var security in SecurityList)
            {
                // Если есть активные трейды по инструменту
                if (ActiveTrades.Count(trade => trade.Security == security.Code) != 0)
                {
                    // Если сейчас 23-35
                    if (e.MarketTime.AddSeconds(5).Hour == 23 && e.MarketTime.Minute == 35)
                    {
                        this.AddInfoLog("Московское время 23-35. Проверяем активные сделки на ВЫХОД ПО ВРЕМЕНИ");

                        var currentSecurity = security;

                        foreach (var trade in ActiveTrades.Where(trade => trade.Security == currentSecurity.Code))
                        {
                            var enclosedTakeProfitLevel = trade.Direction == Direction.Buy
                                                              ? ((e.LastBarsDictionary[currentSecurity.Code].Close) - (trade.Price * (1 + TakeProfitPercent * 0.8m / 100))) > 0
                                                              : ((e.LastBarsDictionary[currentSecurity.Code].Close - trade.Price * (1 - TakeProfitPercent * 0.8m / 100))) < 0;

                            var enclosedStopLossLevel = trade.Direction == Direction.Buy
                                                              ? ((e.LastBarsDictionary[currentSecurity.Code].Close - trade.Price * (1 - StopLossPercent * 0.5m / 100))) < 0
                                                              : ((e.LastBarsDictionary[currentSecurity.Code].Close - trade.Price * (1 + StopLossPercent * 0.5m / 100))) > 0;

                            if (!enclosedTakeProfitLevel && !enclosedStopLossLevel) continue;

                            var message = enclosedTakeProfitLevel
                                              ? "Выходим с профитом перед закрытием"
                                              : "Выходим с лоссом перед закрытием";
                            this.AddInfoLog(message);

                            ClosePositionByTime(trade);
                        }
                    }
                }

                // Если нет активных трейдов по инструменту
                else
                {
                    #region Фильтр времени 2

                    // Если сейчас позже 19-00, то снимаем все активные заявки на вход и ничего не делаем
                    if (IsWorkContour)
                        if (e.MarketTime.AddSeconds(5) >= DateTime.Today.AddHours(19))
                        {
                            foreach (var order in Orders.Where(order => order.Security == security && order.State == OrderStates.Active && order.Comment.Contains("enter")).Where(order => order != null))
                            {
                                this.AddInfoLog("{0}: Начало вечерней сессии. Снимаем все активные заявки на ВХОД.", security.Code);
                                CancelOrder(order);
                            }

                            return;
                        }

                    #endregion

                    // Если в словаре экстремумов утра есть соответствующие данные по инструменту
                    if (ExtremesOfMorningDictionary.Count(item => item.Key == security.Code) != 0)
                    {
                        // Если инструмент - "Si" и диапазон утренних экстремумов превышает 200 пунктов, то ничего не делаем
                        if (security.Code.StartsWith("Si") && (ExtremesOfMorningDictionary[security.Code].High - ExtremesOfMorningDictionary[security.Code].Low) >= 200)
                        {
                            this.AddInfoLog("Утренний диапазон Si превышает или равен 200 пунктов! Заявки на вход в течения дня не выставляются.");
                            continue;
                        }


                        // Если нет уже выставленных активных заявок на вход
                        if (!Orders.Any(order => order.Security == security && order.State == OrderStates.Active && order.Comment.Contains("enter")))
                        {
                            // Если закрытие свечки выше или ниже соответствующего экстремума на величину пробоя, выставляем лимитку на границе экстремума в направлении пробоя
                            if (e.LastBarsDictionary[security.Code].Close > security.ShrinkPrice(ExtremesOfMorningDictionary[security.Code].High * (1 + _breakPercent / 100)))
                            {
                                this.AddInfoLog("Пробили утренний диапазон вверх. Выставляю заявки на вход.");

                                #region Выставляем заявки на ВХОД на покупку
                                var orderBuy = new Order
                                {
                                    Comment = Name + ", enter",
                                    ExpiryDate = DateTime.Now.AddDays(1),
                                    Portfolio = Portfolio,
                                    Security = security,
                                    Type = OrderTypes.Limit,
                                    Volume = SecurityVolumeDictionary[security.Code],
                                    Direction = Sides.Buy,
                                    Price = security.ShrinkPrice(ExtremesOfMorningDictionary[security.Code].High)
                                };

                                this.AddInfoLog(
                                    "ЗАЯВКА на ВХОД - {0}. Регистрируем заявку на {1} по цене {2} c объемом {3} - стоп на {4}",
                                    security.Code, orderBuy.Direction == Sides.Sell ? "продажу" : "покупку",
                                    orderBuy.Price.ToString(CultureInfo.InvariantCulture),
                                    orderBuy.Volume.ToString(CultureInfo.InvariantCulture),
                                    security.ShrinkPrice(orderBuy.Price * (1 - StopLossPercent / 100)));

                                var orderBuy2 = new Order
                                {
                                    Comment = Name + ", enter2",
                                    ExpiryDate = DateTime.Now.AddDays(1),
                                    Portfolio = Portfolio,
                                    Security = security,
                                    Type = OrderTypes.Limit,
                                    Volume = SecurityVolumeDictionary[security.Code],
                                    Direction = Sides.Buy,
                                    Price = security.ShrinkPrice(ExtremesOfMorningDictionary[security.Code].High)
                                };

                                this.AddInfoLog(
                                    "ЗАЯВКА на ВХОД - {0}. Регистрируем заявку №2 на {1} по цене {2} c объемом {3} - стоп на {4}",
                                    security.Code, orderBuy2.Direction == Sides.Sell ? "продажу" : "покупку",
                                    orderBuy2.Price.ToString(CultureInfo.InvariantCulture),
                                    orderBuy2.Volume.ToString(CultureInfo.InvariantCulture),
                                    security.ShrinkPrice(orderBuy2.Price * (1 - StopLossPercent / 100)));

                                var orderBuy3 = new Order
                                {
                                    Comment = Name + ", enter3",
                                    ExpiryDate = DateTime.Now.AddDays(1),
                                    Portfolio = Portfolio,
                                    Security = security,
                                    Type = OrderTypes.Limit,

                                    Volume = SecurityVolumeDictionary[security.Code],

                                    Direction = Sides.Buy,
                                    Price = security.ShrinkPrice(ExtremesOfMorningDictionary[security.Code].High)
                                };

                                this.AddInfoLog(
                                    "ЗАЯВКА на ВХОД - {0}. Регистрируем заявку №3 на {1} по цене {2} c объемом {3} - стоп на {4}",
                                    security.Code, orderBuy3.Direction == Sides.Sell ? "продажу" : "покупку",

                                    orderBuy3.Price.ToString(CultureInfo.InvariantCulture),
                                    orderBuy3.Volume.ToString(CultureInfo.InvariantCulture),
                                    security.ShrinkPrice(orderBuy3.Price * (1 - StopLossPercent / 100)));

                                RegisterOrder(orderBuy);
                                RegisterOrder(orderBuy2);
                                RegisterOrder(orderBuy3);

                                #endregion

                            }
                            if (e.LastBarsDictionary[security.Code].Close < security.ShrinkPrice(ExtremesOfMorningDictionary[security.Code].Low * (1 - _breakPercent / 100)))
                            {
                                this.AddInfoLog("Пробили утренний диапазон вниз. Выставляю заявки на вход.");

                                #region Выставляем заявки на ВХОД на продажу
                                var orderSell = new Order
                                {

                                    Comment = Name + ", enter",
                                    ExpiryDate = DateTime.Now.AddDays(1),
                                    Portfolio = Portfolio,
                                    Security = security,
                                    Type = OrderTypes.Limit,
                                    Volume = SecurityVolumeDictionary[security.Code],
                                    Direction = Sides.Sell,
                                    Price = security.ShrinkPrice(ExtremesOfMorningDictionary[security.Code].Low)
                                };

                                this.AddInfoLog(
                                    "ЗАЯВКА на ВХОД - {0}. Регистрируем заявку на {1} по цене {2} c объемом {3} - стоп на {4}",
                                    security.Code, orderSell.Direction == Sides.Sell ? "продажу" : "покупку",
                                    orderSell.Price.ToString(CultureInfo.InvariantCulture),
                                    orderSell.Volume.ToString(CultureInfo.InvariantCulture),
                                    security.ShrinkPrice(orderSell.Price * (1 + StopLossPercent / 100)));

                                var orderSell2 = new Order
                                {

                                    Comment = Name + ", enter2",
                                    ExpiryDate = DateTime.Now.AddDays(1),
                                    Portfolio = Portfolio,
                                    Security = security,
                                    Type = OrderTypes.Limit,
                                    Volume = SecurityVolumeDictionary[security.Code],
                                    Direction = Sides.Sell,
                                    Price = security.ShrinkPrice(ExtremesOfMorningDictionary[security.Code].Low)
                                };

                                this.AddInfoLog(
                                    "ЗАЯВКА на ВХОД - {0}. Регистрируем заявку №2 на {1} по цене {2} c объемом {3} - стоп на {4}",
                                    security.Code, orderSell2.Direction == Sides.Sell ? "продажу" : "покупку",
                                    orderSell2.Price.ToString(CultureInfo.InvariantCulture),
                                    orderSell2.Volume.ToString(CultureInfo.InvariantCulture),
                                    security.ShrinkPrice(orderSell2.Price * (1 + StopLossPercent / 100)));

                                var orderSell3 = new Order
                                {

                                    Comment = Name + ", enter3",
                                    ExpiryDate = DateTime.Now.AddDays(1),
                                    Portfolio = Portfolio,
                                    Security = security,
                                    Type = OrderTypes.Limit,
                                    Volume = SecurityVolumeDictionary[security.Code],
                                    Direction = Sides.Sell,
                                    Price = security.ShrinkPrice(ExtremesOfMorningDictionary[security.Code].Low)
                                };

                                this.AddInfoLog(
                                    "ЗАЯВКА на ВХОД - {0}. Регистрируем заявку №3 на {1} по цене {2} c объемом {3} - стоп на {4}",
                                    security.Code, orderSell3.Direction == Sides.Sell ? "продажу" : "покупку",
                                    orderSell3.Price.ToString(CultureInfo.InvariantCulture),
                                    orderSell3.Volume.ToString(CultureInfo.InvariantCulture),
                                    security.ShrinkPrice(orderSell3.Price * (1 + StopLossPercent / 100)));

                                RegisterOrder(orderSell);
                                RegisterOrder(orderSell2);
                                RegisterOrder(orderSell3);
                                #endregion
                            }
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Метод установки профит ордера по активной позиции
        /// </summary>
        protected override void PlaceProfitOrder(ActiveTrade trade)
        {
            var currentSecurity = SecurityList.First(sec => sec.Code == trade.Security);
            if(currentSecurity == null)
                return;

            var profitOrder = new Order
            {
                Comment = Name + ",p," + trade.Id,
                Portfolio = Portfolio,
                ExpiryDate = DateTime.Now.AddDays(5),
                Type = OrderTypes.Limit,
                Volume = trade.Volume,
                Security = currentSecurity,
                Direction =
                    trade.Direction == Direction.Sell
                        ? Sides.Buy
                        : Sides.Sell,
            };

            if (trade.OrderName.EndsWith("enter2"))
            {
                profitOrder.Price = trade.Direction == Direction.Sell
                                    ? currentSecurity.
                                          ShrinkPrice((trade.Price * (1 - _takeProfitPercent2 / 100)))
                                    : currentSecurity.
                                          ShrinkPrice(trade.Price * (1 + _takeProfitPercent2 / 100));
            }
            else if (trade.OrderName.EndsWith("enter3"))
            {
                profitOrder.Price = trade.Direction == Direction.Sell
                                    ? currentSecurity.
                                          ShrinkPrice((trade.Price * (1 - _takeProfitPercent3 / 100)))
                                    : currentSecurity.
                                          ShrinkPrice(trade.Price * (1 + _takeProfitPercent3 / 100));
            }
            else
            {
                {
                    profitOrder.Price = trade.Direction == Direction.Sell
                                        ? currentSecurity.
                                              ShrinkPrice((trade.Price * (1 - TakeProfitPercent / 100)))
                                        : currentSecurity.
                                              ShrinkPrice(trade.Price * (1 + TakeProfitPercent / 100));
                }
            }

            profitOrder
                .WhenRegistered()
                .Once()
                .Do(() =>
                {
                    trade.ProfitOrderTransactionId = profitOrder.TransactionId;
                    trade.ProfitOrderId = profitOrder.Id;

                    // Вызываем событие прихода изменения ActiveTrades
                    OnActiveTradesChanged(new ActiveTradesChangedEventArgs());

                    this.AddInfoLog(
                                    "ТЕЙКПРОФИТ - {0}. Зарегистрирована заявка на {1} на выход по тейк профиту",
                                    trade.Security,
                                    trade.Direction == Direction.Buy ? "Продажу" : "Покупку");
                })
                .Apply(this);

            profitOrder
                .WhenNewTrades()
                .Do(newTrades =>
                {
                    //foreach (var newTrade in newTrades)
                    //{
                    //    foreach (var activeTrade in ActiveTrades.Where(activeTrade => activeTrade.Id == trade.Id))
                    //    {
                    //        activeTrade.Volume -= newTrade.Trade.Volume;

                    //        // Вызываем событие прихода изменения ActiveTrades
                    //        OnActiveTradesChanged(new ActiveTradesChangedEventArgs());

                    //        if (activeTrade.Volume != 0)
                    //        {
                    //            this.AddInfoLog("Новый объем активной сделки с ID {0} - {1}",
                    //                  activeTrade.Id, activeTrade.Volume);
                    //        }
                    //        else
                    //        {
                    //            this.AddInfoLog("Новый объем активной сделки с ID {0} стал равен 0! Удаляем активную сделку и отменяем соответствующие заявки",
                    //                  activeTrade.Id);
                    //        }
                    //    }
                    //}
                })
                .Apply(this);


            profitOrder
                .WhenMatched()
                .Once()
                .Do(() =>
                {
                    // Обновляем список активных трейдов. Точнее, удаляем закрывшийся по профиту трейд.
                    ActiveTrades = ActiveTrades.Where(activeTrade => activeTrade != trade).ToList();

                    // Вызываем событие прихода изменения ActiveTrades
                    OnActiveTradesChanged(new ActiveTradesChangedEventArgs());

                    if (trade.OrderName.EndsWith("enter"))
                    {
                        foreach (var activeTrade in ActiveTrades.Where(aT => aT.OrderName.EndsWith("enter2")))
                        {
                            activeTrade.StopPrice = activeTrade.Price;
                            var tradeToStop = activeTrade;

                            if (MainWindow.Instance.ConnectorType == ConnectorTypes.Quik)
                            {
                                if (Connector.Orders.All(order => !order.Comment.EndsWith(",s," + tradeToStop.Id) || order.State != OrderStates.Active)) continue;

                                var oldStopOrder = Connector.Orders.First(order => order.Comment.EndsWith(",s," + tradeToStop.Id) && order.State == OrderStates.Active);

                                oldStopOrder
                                    .WhenCanceled()
                                    .Once()
                                    .Do(() =>
                                    {
                                        var newStopOrderSecurity = SecurityList.First(sec => sec.Code == tradeToStop.Security);

                                        var newStopOrder = new Order
                                        {
                                            Comment = Name + ",s," + tradeToStop.Id,

                                            Portfolio = Portfolio,
                                            Type = OrderTypes.Conditional,
                                            Volume = tradeToStop.Volume,
                                            Security = newStopOrderSecurity,
                                            Direction =
                                                tradeToStop.Direction == Direction.Sell
                                                    ? Sides.Buy
                                                    : Sides.Sell,
                                            Price =
                                                tradeToStop.Direction == Direction.Sell
                                                    ? newStopOrderSecurity.ShrinkPrice(tradeToStop.StopPrice * (1 + 0.0015m))
                                                    : newStopOrderSecurity.ShrinkPrice(tradeToStop.StopPrice * (1 - 0.0015m)),

                                            Condition = new QuikOrderCondition
                                            {
                                                Type = QuikOrderConditionTypes.StopLimit,
                                                StopPrice = tradeToStop.StopPrice,
                                            },
                                        };

                                        newStopOrder
                                            .WhenRegistered()
                                            .Once()
                                            .Do(() =>
                                            {
                                                this.AddInfoLog(
                                                    "СТОПЛОСС - {0}. Зарегистрирована заявка на {1} на выход по рынку из сделки.",
                                                    newStopOrder.Security,
                                                    newStopOrder.Direction == Sides.Buy ? "Продажу" : "Покупку");

                                                tradeToStop.StopLossOrderTransactionId = newStopOrder.TransactionId;
                                                tradeToStop.StopLossOrderId = newStopOrder.Id;
                                            })

                                            .Apply(this);

                                        newStopOrder
                                            .WhenNewTrades()
                                            .Do(newTrades =>
                                            {
                                                //foreach (var newTrade in newTrades)
                                                //{
                                                //    foreach (var aT in ActiveTrades.Where(aT => aT.Id == tradeToStop.Id))
                                                //    {
                                                //        aT.Volume -= newTrade.Trade.Volume;

                                                //        if (aT.Volume != 0)
                                                //        {
                                                //            this.AddInfoLog("Новый объем активной сделки с ID {0} - {1}",
                                                //                aT.Id, aT.Volume);
                                                //        }
                                                //        else
                                                //        {
                                                //            this.AddInfoLog("Новый объем активной сделки с ID {0} стал равен 0! Удаляем активную сделку и отменяем соответствующие заявки",
                                                //                aT.Id);
                                                //        }
                                                //    }
                                                //}
                                            })
                                            .Apply(this);


                                        newStopOrder
                                            .WhenMatched()
                                            .Once()
                                            .Do(() =>
                                            {
                                                this.AddInfoLog("ВЫХОД по СТОПУ - {0}", tradeToStop.Security);

                                                ActiveTrades =
                                                    ActiveTrades.Where(aT => aT != trade).ToList();

                                                // Вызываем событие прихода изменения ActiveTrades
                                                OnActiveTradesChanged(new ActiveTradesChangedEventArgs());

                                                foreach (
                                                    var order in
                                                        Connector.Orders.Where(
                                                            order =>
                                                            order.Comment.EndsWith(
                                                                tradeToStop.Id.ToString(CultureInfo.CurrentCulture)) &&
                                                            order.State == OrderStates.Active && order != newStopOrder).Where(
                                                                order => order != null))
                                                {
                                                    Connector.CancelOrder(order);
                                                }
                                            })
                                            .Apply(this);

                                        RegisterOrder(newStopOrder);

                                        this.AddInfoLog("Меняем цену СТОП для средней заявки на цену входа - {0}", tradeToStop.StopPrice);
                                    })

                                    .Apply(this);

                                CancelOrder(oldStopOrder);
                            }

                            this.AddInfoLog("Меняем цену СТОП для средней заявки на цену входа - {0}", activeTrade.StopPrice);
                        }
                    }

                    var ordersToCancel = Connector.Orders.Where(
                        order => order != null &&
                        ((order.Comment.EndsWith(trade.Id.ToString(CultureInfo.CurrentCulture)) &&
                          order.State == OrderStates.Active)));

                    //Если нет других активных ордеров связанных с данным активным трейдом, то ничего не делаем
                    if (!ordersToCancel.Any())
                        return;

                    // Иначе удаляем все связанные с данным активным трейдом ордера
                    foreach (var order in ordersToCancel)
                    {
                        Connector.CancelOrder(order);
                    }

                    this.AddInfoLog("ВЫХОД по ПРОФИТУ - {0}", trade.Security);
                })
                .Apply(this);

            // Регистрируем профит ордер
            RegisterOrder(profitOrder);
        }

        /// <summary>
        /// Вычисление экстремумов утра
        /// </summary>
        private void GetExtremesOfMorning()
        {
            this.AddInfoLog("Вычисляем экстремумы утра...");

            foreach (var security in SecurityList)
            {
                var tempSecurity = security;
                var lastFrameTrades =
                    Connector.Trades.Where(trade => trade.Security.Code == tempSecurity.Code).Filter(
                        DateTime.Today.AddHours(10),
                        DateTime.Today.AddHours(_timeOff.Hours).AddMinutes(_timeOff.Minutes)).ToList();

                if (!lastFrameTrades.Any() || lastFrameTrades.Count(trade => trade.Time < DateTime.Today.AddHours(10).AddSeconds(2)) == 0)
                {
                    this.AddInfoLog("Не найдены все утренние сделки!");
                    return;
                }

                if (!ExtremesOfMorningDictionary.ContainsKey(security.Code))
                    ExtremesOfMorningDictionary.Add(security.Code,
                                                    new ExtremeOfMorning(lastFrameTrades.Max(trade => trade.Price),
                                                                         lastFrameTrades.Min(trade => trade.Price), lastFrameTrades.Last().Time.Date));
                else
                {
                    ExtremesOfMorningDictionary[security.Code].High = lastFrameTrades.Max(trade => trade.Price);
                    ExtremesOfMorningDictionary[security.Code].Low = lastFrameTrades.Min(trade => trade.Price);
                    ExtremesOfMorningDictionary[security.Code].Date = lastFrameTrades.Last().Time.Date;
                }


                this.AddInfoLog(
                    "{0}:\nHigh - {1}: {2}, Low - {3}: {4},  -  {5}\n",
                    security.Code, ExtremesOfMorningDictionary[security.Code].High,
                    lastFrameTrades.Find(trade => trade.Price == lastFrameTrades.Max(a => a.Price)).Time,
                    ExtremesOfMorningDictionary[security.Code].Low,
                    lastFrameTrades.Find(trade => trade.Price == lastFrameTrades.Min(a => a.Price)).Time,
                    lastFrameTrades.Last().Time.Date.ToString(CultureInfo.InvariantCulture));
            }

            switch (DateTime.Today.AddDays(1).DayOfWeek)
            {
                case (DayOfWeek.Saturday):
                    _nextTimeToGetExtremesOfMorning = DateTime.Today.AddHours(_timeOff.Hours).AddMinutes(_timeOff.Minutes).AddDays(3);
                    break;

                case (DayOfWeek.Sunday):
                    _nextTimeToGetExtremesOfMorning = DateTime.Today.AddHours(_timeOff.Hours).AddMinutes(_timeOff.Minutes).AddDays(2);
                    break;

                default:
                    _nextTimeToGetExtremesOfMorning = DateTime.Today.AddHours(_timeOff.Hours).AddMinutes(_timeOff.Minutes).AddDays(1);
                    break;
            }


            // Подписываемся на события прихода времени отсечки
            Security
                .WhenTimeCome(_nextTimeToGetExtremesOfMorning)
                .Do(GetExtremesOfMorning)
                .Once()
                .Apply(this);

            this.AddInfoLog("Следующий расчет по плану: {0}", _nextTimeToGetExtremesOfMorning);

            if (!ActiveTrades.Any()) return;

            foreach (var activeTrade in ActiveTrades)
            {
                switch (activeTrade.Direction)
                {
                    case Direction.Buy:
                        if (ExtremesOfMorningDictionary[activeTrade.Security].Low > activeTrade.StopPrice)
                        {
                            activeTrade.StopPrice = ExtremesOfMorningDictionary[activeTrade.Security].Low;

                            this.AddInfoLog("СТОП Меняем уровень стоп-цены по инструменту: " + activeTrade.Security + "\nНовый уровень: " + activeTrade.StopPrice + "\n");
                        }
                        break;
                    case Direction.Sell:
                        if (ExtremesOfMorningDictionary[activeTrade.Security].High < activeTrade.StopPrice)
                        {
                            activeTrade.StopPrice = ExtremesOfMorningDictionary[activeTrade.Security].High;
                            this.AddInfoLog("СТОП Меняем уровень стоп-цены по инструменту: " + activeTrade.Security + "\nНовый уровень: " + activeTrade.StopPrice + "\n");
                        }
                        break;
                }
            }
        }
    }
}
