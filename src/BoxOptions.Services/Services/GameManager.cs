﻿using BoxOptions.Common;
using BoxOptions.Common.Extensions;
using BoxOptions.Common.Interfaces;
using BoxOptions.Common.Models;
using BoxOptions.Common.Settings;
using BoxOptions.Core.Interfaces;
using BoxOptions.Core.Repositories;
using BoxOptions.Services.Models;
using Common.Log;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using WampSharp.V2.Realm;


namespace BoxOptions.Services
{
    public class GameManager : IGameManager, IDisposable
    {
        const int NPriceIndex = 15; // Number of columns hardcoded
        const int NTimeIndex = 8;   // Number of rows hardcoded
        const int CoefficientCacheUpdateInterval = 1000; // Coeff cache update interval (milliseconds)
        const int BoxRecalculationInterval = 3600 * 1000; // BoxSize recalculation interval (milliseconds)

        #region Vars
        /// <summary>
        /// Coefficient Calculator Request Semaphore
        /// Mutual Exclusion Process
        /// </summary>
        private static readonly System.Threading.SemaphoreSlim coeffCalculatorSemaphoreSlim = new System.Threading.SemaphoreSlim(1, 1);
        /// <summary>
        /// Process AssetQuote Received Semaphore.
        /// Mutual Exclusion Process
        /// </summary>
        private static readonly System.Threading.SemaphoreSlim quoteReceivedSemaphoreSlim = new System.Threading.SemaphoreSlim(1, 1);
                
        private static readonly object BetCacheLock = new object();

        private static readonly CultureInfo Ci = new CultureInfo("en-us");

        private readonly int MaxUserBuffer = 128;
        private readonly string GameManagerId;

        private readonly CoefficientCache _coefficientCache;

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
        private readonly IAssetQuoteSubscriber _assetQuoteSubscriber;
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
        private readonly IHistoryHolder _historyHolder;
        /// <summary>
        /// Settings
        /// </summary>
        private readonly BoxOptionsApiSettings settings;
        /// <summary>
        /// Last Prices Cache
        /// </summary>
        private Dictionary<string, PriceCache> assetCache;

        private System.Threading.Timer CoefficientCacheUpdateTimer;
        private System.Threading.Timer BoxRecalculationTimer;

        /// <summary>
        /// Ongoing Bets Cache
        /// </summary>
        private List<GameBet> betCache;
        /// <summary>
        /// Users Cache
        /// </summary>
        private List<UserState> userList;

                

        /// <summary>
        /// Box configuration
        /// </summary>
        private List<BoxSize> dbBoxConfig;
        private List<BoxSize> _calculatedBoxes;

        private Queue<string> appLogInfoQueue = new Queue<string>();
                
        private bool isDisposing = false;

        private DateTime lastCoeffChange;
        private DateTime LastErrorDate = DateTime.MinValue;
        private string LastErrorMessage = "";
        
        private Dictionary<string, string> coefStatus;
        #endregion

        #region Constructor
        public GameManager(BoxOptionsApiSettings settings, IGameDatabase database, ICoefficientCalculator calculator,
            IAssetQuoteSubscriber quoteFeed, IWampHostedRealm wampRealm, IMicrographCache micrographCache, IBoxConfigRepository boxConfigRepository, ILogRepository logRepository, ILog appLog, IHistoryHolder historyHolder)
        {
            this.database = database;
            this.calculator = calculator;
            this._assetQuoteSubscriber = quoteFeed;
            this.settings = settings;
            this.logRepository = logRepository;
            this.appLog = appLog;
            this.wampRealm = wampRealm;
            this.boxConfigRepository = boxConfigRepository;
            this.micrographCache = micrographCache;
            _historyHolder = historyHolder;
            _coefficientCache = new CoefficientCache();

            if (this.settings != null && this.settings != null && this.settings.GameManager != null)
                MaxUserBuffer = this.settings.GameManager.MaxUserBuffer;

            GameManagerId = Guid.NewGuid().ToString();
            userList = new List<UserState>();
            betCache = new List<GameBet>();
            assetCache = new Dictionary<string, PriceCache>();
                        
            this._assetQuoteSubscriber.MessageReceived += QuoteFeed_MessageReceived;

            //calculateBoxConfig = null;
            dbBoxConfig = Task.Run(() => LoadBoxParameters()).Result;            
            Console.WriteLine("Db Box Config = [{0}]", string.Join(",", dbBoxConfig.Select(b => b.AssetPair)));

            coefStatus = new Dictionary<string, string>();

            _historyHolder.InitializationFinished += HistoryHolder_InitializationFinished;            
        }

