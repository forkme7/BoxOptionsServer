﻿using BoxOptions.Common;
using BoxOptions.Common.Interfaces;
using BoxOptions.Core;
using BoxOptions.Services.Interfaces;
using BoxOptions.Services.Models;
using Common.Log;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using WampSharp.V2.Realm;


namespace BoxOptions.Services
{
    public class GameManager : IGameManager, IDisposable
    {
        const int NPriceIndex = 15; // Number of columns hardcoded
        const int NTimeIndex = 8;   // Number of rows hardcoded
        const int CoeffMonitorTimerInterval = 1000; // Coeff cache update interval (milliseconds)


        #region Vars
        /// <summary>
        /// Coefficient Calculator Request Semaphore
        /// Mutual Exclusion Process
        /// </summary>
        static System.Threading.SemaphoreSlim coeffCalculatorSemaphoreSlim = new System.Threading.SemaphoreSlim(1, 1);
        /// <summary>
        /// Process AssetQuote Received Semaphore.
        /// Mutual Exclusion Process
        /// </summary>
        static System.Threading.SemaphoreSlim quoteReceivedSemaphoreSlim = new System.Threading.SemaphoreSlim(1, 1);
                
        static int MaxUserBuffer = 128;
        static object CoeffCacheLock = new object();


        string GameManagerId;

        /// <summary>
        /// Ongoing Bets Cache
        /// </summary>
        List<GameBet> betCache;
        /// <summary>
        /// Users Cache
        /// </summary>
        List<UserState> userList;
        
        public event EventHandler<BetEventArgs> BetWin;
        public event EventHandler<BetEventArgs> BetLose;

        /// <summary>
        /// Database Object
        /// </summary>
        private readonly IGameDatabase database;
        /// <summary>
        /// CoefficientCalculator Object
        /// </summary>
        private readonly ICoefficientCalculator calculator;
        /// <summary>
        /// QuoteFeed Object
        /// </summary>
        private readonly IAssetQuoteSubscriber quoteFeed;
        /// <summary>
        /// WAMP Realm Object
        /// </summary>
        private readonly IWampHostedRealm wampRealm;
        private readonly IMicrographCache micrographCache;
        /// <summary>
        /// BoxSize configuration
        /// </summary>
        private readonly IBoxConfigRepository boxConfigRepository;
        /// <summary>
        /// User Log Repository
        /// </summary>
        private readonly ILogRepository logRepository;
        /// <summary>
        /// Application Log Repository
        /// </summary>
        private readonly ILog appLog;
        /// <summary>
        /// Settings
        /// </summary>
        private readonly BoxOptionsSettings settings;
        /// <summary>
        /// Last Prices Cache
        /// </summary>
        private Dictionary<string, PriceCache> assetCache;

        /// <summary>
        /// Box configuration
        /// </summary>
        List<Core.Models.BoxSize> defaultBoxConfig;

        Queue<string> appLogInfoQueue = new Queue<string>();

        System.Threading.Timer CoeffMonitorTimer = null;
        bool isDisposing = false;
        #endregion

        #region Constructor
        public GameManager(BoxOptionsSettings settings, IGameDatabase database, ICoefficientCalculator calculator, 
            IAssetQuoteSubscriber quoteFeed, IWampHostedRealm wampRealm, IMicrographCache micrographCache, IBoxConfigRepository boxConfigRepository, ILogRepository logRepository, ILog appLog)
        {
            this.database = database;
            this.calculator = calculator;
            this.quoteFeed = quoteFeed;
            this.settings = settings;            
            this.logRepository = logRepository;
            this.appLog = appLog;
            this.wampRealm = wampRealm;
            this.boxConfigRepository = boxConfigRepository;
            this.micrographCache = micrographCache;

            if (this.settings != null && this.settings.BoxOptionsApi != null && this.settings.BoxOptionsApi.GameManager != null)
                MaxUserBuffer = this.settings.BoxOptionsApi.GameManager.MaxUserBuffer;

            GameManagerId = Guid.NewGuid().ToString();
            userList = new List<UserState>();
            betCache = new List<GameBet>();
            assetCache = new Dictionary<string, PriceCache>();
                        
            this.quoteFeed.MessageReceived += QuoteFeed_MessageReceived;

            defaultBoxConfig = null;
        }


        #endregion

        #region Methods

