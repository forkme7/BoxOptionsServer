﻿using BoxOptions.Common.Extensions;
using BoxOptions.Common.Models;
using BoxOptions.Core.Interfaces;
using Common;
using System;

namespace BoxOptions.Services.Models
{
    public class GameBet : IGameBetItem, IDisposable
    {        
        readonly string userId;
        UserState user;

        public string UserId => userId;
        public UserState User => user;
        public decimal BetAmount { get; set; }
        public string AssetPair { get; set; }
        public DateTime Timestamp { get; set; }
        public Box Box { get; set; }
        public BetStates BetStatus { get; set; }
        public BoxSize CurrentParameters { get; set; }
        public DateTime? TimeToGraphStamp { get; private set; }
        public DateTime? WinStamp { get; set; }
        public DateTime? FinishedStamp { get; private set; }

        public string BetLog { get; private set; }

        #region IGameBetItem
        public string BoxId => Box.Id;
        public DateTime Date => Timestamp;
        string IGameBetItem.Box => Box.ToJson();
        string IGameBetItem.BetAmount => BetAmount.ToString(System.Globalization.CultureInfo.InvariantCulture);
        public string Parameters => CurrentParameters.ToJson();
        int IGameBetItem.BetStatus => (int)BetStatus;
        #endregion

        System.Threading.Timer BetTimer;

        public GameBet(string userId)
        {
            this.userId = userId;
            BetTimer = new System.Threading.Timer(new System.Threading.TimerCallback(WaitTimeToGraphCallback), null, -1, -1);
            user = null;
            TimeToGraphStamp = null;
            WinStamp = null;
            FinishedStamp = null;
            BetLog = "";
        }
        public GameBet(UserState user) :
            this(user.UserId)
        {
            this.user = user;
        }
        private void AssignUser(UserState user)
        {
            if (user != null)
                throw new InvalidOperationException("User already defined");
            this.user = user;
        }

        public override string ToString()
        {
            return string.Format("{0} | {1:f4}", Timestamp.ToDateTimeString(), BetAmount);
        }        

        internal void StartWaitTimeToGraph()
        {
            BetStatus = BetStates.Waiting;
            DateTime now = DateTime.UtcNow;
            BetLog += string.Format("RUNBET Calc:{0} Real:{1} Delta(seconds):{2}", 
                Timestamp.ToTimeString(), 
                now.ToTimeString(),
                (now- Timestamp).TotalSeconds);
            BetTimer.Change((int)(1000 * Box.TimeToGraph), -1);
        }

        private void WaitTimeToGraphCallback(object status)
        {
            ClearTimer();
            TimeToGraphStamp = DateTime.UtcNow;
            BetStatus = BetStates.OnGoing;

            BetLog += string.Format("\n\rGRAPHR Calc:{0} Real:{1} Delta(seconds):{2}",
                Timestamp.AddSeconds(Box.TimeToGraph).ToTimeString(),
                TimeToGraphStamp.Value.ToTimeString(),
                (TimeToGraphStamp.Value - Timestamp.AddSeconds(Box.TimeToGraph)).TotalSeconds);

            BetTimer = new System.Threading.Timer(new System.Threading.TimerCallback(WaitTimeLengthCallback), Box, (int)(1000 * Box.TimeLength), -1);

            TimeToGraphReached?.Invoke(this, new EventArgs());
        }
        private void WaitTimeLengthCallback(object status)
        {
            ClearTimer();
            FinishedStamp = DateTime.UtcNow;

            BetLog += string.Format("\n\rBETEND Calc:{0} Real:{1} Delta(seconds):{2}",
                Timestamp.AddSeconds(Box.TimeToGraph+Box.TimeLength).ToTimeString(),
                FinishedStamp.Value.ToTimeString(),
                (FinishedStamp.Value - Timestamp.AddSeconds(Box.TimeToGraph+Box.TimeLength)).TotalSeconds);
                        
            TimeLenghFinished?.Invoke(this, new EventArgs());
        }
        private void ClearTimer()
        {
            BetTimer.Dispose();
            BetTimer = null;

        }
        public void Dispose()
        {
            if (BetTimer != null)
            {
                BetTimer.Change(-1, -1);
                BetTimer.Dispose();
                BetTimer = null;
            }
        }

        public event EventHandler TimeToGraphReached;
        public event EventHandler TimeLenghFinished;

    }
}
