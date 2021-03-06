﻿using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Text;

namespace BoxOptions.Services.Models
{
    public class Box
    {
        string id;
        decimal minPrice;
        decimal maxPrice;
        double timeToGraph; // (in seconds), 
        double timeLength;//(in seconds), 
        decimal coefficient;
        
                
        public string Id { get => id; set => id= value; }
        public decimal MinPrice { get => minPrice; set => minPrice = value; }
        public decimal MaxPrice { get => maxPrice; set => maxPrice = value; }
        public double TimeToGraph { get => timeToGraph; set => timeToGraph = value; }
        public double TimeLength { get => timeLength; set => timeLength = value; }
        public decimal Coefficient { get => coefficient; set => coefficient = value; }
               
        public static Box FromJson(string json)
        {
            Box retval = JsonConvert.DeserializeObject<Box>(json);
            return retval;
        }
    }
}