        private void InitializeCoefCalc( )
        {
            // Create default coef parameters foreach Asset
            //List<CoeffParameters> coeffPars = new List<CoeffParameters>();
            //foreach (var box in defaultBoxConfig)
            //{
            //    coeffPars.Add(new CoeffParameters()
            //    {
            //        AssetPair = box.AssetPair,
            //        TimeToFirstOption = (int)box.TimeToFirstBox,    // Time to first box in seconds
            //        OptionLen = (int)box.BoxHeight,     // Box Height in seconds
            //        PriceSize = box.BoxWidth,   // Box Width factor (not calculated)
            //        NPriceIndex = NPriceIndex,  // Number of columns hardcoded
            //        NTimeIndex = NTimeIndex     // Number of rows hardcoded
            //    });

            //}
            
            Core.Models.BoxSize[] calculatedParams = CalculatedBoxes(defaultBoxConfig, micrographCache);

            Task t = CoeffCalculatorChangeBatch(GameManagerId, calculatedParams);
            t.Wait();

            LoadCoefficientCache();

            StartCoefficientCacheMonitor();

            Console.WriteLine(calculatedParams.Length);

        }
               
        
        private List<Core.Models.BoxSize> LoadBoxParameters()
        {
            Task<IEnumerable<Core.Models.BoxSize>> t = boxConfigRepository.GetAll();
            t.Wait();

            List<Core.Models.BoxSize> boxConfig = t.Result.ToList();
            List<Core.Models.BoxSize> AssetsToAdd = new List<Core.Models.BoxSize>();

            List<string> AllAssets = settings.BoxOptionsApi.PricesSettingsBoxOptions.PrimaryFeed.AllowedAssets.ToList();
            AllAssets.AddRange(settings.BoxOptionsApi.PricesSettingsBoxOptions.SecondaryFeed.AllowedAssets);

            string[] DistictAssets = AllAssets.Distinct().ToArray();
            // Validate Allowed Assets
            foreach (var item in DistictAssets)
            {

                // If database does not contain allowed asset then add it
                if (!boxConfig.Select(config => config.AssetPair).Contains(item))
                {
                    // Check if it was not added before to avoid duplicates
                    if (!AssetsToAdd.Select(config => config.AssetPair).Contains(item))
                    {
                        // Add default settings
                        AssetsToAdd.Add(new Core.Models.BoxSize()
                        {
                            AssetPair = item,
                            BoxesPerRow = 7,
                            BoxHeight = 7,
                            BoxWidth = 0.00003,
                            TimeToFirstBox = 4
                        });
                    }
                }
            }
            if (AssetsToAdd.Count > 0)
            {
                boxConfigRepository.InsertManyAsync(AssetsToAdd);
                boxConfig.AddRange(AssetsToAdd);
            }

            List<Core.Models.BoxSize> retval = new List<Core.Models.BoxSize>();
            foreach (var item in DistictAssets)
            {
                var box = boxConfig.Where(bx => bx.AssetPair == item).FirstOrDefault();
                retval.Add(new Core.Models.BoxSize()
                {
                    AssetPair = box.AssetPair,
                    BoxesPerRow = box.BoxesPerRow,
                    BoxHeight = box.BoxHeight,
                    BoxWidth = box.BoxWidth,
                    TimeToFirstBox = box.TimeToFirstBox
                });

            }

            return retval;
        }

        #region Coefficient Cache Monitor
        Dictionary<string, string> CoefficientCache;

        private void LoadCoefficientCache()
        {
            string[] assets = defaultBoxConfig.Select(b => b.AssetPair).ToArray();

            Task<Dictionary<string, string>> t = CoeffCalculatorRequestBatch(GameManagerId, assets);
            t.Wait();

            lock (CoeffCacheLock)
            {
                //Console.WriteLine("{0} > LoadCoefficientCache LOCK", DateTime.Now.ToString("HH:mm:ss.fff"));
                CoefficientCache = t.Result;
            }
            //Console.WriteLine("{0} > LoadCoefficientCache LOCK Released", DateTime.Now.ToString("HH:mm:ss.fff"));
        }

        private string GetCoefficients(string assetPair)
        {
            string retval = "";
            lock (CoeffCacheLock)
            {
                //Console.WriteLine("{0} > GetCoefficients LOCK", DateTime.Now.ToString("HH:mm:ss.fff"));
                retval = CoefficientCache[assetPair];
            }
            //Console.WriteLine("{0} > GetCoefficients LOCK release", DateTime.Now.ToString("HH:mm:ss.fff"));
            return retval;
        }

        private void StartCoefficientCacheMonitor()
        {
            CoeffMonitorTimer = new System.Threading.Timer(new System.Threading.TimerCallback(CoeffMonitorTimerCallback), null, CoeffMonitorTimerInterval, -1);

        }
        private void CoeffMonitorTimerCallback(object status)
        {
            CoeffMonitorTimer.Change(-1, -1);

            LoadCoefficientCache();

            if (!isDisposing)
                CoeffMonitorTimer.Change(CoeffMonitorTimerInterval, -1);
        }
        #endregion