        #endregion

        /// <summary>
        /// Disposes GameManager Resources
        /// </summary>
        public void Dispose()
        {
            if (isDisposing)
                return;
            isDisposing = true;

            _assetQuoteSubscriber.MessageReceived -= QuoteFeed_MessageReceived;

            if (CoefficientCacheUpdateTimer != null)
            {
                CoefficientCacheUpdateTimer.Change(-1, -1);
                CoefficientCacheUpdateTimer.Dispose();
            }

            if (BoxRecalculationTimer != null)
            {
                BoxRecalculationTimer.Change(-1, -1);
                BoxRecalculationTimer.Dispose();
            }

            betCache = null;
            foreach (var user in userList)
            {
                user.Dispose();
            }

            userList = null;

        }

        private GameBet[] GetRunningBets()
        {
            GameBet[] retval;
            lock (BetCacheLock)
            {
                retval = new GameBet[betCache.Count];
                betCache.CopyTo(retval);
            }
            return retval;
        }
        
        #region Box Sizes
        private async Task<List<BoxSize>> LoadBoxParameters()
        {
            var dbConfig = await boxConfigRepository.GetAll();
            List<BoxSize> AssetsToAdd = new List<BoxSize>();

            List<string> AllAssets = settings.PricesSettingsBoxOptions.PrimaryFeed.AllowedAssets.ToList();
            AllAssets.AddRange(settings.PricesSettingsBoxOptions.SecondaryFeed.AllowedAssets.ToList());


            string[] DistictAssets = AllAssets.Distinct().ToArray();
            // Validate Allowed Assets
            foreach (var item in DistictAssets)
            {

                // If database does not contain allowed asset then add it
                if (!dbConfig.Select(config => config.AssetPair).Contains(item))
                {
                    // Check if it was not added before to avoid duplicates
                    if (!AssetsToAdd.Select(config => config.AssetPair).Contains(item))
                    {
                        // Add default settings
                        AssetsToAdd.Add(new BoxSize
                        {
                            AssetPair = item,
                            BoxesPerRow = 7,
                            BoxHeight = 7000,
                            BoxWidth = 0.00003,
                            TimeToFirstBox = 4000,
                            GameAllowed = false,
                            SaveHistory = false,
                            ScaleK = 0.0009
                        });
                    }
                }
            }
            List<IBoxSize> boxConfig = dbConfig.ToList();
            if (AssetsToAdd.Count > 0)
            {
                await boxConfigRepository.InsertManyAsync(AssetsToAdd);
            }

            // Check if boxes have Scale_K
            AssetsToAdd.Clear();
            foreach (var item in boxConfig)
            {
                if (item.ScaleK == 0)
                {
                    var b = item.ToDto();
                    b.ScaleK = 0.0009;
                    AssetsToAdd.Add(b);                    
                }
            }
            if (AssetsToAdd.Count > 0)
            {
                await boxConfigRepository.InsertManyAsync(AssetsToAdd);                
            }

            //Refresh list
            boxConfig = (await boxConfigRepository.GetAll()).ToList();

            List<BoxSize> retval = new List<BoxSize>();
            foreach (var item in DistictAssets)
            {
                var box = boxConfig.Where(bx => bx.AssetPair == item).FirstOrDefault();
                retval.Add(new BoxSize
                {
                    AssetPair = box.AssetPair,
                    BoxesPerRow = box.BoxesPerRow,
                    BoxHeight = box.BoxHeight,
                    BoxWidth = box.BoxWidth,
                    TimeToFirstBox = box.TimeToFirstBox,
                    GameAllowed = box.GameAllowed,
                    SaveHistory = box.SaveHistory,
                    ScaleK = box.ScaleK
                });

            }

            return retval;
        }

