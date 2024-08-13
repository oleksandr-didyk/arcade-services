// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.ApiVersioning.Swashbuckle;
using System.Net;
using Microsoft.AspNetCore.Mvc;
using StackExchange.Redis;

namespace ProductConstructionService.Api.Controllers;

public class CacheKey
{
    public required string Key { get; set; }
    public required string Value { get; set; }
}

[Route("redis")]
public class RedisController : Controller
{
    private readonly IDatabase _cache;

    public RedisController (IConnectionMultiplexer redis)
    {
        _cache = redis.GetDatabase();
    }

    [HttpPost("key")]
    [SwaggerApiResponse(HttpStatusCode.Created)]
    public async Task SetCacheKey([FromBody, Required] CacheKey cacheKey)
    {
        await _cache.StringSetAsync(cacheKey.Key, cacheKey.Value);
    }

    [HttpGet("key/{key}")]
    [SwaggerApiResponse(HttpStatusCode.OK, Type = typeof(string), Description = "The value for the key")]
    public async Task<string> GetCacheKey(string key)
    {
        var value = await _cache.StringGetAsync(key);

        return value.ToString();
    }

}