        /// <summary>
        /// Finds user object in User cache or loads it from DB if not in cache
        /// Opens Wamp Topic for User Client
        /// </summary>
        /// <param name="userId">User Id</param>
        /// <returns>User Object</returns>
        private UserState GetUserState(string userId)
        {
            var ulist = from u in userList
                        where u.UserId == userId
                        select u;
            if (ulist.Count() > 1)
                throw new InvalidOperationException("User State List has duplicate entries");

            UserState current = ulist.FirstOrDefault();
            if (current == null)
            {
                // UserState not in current cache,
                // download it from database
                Task<UserState> t = LoadUserStateFromDb(userId);                               
                t.Wait();
                current = t.Result;

                // Assing WAMP realm to user
                current.StartWAMP(wampRealm, this.settings.BoxOptionsApi.GameManager.GameTopicName);

                // keep list size to maxbuffer
                if (userList.Count >= MaxUserBuffer)
                {
                    var OlderUser = (from u in userList
                                     orderby u.LastChange
                                     select u).FirstOrDefault();

                    if (OlderUser != null)
                    {
                        // Check if user does not have running bets
                        var userOpenBets = from b in betCache
                                           where b.UserId == OlderUser.UserId
                                           select b;

                        // No running bets. Kill user
                        if (userOpenBets.Count() == 0)
                        {
                            // Remove user from cache
                            userList.Remove(OlderUser);

                            // Dispose user
                            OlderUser.Dispose();
                        }
                    }
                }
                // add it to cache
                userList.Add(current);
            }
            return current;
        }

        /// <summary>
        /// Loads user object from DB
        /// </summary>
        /// <param name="userId">User Id</param>
        /// <returns>User Object</returns>
        private async Task<UserState> LoadUserStateFromDb(string userId)
        {
            //await MutexTestAsync();
            //MutexTest();
            //Console.WriteLine("MutexTestAsync Done");

            // Database object fetch
            UserState retval = await database.LoadUserState(userId);            

            if (retval == null)
            {
                // UserState not in database
                // Create new
                retval = new UserState(userId);                
                //retval.SetBalance(40.50m);
                // Save it to Database
                await database.SaveUserState(retval);

            }
            else
            {                
                // Load User Parameters
                //var userParameters = await database.LoadUserParameters(userId);
                //retval.LoadParameters(userParameters);

                // TODO: Load User Bets                
                //var bets = await database.LoadGameBets(userId, (int)GameBet.BetStates.OnGoing);
                //retval.LoadBets(bets);
            }

            return retval;
        }

        private async Task<string> CoeffCalculatorChangeBatch(string userId, Core.Models.BoxSize[] boxes)
        {
            await coeffCalculatorSemaphoreSlim.WaitAsync();
            try
            {
                string res = "EMPTY BOXES";

                //Console.WriteLine("{0} > Calculator.ChangeAsync BATCH Start", DateTime.Now.ToString("HH:mm:ss.fff"));
                foreach (var box in boxes)
                {
                    // Change calculator parameters for current pair with User parameters
                    //Console.WriteLine("{0} > Calculator.ChangeAsync Start", DateTime.Now.ToString("HH:mm:ss.fff"));
                    res = await calculator.ChangeAsync(userId, box.AssetPair, Convert.ToInt32(box.TimeToFirstBox), Convert.ToInt32(box.BoxHeight), box.BoxWidth, NPriceIndex, NTimeIndex);
                    //Console.WriteLine("{0} > Calculator.ChangeAsync Finished", DateTime.Now.ToString("HH:mm:ss.fff"));

                    if (res != "OK")
                        throw new InvalidOperationException(res);
                }
                //Console.WriteLine("{0} > Calculator.ChangeAsync BATCH Finished", DateTime.Now.ToString("HH:mm:ss.fff"));
                return res;
            }
            finally { coeffCalculatorSemaphoreSlim.Release(); }


        }        
        /// <summary>
        /// Performs a Coefficient Request to CoeffCalculator object
        /// </summary>
        /// <param name="userId">User Id</param>
        /// <param name="pair">Instrument</param>
        /// <param name="timeToFirstOption">Time to first option</param>
        /// <param name="optionLen">Option Length</param>
        /// <param name="priceSize">Price Size</param>
        /// <param name="nPriceIndex">NPrice Index</param>
        /// <param name="nTimeIndex">NTime Index</param>
        /// <returns>CoeffCalc result</returns>
        private async Task<Dictionary<string, string>> CoeffCalculatorRequestBatch(string userId, string[] assetPairs)
        {
            //Activate Mutual Exclusion Semaphor
            await coeffCalculatorSemaphoreSlim.WaitAsync();
            try
            {
                Dictionary<string, string> retval = new Dictionary<string, string>();
                //Console.WriteLine("{0} > Calculator.RequestAsync BATCH Start", DateTime.Now.ToString("HH:mm:ss.fff"));
                foreach (var asset in assetPairs)
                {
                    //Console.WriteLine("{0} > Calculator.RequestAsync Start", DateTime.Now.ToString("HH:mm:ss.fff"));
                    string res = await calculator.RequestAsync(userId, asset);
                    //Console.WriteLine("{0} > Calculator.RequestAsync Finished", DateTime.Now.ToString("HH:mm:ss.fff"));
                    retval.Add(asset, res);
                }
                //Console.WriteLine("{0} > Calculator.RequestAsync BATCH Finished", DateTime.Now.ToString("HH:mm:ss.fff"));
                return retval;                
            }
            finally { coeffCalculatorSemaphoreSlim.Release(); }

        }