        /// <summary>
        /// Calculate Box Width acording to BoxSize
        /// </summary>
        /// <param name="boxConfig"></param>
        /// <param name="priceCache"></param>
        /// <returns></returns>
        private List<BoxSize> CalculateBoxes(List<BoxSize> boxConfig, IMicrographCache priceCache)
        {   
            var graphData = priceCache.GetGraphData();

            // Only send pairs with graph data
            var filtered = boxConfig.Where(c => graphData.ContainsKey(c.AssetPair));

            // Get Volatility
            var Volatility = GetVolatilities();
            
            // Calculate BoxWidth according to average prices
            // BoxWidth = average(asset.midprice) * Box.PriceSize from database
            var retval = (from b in filtered
                          select new BoxSize
                          {
                              AssetPair = b.AssetPair,
                              BoxesPerRow = b.BoxesPerRow,
                              BoxHeight = b.BoxHeight,
                              TimeToFirstBox = b.TimeToFirstBox,
                              BoxWidth = CalcBoxWidth(b.AssetPair, graphData[b.AssetPair], Volatility[b.AssetPair], b.ScaleK, b.BoxWidth),
                              SaveHistory = b.SaveHistory,
                              GameAllowed = b.GameAllowed
                          }).ToList();
            return retval;
        }
        private double CalcBoxWidth(string assetPair, IEnumerable<Price> graphData, double volatility, double scale_K, double dbBoxWidth)
        {
            double boxWidth;
            if (volatility == 0)
                boxWidth = graphData.Average(price => price.MidPrice()) * dbBoxWidth;
            else
            {
                boxWidth = graphData.Average(price => price.MidPrice()) * volatility * scale_K;
                UpdateDatabaseBox(assetPair, volatility * scale_K);
            }
            LogInfo("CalcBoxWidth", $"[{assetPair}] Volatility={volatility}, Scale_K={scale_K}, BoxWidth={boxWidth}");
            return boxWidth;
        }

        private void BoxRecalculationTimerCallback(object status)
        {
            BoxRecalculationTimer.Change(-1, -1);
            _calculatedBoxes = CalculateBoxes(dbBoxConfig, micrographCache).ToList();

            if (!isDisposing)
                BoxRecalculationTimer.Change(BoxRecalculationInterval, -1);

        }

        private void UpdateDatabaseBox(string assetPair, double boxWidth)
        {
            var box = dbBoxConfig.FirstOrDefault(b => b.AssetPair == assetPair);
            if (box == null)
                return;
            box.BoxWidth = boxWidth;
            Task.Run(async () => await boxConfigRepository.InsertAsync(box));
        }
        #endregion
                
        #region Coefficient Cache

        private async Task SetCoeffs()
        {            
            await coeffCalculatorSemaphoreSlim.WaitAsync();
            try
            {
                foreach (var box in _calculatedBoxes)
                {
                    try
                    {
                        var res = await calculator.ChangeAsync(GameManagerId, box.AssetPair, Convert.ToInt32(box.TimeToFirstBox), Convert.ToInt32(box.BoxHeight), box.BoxWidth, NPriceIndex, NTimeIndex);
                        lastCoeffChange = DateTime.UtcNow;
                        await ProcessCoeffSetNotifications(box.AssetPair, res);                        
                    }
                    catch (Exception ex)
                    {
                        LogInfo("SetCoeffs", $"[{box.AssetPair}] Error: {ex.Message}");
                    }
                }
            }
            finally
            { coeffCalculatorSemaphoreSlim.Release(); }
        }                
        private async Task GetCoeffs()
        {
            string[] assets = dbBoxConfig.Where(a => a.GameAllowed).Select(b => b.AssetPair).ToArray();
            await coeffCalculatorSemaphoreSlim.WaitAsync();
            try
            {
                foreach (var asset in assets)
                {
                    try
                    {
                        string coeffString = await calculator.RequestAsync(GameManagerId, asset);
                        var res = _coefficientCache.SetCache(asset, coeffString);
                        await ProcessCoeffGetNotifications(asset, res);
                    }
                    catch (Exception ex)
                    {
                        LogInfo("GetCoeffs", $"[{asset}] Error: {ex.Message}");
                    }
                }
            }
            finally
            { coeffCalculatorSemaphoreSlim.Release(); }
        }

