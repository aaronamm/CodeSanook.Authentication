﻿namespace Codesanook.Authorization.Models
{
    public class TokenResponse
    {
        public string AccessToken { get; set; }
        public string RefreshToken { get; set; } 
        public int UserId { get; set; }
    }
}