        /// <summary>
        /// Sets User status, creates an UserHistory entry and saves user to DB
        /// </summary>
        /// <param name="userId">User Id</param>
        /// <param name="status">New Status</param>
        /// <param name="message">Status Message</param>
        private void SetUserStatus(string userId, GameStatus status, string message = null)
        {
            UserState user = GetUserState(userId);
            SetUserStatus(user, status, message);
        }
        /// <summary>
        /// Sets User status, creates an UserHistory entry and saves user to DB
        /// </summary>
        /// <param name="user">User Object</param>
        /// <param name="status">New Status</param>
        /// <param name="message">Status Message</param>
        private void SetUserStatus(UserState user, GameStatus status, string message = null)
        {
            Console.WriteLine("SetUserStatus - UserId:[{0}] Status:[{1}] Message:[{2}]", user.UserId, status, message);
            var hist = user.SetStatus((int)status, message);
            // Save history to database
            database.SaveUserHistory(user.UserId, hist);
            // Save status to Database
            database.SaveUserState(user);

            logRepository.InsertAsync(new Core.Models.LogItem
            {
                ClientId = user.UserId,
                EventCode = ((int)status).ToString(),
                Message = message
            });
        }

        /// <summary>
        /// Checks Bet WIN agains given parameters
        /// </summary>
        /// <param name="bet"></param>
        /// <param name="dCurrentPrice"></param>
        /// <param name="dPreviousPrice"></param>
        /// <returns>TRUE if WIN</returns>
        private bool CheckWinOngoing(GameBet bet, double dCurrentPrice, double dPreviousPrice)
        {
            decimal currentPrice = Convert.ToDecimal(dCurrentPrice);
            decimal previousPrice = Convert.ToDecimal(dPreviousPrice);

            double currentDelta = (double)currentPrice - dCurrentPrice;
            double previousDelta = (double)previousPrice - dPreviousPrice;

            if (currentDelta > 0.000001 || currentDelta < -0.000001)
                appLog.WriteWarningAsync("GameManager", "CheckWinOngoing", "", $"Double to Decimal conversion Fail! CurrDelta={currentDelta} double:{dCurrentPrice} decimal:{currentPrice}");
            if (previousDelta > 0.000001 || previousDelta < -0.000001)
                appLog.WriteWarningAsync("GameManager", "CheckWinOngoing", "", $"Double to Decimal conversion Fail! PrevDelta={previousDelta} double:{dPreviousPrice} decimal:{previousPrice}");


            if ((currentPrice > bet.Box.MinPrice && currentPrice < bet.Box.MaxPrice) ||       // currentPrice> minPrice and currentPrice<maxPrice
                (previousPrice > bet.Box.MaxPrice && currentPrice < bet.Box.MinPrice) ||     // OR previousPrice > maxPrice and currentPrice < minPrice
                (previousPrice < bet.Box.MinPrice && currentPrice > bet.Box.MaxPrice))      // OR previousPrice < minPrice and currentPrice > maxPrice
                return true;
            else
                return false;
        }
        private bool CheckWinOnstarted(GameBet bet, double dCurrentPrice)
        {
            decimal currentPrice = Convert.ToDecimal(dCurrentPrice);
            
            double currentDelta = (double)currentPrice - dCurrentPrice;            

            if (currentDelta > 0.000001 || currentDelta < -0.000001)
                appLog.WriteWarningAsync("GameManager", "CheckWinOnstarted", "", $"Double to Decimal conversion Fail! CurrDelta={currentDelta} double:{dCurrentPrice} decimal:{currentPrice}");
            


            if (currentPrice > bet.Box.MinPrice && currentPrice < bet.Box.MaxPrice)
                return true;
            else
                return false;
        }

