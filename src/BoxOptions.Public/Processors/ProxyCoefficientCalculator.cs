﻿using BoxOptions.Common;
using Flurl;
using Flurl.Http;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;

namespace BoxOptions.Public.Processors
{
    /// <summary>
    /// Coefficient calculator Using proxy to Coefficient API
    /// </summary>
    public class ProxyCoefficientCalculator : ICoefficientCalculator
    {
        /// <summary>
        /// Settings Object
        /// </summary>
        private readonly BoxOptionsSettings settings;

        /// <summary>
        /// Default constructor with ref to <see cref="BoxOptionsSettings"/>
        /// </summary>
        /// <param name="settings"></param>
        public ProxyCoefficientCalculator(BoxOptionsSettings settings)
        {
            this.settings = settings;
        }

        /// <summary>
        /// Change method forwarded to Coefficient API and returns result.
        /// </summary>
        /// <param name="pair"></param>
        /// <param name="timeToFirstOption"></param>
        /// <param name="optionLen"></param>
        /// <param name="priceSize"></param>
        /// <param name="nPriceIndex"></param>
        /// <param name="nTimeIndex"></param>
        /// <returns></returns>
        public async Task<string> ChangeAsync(string userId, string pair, int timeToFirstOption, int optionLen, double priceSize, int nPriceIndex, int nTimeIndex)
        {
            //TODO: UserId??
            string result = await $"{settings.BoxOptionsApi.CoefApiUrl}/change"
                .SetQueryParams(new
                {
                    pair,
                    timeToFirstOption,
                    optionLen,
                    priceSize,
                    nPriceIndex,
                    nTimeIndex
                })
                .GetStringAsync();

            
            

            return result;
        }
        
        /// <summary>
        /// Request method forwarded to Coefficient API and returns result.
        /// </summary>
        /// <param name="pair"></param>
        /// <returns></returns>
        public async Task<string> RequestAsync(string userId, string pair)
        {
            //TODO: UserId??            
            string result = await $"{settings.BoxOptionsApi.CoefApiUrl}/request"
                .SetQueryParams(new { pair, userId })
                .GetStringAsync();
            return result;
        }

        public bool ValidateChange(string userId, string pair, int timeToFirstOption, int optionLen, double priceSize, int nPriceIndex, int nTimeIndex)
        {
            // Parameter validation


            return true;
        }

        public bool ValidateChangeResult(string result)
        {
            // Result validation

            return true;
        }

        public bool ValidateRequest(string userId, string pair)
        {
            return true;
        }

        public bool ValidateRequestResult(string result)
        {
            Models.CoefModels.CoefRequestResult res = Models.CoefModels.CoefRequestResult.Parse(result);

            // Test if all coefs are equal to 1
            bool AllEqualOne = true;
            foreach (var block in res.CoefBlocks)
            {
                foreach (var coef in block.Coeffs)
                {
                    if (coef.HitCoeff != 1.0m || coef.MissCoeff != 1.0m)
                    {
                        AllEqualOne = false;
                        break;
                    }                    
                }
                if (AllEqualOne != false)
                    break;
            }
            if (AllEqualOne == true)
                throw new ArgumentException("All coefficients are equal to 1.0");
            

            return true;
        }
    }
}