        private async Task ProcessCoeffGetNotifications(string asset, string res)
        {
            if (!coefStatus.ContainsKey(asset))
                coefStatus.Add(asset, "");

            if (res != coefStatus[asset])
            {
                if (res == "OK" && coefStatus[asset]== "All coefficients are equal to 1.0")
                {
                    await appLog.WriteWarningAsync("GetCoeffs", null, $"Coefficients for [{asset}] are not 1.0 anymore");
                }
                else if (res == "All coefficients are equal to 1.0" )
                {
                    await appLog.WriteWarningAsync("GetCoeffs", null, $"Coefficients for [{asset}] are all 1.0");
                }
                coefStatus[asset] = res;
            }
        }
        private async Task ProcessCoeffSetNotifications(string assetPair, string res)
        {
            string msg = $"{DateTime.UtcNow.ToTimeString()} | SetCoeffs[{assetPair}]: {res}";
            Console.WriteLine(msg);            
        }
                
        private void CoefficientCacheUpdateTimerCallback(object status)
        {
            CoefficientCacheUpdateTimer.Change(-1, -1);
            try
            {
                // If more than 5 minute passed since last change, do another change
                if (lastCoeffChange.AddMinutes(5) < DateTime.UtcNow)
                    Task.Run(async () => await SetCoeffs()).Wait();
                else
                    Task.Run(async () => await GetCoeffs()).Wait(); 
            }
            catch (Exception ex) { LogError("CoeffMonitorTimerCallback", ex); }

            if (!isDisposing)
                CoefficientCacheUpdateTimer.Change(CoefficientCacheUpdateInterval, -1);
        }
        
        #endregion

        #region User Methods

        private BoxSize[] InitializeUser(string userId)
        {
            try
            {
                UserState userState = GetUserState(userId);

                // Add Launch to user history 
                string launchMsg = "User Initialization. BoxSizes:";
                foreach (var boxSize in _calculatedBoxes)
                {
                    launchMsg += string.Format(Ci, "[{0};BoxWidth:{1};BoxHeight:{2};TimeToFirstBox:{3};BoxesPerRow:{4}]", boxSize.AssetPair, boxSize.BoxWidth, boxSize.BoxHeight, boxSize.TimeToFirstBox, boxSize.BoxesPerRow);
                }
                SetUserStatus(userState, GameStatus.Launch, 0, launchMsg);

                // Return Calculated Price Sizes
                return _calculatedBoxes.ToArray();
            }
            catch (Exception ex01)
            {
                LogInfo("InitializeUser", $"UserId:[{userId}] Error:{ex01.Message}");
                throw;
            }            
            
        }

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
                current.StartWAMP(wampRealm, settings.GameManager.GameTopicName);