        /// <summary>
        /// Performs a check to validate bet WIN
        /// </summary>
        /// <param name="bet">Bet</param>
        private void ProcessBetCheck(GameBet bet, bool IsFirstCheck)
        {
            // Run Check Asynchronously
            Task.Run(() =>
            {
                var assetHist = assetCache[bet.AssetPair];
                bool IsWin = false;
                if (IsFirstCheck)
                    IsWin  = CheckWinOnstarted(bet, assetHist.CurrentPrice.MidPrice());
                else
                    IsWin = CheckWinOngoing(bet, assetHist.CurrentPrice.MidPrice(), assetHist.PreviousPrice.MidPrice());
                               
                if (IsWin)
                {
                    // Process WIN
                    ProcessBetWin(bet);
                }
                else
                {
                    BetResult checkres = new BetResult(bet.Box.Id)
                    {
                        BetAmount = bet.BetAmount,
                        Coefficient = bet.Box.Coefficient,
                        MinPrice = bet.Box.MinPrice,
                        MaxPrice = bet.Box.MaxPrice,
                        TimeToGraph = bet.Box.TimeToGraph,
                        TimeLength = bet.Box.TimeLength,

                        PreviousPrice = assetHist.PreviousPrice,
                        CurrentPrice = assetHist.CurrentPrice,

                        Timestamp = bet.Timestamp,
                        TimeToGraphStamp = bet.TimeToGraphStamp,
                        WinStamp = bet.WinStamp,
                        FinishedStamp = bet.FinishedStamp,
                        BetState = (int)bet.BetStatus,
                        IsWin = IsWin
                    };
                    // Report Not WIN to WAMP
                    bet.User.PublishToWamp(checkres);

                    // Log check
                    string msg = checkres.ToJson();                    
                    AppLog("ProcessBetCheck", msg);
                }
                
            });
        }
                        
