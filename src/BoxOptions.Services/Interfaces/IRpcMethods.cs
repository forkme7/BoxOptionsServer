﻿using System.Collections.Generic;
using BoxOptions.Core;
using WampSharp.V2.Rpc;

namespace BoxOptions.Services
{
    public interface IRpcMethods
    {
        /// <summary>
        /// Client calls init.chartdata RPC to get that data for charts
        /// </summary>
        /// <returns></returns>
        [WampProcedure("init.chartdata")]
        Dictionary<string, Price[]> InitChartData();

        /// <summary>
        /// Client calls init.assets RPC method to get list of asset pairs
        /// </summary>
        /// <returns></returns>
        [WampProcedure("init.assets")]
        AssetPair[] InitAssets();
    }
}