                // keep list size to maxbuffer
                if (userList.Count >= MaxUserBuffer)
                {
                    var OlderUser = (from u in userList
                                     orderby u.LastChange
                                     select u).FirstOrDefault();

                    if (OlderUser != null)
                    {
                        // Check if user does not have running bets
                        var userOpenBets = from b in OlderUser.OpenBets
                                           where b.BetStatus == BetStates.Waiting || b.BetStatus == BetStates.OnGoing
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
            UserState result;
            // Database object fetch
            var dbUser = await database.LoadUserState(userId);
            if (dbUser == null)
            {
                // UserState not in database
                // Create new
                result = new UserState(userId);
                // Save it to Database
                await database.SaveUserState(result.ToDto());
            }
            else
            {
                result = new UserState(dbUser);

                // Load User Parameters
                //var userParameters = await database.LoadUserParameters(userId);
                //retval.LoadParameters(userParameters);

                // TODO: Load User Bets                
                //var bets = await database.LoadGameBets(userId, (int)GameBet.BetStates.OnGoing);
                //retval.LoadBets(bets);
            }
            
            return result;
        }
                
        /// <summary>
        /// Sets User status, creates an UserHistory entry and saves user to DB
        /// </summary>
        /// <param name="user">User Object</param>
        /// <param name="status">New Status</param>
        /// <param name="message">Status Message</param>
        private void SetUserStatus(UserState user, GameStatus status,double accountdelta, string message = null)
        {
            var hist = user.SetStatus(status, message, accountdelta);
            // Save User
            database.SaveUserState(user.ToDto());
            // Save user History
            database.SaveUserHistory(hist);
        }

        #endregion

        #region Game Logic

        private GameBet PlaceNewBet(string userId, string assetPair, string box, decimal bet, out string message)
        {
            message = "Placing Bet";

            // Get user state
            UserState userState = GetUserState(userId);

            // Validate balance
            if (bet > userState.Balance)
            {
                message = "User has no balance for the bet.";
                SetUserStatus(userState, GameStatus.Error, 0, "PlaceBet Failed:" + message);
                return null;
            }
                        
            Box boxObject = Box.FromJson(box);
            
            // Get Current Coeffs for Game's Assetpair
            var assetConfig = dbBoxConfig.Where(b => b.GameAllowed).Where(b => b.AssetPair == assetPair).FirstOrDefault();
            if (assetConfig == null)
            {
                message = $"Box Size parameters are not set for Asset Pair[{ assetPair}].";
                SetUserStatus(userState, GameStatus.Error, 0, "PlaceBet Failed:" + message);
                return null;
            }

            // Validate Coeff:
            bool IsCoefValid = ValidateCoeff(assetPair, boxObject, assetConfig);
            if (!IsCoefValid)
            {
                message = $"Invalid Coefficient[{boxObject.Coefficient}].";
                SetUserStatus(userState, GameStatus.Error, 0, "PlaceBet Failed:" + message);
                return null;
            }

            // Place Bet            
            GameBet newBet = userState.PlaceBet(boxObject, assetPair, bet, assetConfig);
            newBet.TimeToGraphReached += Bet_TimeToGraphReached;
            newBet.TimeLenghFinished += Bet_TimeLenghFinished;

            // Update user balance
            userState.SetBalance(userState.Balance - bet);

            // Run bet
            newBet.StartWaitTimeToGraph();

            // Async Save to Database
            Task.Run(() =>
            {
                // Save bet to DB                
                database.SaveGameBet(newBet);

                // Set Status, saves User to DB            
                SetUserStatus(userState, GameStatus.BetPlaced, -Convert.ToDouble(bet), $"BetPlaced[{boxObject.Id}]. Asset:{assetPair}  Bet:{bet} Balance:{userState.Balance}");
            });

            message = "OK";
            return newBet;
        }

        // Validate Coeffs, task BOXOPT-29 to be implemented in the future.
        private bool ValidateCoeff(string assetPair, Box box, BoxSize assetConfig)
        {
            return true;
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
                LogWarning("CheckWinOngoing", $"Double to Decimal conversion Fail! CurrDelta={currentDelta} double:{dCurrentPrice} decimal:{currentPrice}");                
            if (previousDelta > 0.000001 || previousDelta < -0.000001)
                LogWarning("CheckWinOngoing", $"Double to Decimal conversion Fail! PrevDelta={previousDelta} double:{dPreviousPrice} decimal:{previousPrice}");
            


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
                LogWarning("CheckWinOnstarted", $"Double to Decimal conversion Fail! CurrDelta={currentDelta} double:{dCurrentPrice} decimal:{currentPrice}");

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
            if (bet == null || bet.BetStatus == BetStates.Win || bet.BetStatus == BetStates.Lose)
            {
                // bet already processed;
                return;
            }
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
                    if (IsFirstCheck)
                    {
                        // Report TimeTograph Reached
                        GameEvent ev = new GameEvent
                        {
                            EventType = (int)GameEventType.BetResult,
                            EventParameters = string.Format("{0};{1}", bet.Box.Id, (int)bet.BetStatus)
                            //EventParameters = checkres.ToJson()
                        };
                        bet.User.PublishToWamp(ev);
                    }
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
            bet.BetStatus = BetStates.Win;
            bet.WinStamp = DateTime.UtcNow;

            //Update user balance with prize            
            decimal prize = bet.BetAmount * bet.Box.Coefficient;
            bet.User.SetBalance(bet.User.Balance + prize);

            // Publish WIN to WAMP topic            
            var t = Task.Run(() => {
                //BetResult checkres = new BetResult(bet.Box.Id)
                //{
                //    BetAmount = bet.BetAmount,
                //    Coefficient = bet.Box.Coefficient,
                //    MinPrice = bet.Box.MinPrice,
                //    MaxPrice = bet.Box.MaxPrice,
                //    TimeToGraph = bet.Box.TimeToGraph,
                //    TimeLength = bet.Box.TimeLength,

                //    PreviousPrice = assetCache[bet.AssetPair].PreviousPrice,
                //    CurrentPrice = assetCache[bet.AssetPair].CurrentPrice,

                //    Timestamp = bet.Timestamp,
                //    TimeToGraphStamp = bet.TimeToGraphStamp,
                //    WinStamp = bet.WinStamp,
                //    FinishedStamp = bet.FinishedStamp,
                //    BetState = (int)bet.BetStatus,
                //    IsWin = true
                //};
                // Publish to WAMP topic
                GameEvent ev = new GameEvent()
                {
                    EventType = (int)GameEventType.BetResult,
                    EventParameters = string.Format("{0};{1}", bet.Box.Id, (int)bet.BetStatus)
                    //EventParameters = checkres.ToJson()
                };
                bet.User.PublishToWamp(ev);
                
                // Save bet to Database
                database.SaveGameBet(bet);

                // Set User Status
                UserState user = GetUserState(bet.UserId);
                SetUserStatus(user, GameStatus.BetWon, Convert.ToDouble(bet.BetAmount * bet.Box.Coefficient), $"Bet WON [{bet.Box.Id}] [{bet.AssetPair}] Bet:{bet.BetAmount} Coef:{bet.Box.Coefficient} Prize:{bet.BetAmount * bet.Box.Coefficient}");
            });
            
        }
        /// <summary>
        /// Set bet status to Lose(if not won),  publish WIN to WAMP, Save to DB
        /// </summary>
        /// <param name="bet">Bet</param>
        private void ProcessBetTimeOut(GameBet bet)
        {
            // Remove bet from cache
            lock (BetCacheLock)
            {
                bool res = betCache.Remove(bet);
            }
            
            // If bet was not won previously
            if (bet.BetStatus != BetStates.Win)
            {                
                // Set bet Status to lose
                bet.BetStatus = BetStates.Lose;

                // publish LOSE to WAMP topic                
                var t = Task.Run(() => {
                    
                    // Publish LOSE to WAMP topic
                    GameEvent ev = new GameEvent
                    {
                        EventType = (int)GameEventType.BetResult,
                        EventParameters = string.Format("{0};{1}", bet.Box.Id, (int)bet.BetStatus)
                    };
                    bet.User.PublishToWamp(ev);
                    
                    // Save bet to Database
                    database.SaveGameBet(bet);

                    // Set User Status
                    UserState user = GetUserState(bet.UserId);
                    SetUserStatus(user, GameStatus.BetLost, 0, $"Bet LOST [{bet.Box.Id}] [{bet.AssetPair}] Bet:{bet.BetAmount}");
                });
                
                
            }
        }

        #endregion
        
        #region IGameManager
        public IBoxSize[] InitUser(string userId)
        {   
            var result = InitializeUser(userId);

            // Send answer to client im seconds instead milliseconds
            return result.Select(item => new BoxSize
            {
                AssetPair = item.AssetPair,
                BoxesPerRow = item.BoxesPerRow,
                BoxWidth = item.BoxWidth,
                BoxHeight = item.BoxHeight / 1000,
                TimeToFirstBox = item.TimeToFirstBox / 1000,
                GameAllowed = item.GameAllowed,
                SaveHistory = item.SaveHistory
            })
            .ToArray();
        }      
         
        public DateTime PlaceBet(string userId, string assetPair, string box, decimal bet,  out string message)
        {            
            var newBet = PlaceNewBet(userId, assetPair, box, bet, out message);
            if (newBet == null)
                return DateTime.MinValue;
            else
                return newBet.Timestamp;
        }

        public decimal SetUserBalance(string userId, decimal newBalance)
        {
            UserState userState = GetUserState(userId);
            decimal oldBalance = userState.Balance;
            userState.SetBalance(newBalance);
            decimal balanceDelta = newBalance - oldBalance;

            // Log Balance Change
            SetUserStatus(userState, GameStatus.BalanceChanged, Convert.ToDouble(balanceDelta), $"New Balance: {newBalance}");

            return newBalance;
        }

        public decimal GetUserBalance(string userId)
        {
            UserState userState = GetUserState(userId);
            return userState.Balance;
        }        
               
        public string RequestUserCoeff(string pair, string userId = null)
        {
            // feature_BOXOPT-56_all_payoffs_must_be_1
            // If no prices were received in the last ten minutes return all at 1.0
            if (!assetCache.ContainsKey(pair) || DateTime.UtcNow - assetCache[pair].CurrentPrice.Date > new TimeSpan(0, 10, 0))
                return CoefModels.GetEmptyCoeffs();
            string result = _coefficientCache.GetCache(pair);
            if (userId != null)
            {
                UserState user = GetUserState(userId);
                SetUserStatus(user, GameStatus.CoeffRequest, 0, string.Format("[{0}]", pair));
            }
            return result;
        }

        public void AddUserLog(string userId, string eventCode, string message)
        {
            double accountdelta = 0;
            if (eventCode == "8")// BetPlaced
            {
                //string test = "Coeff: 1.24830386982552, Bet: 1.0";
                //model.Message = test;

                int index = message.IndexOf("Bet:");
                string betvalue = message.Substring(index, message.Length - index).Replace("Bet:", "").Trim();
                double.TryParse(betvalue, NumberStyles.AllowDecimalPoint, Ci, out accountdelta);
                if (accountdelta > 0)
                    accountdelta = -accountdelta;
            }
            else if (eventCode == "9")// BetWon
            {
                //string test = "Value: 1.24830386982552";
                //model.Message = test;

                string winvalue = message.Replace("Value:", "").Trim();
                double.TryParse(winvalue, NumberStyles.AllowDecimalPoint, Ci, out accountdelta);
            }

            // Write log to repository
            Task t = logRepository?.InsertAsync(new LogItem()
            {
                ClientId = userId,
                EventCode = eventCode,
                Message = message,
                AccountDelta = accountdelta
            });
            t.Wait();

            // Set Current User Status User
            UserState userState = GetUserState(userId);
            int ecode = -1;
            int.TryParse(eventCode, out ecode);
            SetUserStatus(userState, (GameStatus)ecode, accountdelta, message);
        }

        public async Task ReloadGameAssets()
        {
            dbBoxConfig = await LoadBoxParameters();
            BoxRecalculationTimer.Change(1000, -1);
        }

        public Dictionary<string, double> GetVolatilities()
        {   
            Dictionary<string, double> Volatility = new Dictionary<string, double>();
            foreach (var box in dbBoxConfig.Where(b=>b.GameAllowed))
            {
                Volatility.Add(box.AssetPair, calculator.GetVolatility(box.AssetPair));
            }
            return Volatility;
        }

        #endregion

        private async void LogInfo(string process, string info)
        {
            Console.WriteLine($"{DateTime.UtcNow.ToTimeString()} | {process} | {info}");
            await appLog?.WriteInfoAsync("BoxOptions.Services.GameManager", process, null, info, DateTime.UtcNow);
        }
        private async void LogWarning(string process, string warning)
        {
            Console.WriteLine($"WRN {DateTime.UtcNow.ToTimeString()} | {process} | {warning}");
            await appLog?.WriteWarningAsync("BoxOptions.Services.GameManager", process, null, warning, DateTime.UtcNow);
        }
        private async void LogError(string process, Exception ex)
        {
            Exception innerEx;
            if (ex.InnerException != null)
                innerEx = ex.InnerException;
            else
                innerEx = ex;

            bool LogError;
            if (LastErrorMessage != innerEx.Message)
            {
                LogError = true;
            }
            else
            {
                if (DateTime.UtcNow > LastErrorDate.AddMinutes(1))
                    LogError = true;
                else
                    LogError = false;
            }

            Console.WriteLine($"ERR {DateTime.UtcNow.ToTimeString()} | {process} | {innerEx.Message}\n{innerEx.StackTrace}");
            if (LogError)
            {
                LastErrorMessage = innerEx.Message;
                LastErrorDate = DateTime.UtcNow;                
                await appLog?.WriteErrorAsync("BoxOptions.Services.GameManager", process, null, innerEx);
            }
        }

        #region Event Handlers
        private void HistoryHolder_InitializationFinished(object sender, EventArgs e)
        {
            _calculatedBoxes = CalculateBoxes(dbBoxConfig, micrographCache).ToList();

            Task.Run(async () => await SetCoeffs()).Wait();

            CoefficientCacheUpdateTimer = new System.Threading.Timer(
                new System.Threading.TimerCallback(CoefficientCacheUpdateTimerCallback),
                null, CoefficientCacheUpdateInterval, -1);

            BoxRecalculationTimer = new System.Threading.Timer(
                new System.Threading.TimerCallback(BoxRecalculationTimerCallback),
                null, BoxRecalculationInterval, -1);
        }

        private async void QuoteFeed_MessageReceived(object sender, IInstrumentPrice e)
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
                assetCache[e.Instrument].CurrentPrice = (InstrumentPrice)e.ClonePrice();

                // Get bets for current asset
                // That are not yet with WIN status
                var betCacheSnap = GetRunningBets();

                var assetBets = (from b in betCacheSnap
                                 where b.AssetPair == e.Instrument &&
                                 b.BetStatus != BetStates.Win
                                 select b).ToList();
                if (assetBets.Count == 0)
                    return;
                foreach (var bet in assetBets)
                {
                    ProcessBetCheck(bet, false);
                }
            }
            catch (Exception ex) { LogError("QuoteFeed_MessageReceived", ex); }
            finally { quoteReceivedSemaphoreSlim.Release(); }
        }