        /// <summary>
        /// Set bet status to WIN, update user balance, publish WIN to WAMP, Save to DB
        /// </summary>
        /// <param name="bet">Bet</param>
        /// <param name="res">WinCheck Result</param>
        private void ProcessBetWin(GameBet bet)
        {   
            // Set bet to win
            bet.BetStatus = GameBet.BetStates.Win;
            bet.WinStamp = DateTime.UtcNow;

            //Update user balance with prize            
            decimal prize = bet.BetAmount * bet.Box.Coefficient;
            bet.User.SetBalance(bet.User.Balance + prize);

            // Publish WIN to WAMP topic            
            var t = Task.Run(() => {
                BetResult checkres = new BetResult(bet.Box.Id)
                {
                    BetAmount = bet.BetAmount,
                    Coefficient = bet.Box.Coefficient,
                    MinPrice = bet.Box.MinPrice,
                    MaxPrice = bet.Box.MaxPrice,
                    TimeToGraph = bet.Box.TimeToGraph,
                    TimeLength = bet.Box.TimeLength,

                    PreviousPrice = assetCache[bet.AssetPair].PreviousPrice,
                    CurrentPrice = assetCache[bet.AssetPair].CurrentPrice,

                    Timestamp = bet.Timestamp,
                    TimeToGraphStamp = bet.TimeToGraphStamp,
                    WinStamp = bet.WinStamp,
                    FinishedStamp = bet.FinishedStamp,
                    BetState = (int)bet.BetStatus,
                    IsWin = true
                };
                // Publish to WAMP topic
                bet.User.PublishToWamp(checkres);
                // Raise OnBetWin Event
                OnBetWin(new BetEventArgs(bet));

                string msg = checkres.ToJson();
                AppLog("ProcessBetWin", msg);                

                SetUserStatus(bet.UserId, GameStatus.BetWon, $"Bet WON [{bet.Box.Id}] [{bet.AssetPair}] Bet:{bet.BetAmount} Coef:{bet.Box.Coefficient} Prize:{bet.BetAmount * bet.Box.Coefficient}");
            });
            // Save to Database
            database.SaveGameBet(bet.UserId, bet);
        }
        /// <summary>
        /// Set bet status to Lose(if not won),  publish WIN to WAMP, Save to DB
        /// </summary>
        /// <param name="bet">Bet</param>
        private void ProcessBetTimeOut(GameBet bet)
        {
            // Remove bet from cache
            bool res = betCache.Remove(bet);

            // If bet was not won previously
            if (bet.BetStatus != GameBet.BetStates.Win)
            {                
                // Set bet Status to lose
                bet.BetStatus = GameBet.BetStates.Lose;

                // publish LOSE to WAMP topic                
                var t = Task.Run(() => {
                    BetResult checkres = new BetResult(bet.Box.Id)
                    {
                        BetAmount = bet.BetAmount,
                        Coefficient = bet.Box.Coefficient,
                        MinPrice = bet.Box.MinPrice,
                        MaxPrice = bet.Box.MaxPrice,
                        TimeToGraph = bet.Box.TimeToGraph,
                        TimeLength = bet.Box.TimeLength,

                        PreviousPrice = assetCache.ContainsKey(bet.AssetPair) ? assetCache[bet.AssetPair].PreviousPrice : new Core.Models.InstrumentPrice(),    // BUG: No Prices on Cache 
                        CurrentPrice = assetCache.ContainsKey(bet.AssetPair) ? assetCache[bet.AssetPair].CurrentPrice: new Core.Models.InstrumentPrice(),       // check if there are any prices on cache

                        Timestamp = bet.Timestamp,
                        TimeToGraphStamp = bet.TimeToGraphStamp,
                        WinStamp = bet.WinStamp,
                        FinishedStamp = bet.FinishedStamp,
                        BetState = (int)bet.BetStatus,
                        IsWin = false
                    };
                    // Publish to WAMP topic
                    bet.User.PublishToWamp(checkres);
                    // Raise OnBetLose Event
                    OnBetLose(new BetEventArgs(bet));

                    //string msg = checkres.ToJson();
                    //AppLog("ProcessBetTimeOut", msg);
                    AppLog("ProcessBetTimeout", bet.BetLog);
                    SetUserStatus(bet.UserId, GameStatus.BetLost, $"Bet LOST [{bet.Box.Id}] [{bet.AssetPair}] Bet:{bet.BetAmount}");
                });
                database.SaveGameBet(bet.UserId, bet);
                
            }
        }
        /// <summary>
        /// Calculate Box Width acording to BoxSize
        /// </summary>
        /// <param name="boxConfig"></param>
        /// <param name="priceCache"></param>
        /// <returns></returns>
        private Core.Models.BoxSize[] CalculatedBoxes(List<Core.Models.BoxSize> boxConfig, IMicrographCache priceCache)
        {
            var gdata = priceCache.GetGraphData();

            // Only send pairs with graph data
            var filtered = from c in boxConfig
                           where gdata.ContainsKey(c.AssetPair)
                           select c;

            // Calculate BoxWidth according to average prices
            // BoxWidth = average(asset.midprice) * Box.PriceSize from database
            Core.Models.BoxSize[] retval = (from c in filtered
                                select new Core.Models.BoxSize()
                                {
                                    AssetPair = c.AssetPair,
                                    BoxesPerRow = c.BoxesPerRow,
                                    BoxHeight = c.BoxHeight,
                                    TimeToFirstBox = c.TimeToFirstBox,
                                    BoxWidth = gdata[c.AssetPair].Average(price => price.MidPrice()) * c.BoxWidth
                                }).ToArray();
            return retval;
        }

        private void AppLog(string process, string msg)
        {
            //appLog.WriteInfoAsync("GameManager", process, null, msg);
        }

        /// <summary>
        /// Raises BetWin Event
        /// </summary>
        /// <param name="e"></param>
        protected virtual void OnBetWin(BetEventArgs e)
        {
            //Console.WriteLine("{0}>OnBetWin ={1}", DateTime.Now.ToString("HH:mm:ss.fff"), e.Bet.Box.Id);
            BetWin?.Invoke(this, e);
        }
        /// <summary>
        /// Raises BetLose Event
        /// </summary>
        /// <param name="e"></param>
        protected virtual void OnBetLose(BetEventArgs e)
        {
            //Console.WriteLine("{0}>OnBetLose ={1}", DateTime.Now.ToString("HH:mm:ss.fff"), e.Bet.Box.Id);
            BetLose?.Invoke(this, e);
        }

        /// <summary>
        /// Disposes GameManager Resources
        /// </summary>
        public void Dispose()
        {
            if (isDisposing)
                return;
            isDisposing = true;

            if (CoeffMonitorTimer != null)
            {
                CoeffMonitorTimer.Change(-1, -1);
                CoeffMonitorTimer.Dispose();
                CoeffMonitorTimer = null;
            }
            
            quoteFeed.MessageReceived -= QuoteFeed_MessageReceived;
            betCache = null;

            foreach (var user in userList)
            {
                user.Dispose();
            }

            userList = null;

        }
        #endregion

