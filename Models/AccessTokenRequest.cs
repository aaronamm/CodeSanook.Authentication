﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Web;

namespace CodeSanook.Authentication.Models
{
    public class AccessTokenRequest
    {
        public string RefreshToken { get; set; }
    }
}