        private void Bet_TimeToGraphReached(object sender, EventArgs e)
        {
            try
            {
                GameBet bet = sender as GameBet;
                if (bet == null)
                    return;

                // Do initial Check            
                if (assetCache.ContainsKey(bet.AssetPair))
                {
                    if (assetCache[bet.AssetPair].CurrentPrice != null &&
                        assetCache[bet.AssetPair].PreviousPrice != null &&
                        assetCache[bet.AssetPair].CurrentPrice.MidPrice() > 0 &&
                        assetCache[bet.AssetPair].PreviousPrice.MidPrice() > 0)
                    {
                        ProcessBetCheck(bet, true);
                    }
                }

                // Add bet to cache
                lock (BetCacheLock)
                {
                    betCache.Add(bet);
                }
            }
            catch (Exception ex) { LogError("Bet_TimeToGraphReached", ex); }
        }
        private void Bet_TimeLenghFinished(object sender, EventArgs e)
        {
            GameBet sdr = sender as GameBet;
            if (sdr == null)
                return;

            ProcessBetTimeOut(sdr);
        }

        

        #endregion

        #region Nested Class
        private class PriceCache
        {
            public InstrumentPrice CurrentPrice { get; set; }
            public InstrumentPrice PreviousPrice { get; set; }
        }
        #endregion

       
    }
}