        #region Event Handlers
        private async void QuoteFeed_MessageReceived(object sender, Core.Models.InstrumentPrice e)
        {
            //Activate Mutual Exclusion Semaphore
            await quoteReceivedSemaphoreSlim.WaitAsync();            
            try
            {

                // Add price to cache
                if (!assetCache.ContainsKey(e.Instrument))
                    assetCache.Add(e.Instrument, new PriceCache());

                // Update price cache
                assetCache[e.Instrument].PreviousPrice = assetCache[e.Instrument].CurrentPrice;
                assetCache[e.Instrument].CurrentPrice = (Core.Models.InstrumentPrice)e.ClonePrice();

                // Get bets for current asset
                // That are not yet with WIN status
                try
                {
                    var assetBets = (from b in betCache
                                     where b.AssetPair == e.Instrument &&
                                     b.BetStatus != GameBet.BetStates.Win
                                     select b).ToList();
                    if (assetBets.Count == 0)
                        return;

                    foreach (var bet in assetBets)
                    {
                        ProcessBetCheck(bet, false);
                    }
                }
                catch (Exception ex)
                {
                    await appLog.WriteErrorAsync("GameManager", "QuoteFeed_MessageReceived", null, ex);
                    throw;
                }
            }
            finally { quoteReceivedSemaphoreSlim.Release(); }

        }

        private void Bet_TimeToGraphReached(object sender, EventArgs e)
        {
            GameBet bet = sender as GameBet;
            if (bet == null)
                return;
            
            // Do initial Check            
            if (assetCache.ContainsKey(bet.AssetPair))
            {
                if (assetCache[bet.AssetPair].CurrentPrice.MidPrice() > 0 && assetCache[bet.AssetPair].PreviousPrice.MidPrice() > 0)
                {
                    ProcessBetCheck(bet, true);
                }
            }            
                     
            // Add bet to cache
            betCache.Add(bet);
        }
        private void Bet_TimeLenghFinished(object sender, EventArgs e)
        {
            GameBet sdr = sender as GameBet;
            if (sdr == null)
                return;
            ProcessBetTimeOut(sdr);


        }
        #endregion

        #region IGameManager
        public Core.Models.BoxSize[] InitUser(string userId)
        {
            UserState userState = GetUserState(userId);

            //
            List<Core.Models.BoxSize> boxConfig = LoadBoxParameters();

            if (defaultBoxConfig == null)
            {
                defaultBoxConfig = (from c in boxConfig
                                    select new Core.Models.BoxSize()
                                    {
                                        AssetPair = c.AssetPair,
                                        BoxesPerRow = c.BoxesPerRow,
                                        BoxHeight = c.BoxHeight,
                                        BoxWidth = c.BoxWidth,
                                        TimeToFirstBox = c.TimeToFirstBox
                                    }).ToList();
                InitializeCoefCalc();

            }

            // Return Calculate Price Sizes
            Core.Models.BoxSize[] retval = CalculatedBoxes(boxConfig, micrographCache);
            return retval;
        }      

        public DateTime PlaceBet(string userId, string assetPair, string box, decimal bet)
        {
            //Console.WriteLine("{0}> PlaceBet({1} - {2} - {3:f16})", DateTime.UtcNow.ToString("HH:mm:ss.fff"), userId, box, bet);

            // Get user state
            UserState userState = GetUserState(userId);
            
            // Validate balance
            if (bet > userState.Balance)
                throw new InvalidOperationException("User has no balance for the bet.");

            // TODO: Get Box from... somewhere            
            Box boxObject = Box.FromJson(box);

           
            // Get Current Coeffs for Game's Assetpair
            var assetConfig = defaultBoxConfig.Where(b => b.AssetPair == assetPair).FirstOrDefault();
            if (assetConfig == null)
                throw new InvalidOperationException($"Coefficient parameters are not set for Asset Pair [{assetPair}].");


            // Place Bet            
            GameBet newBet = userState.PlaceBet(boxObject, assetPair, bet, assetConfig);
            newBet.TimeToGraphReached += Bet_TimeToGraphReached;
            newBet.TimeLenghFinished += Bet_TimeLenghFinished;
            
            // Run bet
            newBet.StartWaitTimeToGraph();

            // Save bet to DB
            database.SaveGameBet(userState.UserId, newBet);

            // Update user balance
            userState.SetBalance(userState.Balance - bet);

            // Set Status, saves User to DB            
            SetUserStatus(userState, GameStatus.BetPlaced, $"BetPlaced[{boxObject.Id}]. Asset:{assetPair}  Bet:{bet} Balance:{userState.Balance}");

            AppLog("PlaceBet", $"Coef:{boxObject.Coefficient} Id:{boxObject.Id}");            
            return newBet.Timestamp;
        }

        public decimal SetUserBalance(string userId, decimal newBalance)
        {
            UserState userState = GetUserState(userId);
            userState.SetBalance(newBalance);
            
            // Save User to DB            
            database.SaveUserState(userState);

            // Log Balance Change
            SetUserStatus(userState, GameStatus.BalanceChanged, $"New Balance: {newBalance}");

            return newBalance;
        }
                
        public decimal GetUserBalance(string userId)
        {
            UserState userState = GetUserState(userId);
            return userState.Balance;
        }

        //public void SetUserParameters(string userId, string pair, int timeToFirstOption, int optionLen, double priceSize, int nPriceIndex, int nTimeIndex)
        //{
        //    UserState userState = GetUserState(userId);

        //    // Validate Parameters
        //    bool ValidateParameters = calculator.ValidateChange(userId, pair, timeToFirstOption, optionLen, priceSize, nPriceIndex, nTimeIndex);
        //    if (ValidateParameters == false)
        //    {
        //        // Invalid Parameters, throw error
        //        throw new ArgumentException("Invalid Parameters");
        //    }

        //    // Set User Parameters for AssetPair
        //    userState.SetParameters(pair, timeToFirstOption, optionLen, priceSize, nPriceIndex, nTimeIndex);
        //    // Save User Parameters to DB
        //    database.SaveUserParameters(userId, userState.UserCoeffParameters);
        //    // Update User Status
        //    SetUserStatus(userState, GameStatus.ParameterChanged, $"ParameterChanged [{pair}] timeToFirstOption={timeToFirstOption}; optionLen={optionLen}; priceSize={priceSize}; nPriceIndex={nPriceIndex}, nTimeIndex={nTimeIndex}");
        //}
        //public CoeffParameters GetUserParameters(string userId, string pair)
        //{
        //    UserState userState = GetUserState(userId);
        //    return userState.GetParameters(pair);
        //}
        public string RequestUserCoeff(string userId, string pair)
        {

            // Request Coeffcalculator Data            
            string result = GetCoefficients(pair);
            return result;

            // TODO: Validate CoefCalculator Result
            //string ValidationError;
            //654bool IsOk = calculator.ValidateRequestResult(result, out ValidationError);

            //// Take action on validation result.
            //if (IsOk)
            //{
            //    // Udpdate User Status
            //    //SetUserStatus(userState, GameStatus.CoeffRequest, $"CoeffRequest [{pair}]");
            //    // return CoeffCalcResult
            //    return result;
            //}
            //else
            //{
            //    // Throw Exception
            //    throw new ArgumentException(ValidationError);
            //}
        }

        public void AddUserLog(string userId, string eventCode, string message)
        {
            UserState userState = GetUserState(userId);

            int ecode = -1;
            int.TryParse(eventCode, out ecode);
            SetUserStatus(userState, (GameStatus)ecode, message);
        }
        
        #endregion
                
        #region Nested Class
        private class PriceCache
        {
            public Core.Models.InstrumentPrice CurrentPrice { get; set; }
            public Core.Models.InstrumentPrice PreviousPrice { get; set; }
        }
        #endregion

        #region Tests
        //private void MutexTest()
        //{
        //    var gdata = graphCache.GetGraphData();

        //    int timeToFirstOption = 30000;
        //    int optionLen = 30000;
        //    double priceSize = 0.0002;
        //    int nPriceindex = 15;
        //    int nTimeIndex = 15;

        //    foreach (var item in gdata)
        //    {
        //        Task t = CoeffCalculatorRequest("USERID", item.Key, timeToFirstOption, optionLen, priceSize, nPriceindex, nTimeIndex);
        //        //t.Start();

        //        optionLen += 1000;
        //        priceSize += 0.0002;
        //    }
        //    Console.WriteLine("ss");
        //}
        //private async Task MutexTestAsync()
        //{
        //    var gdata = graphCache.GetGraphData();

        //    int timeToFirstOption = 30000;
        //    int optionLen = 30000;
        //    double priceSize = 0.0002;
        //    int nPriceindex = 15;
        //    int nTimeIndex = 15;

        //    foreach (var item in gdata)
        //    {
        //        if (item.Key == "BTCUSD")
        //            continue;
        //        string res = await CoeffCalculatorRequest("USERID", item.Key, timeToFirstOption, optionLen, priceSize, nPriceindex, nTimeIndex);
        //        Console.WriteLine(res);
        //        //t.Start();

        //        optionLen += 1000;
        //        priceSize += 0.0002;
        //    }
        //    Console.WriteLine("ss");
        //}
        #endregion
    }
}
