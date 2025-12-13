using Microsoft.AspNetCore.Mvc;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace testapi.Controllers
{
    [ApiController]
    [Route("[controller]")]
    public class TestController : ControllerBase
    {

        private readonly Random _random = new();
        [HttpGet]
        [Route("get")]
        public async Task<IActionResult> Get(
            [FromQuery] int? simulateErrors = null,
            [FromQuery] int? delayMs = null,
            [FromQuery] int? status = null,
            [FromQuery] double? failRatio = null,
            [FromQuery] int? bytes = null,
            CancellationToken cancellationToken = default)
        {
            // High-level simulation preset.
            // - Mostly OK
            // - Occasional 4xx/5xx
            // - Rare slow responses to trigger client timeouts (depending on client -T)
            if (simulateErrors.HasValue && simulateErrors.Value != 0)
            {
                // Baseline jitter.
                var jitter = _random.Next(0, 40);
                delayMs = (delayMs ?? 0) + jitter;

                // Rare spike.
                if (_random.NextDouble() < 0.01)
                    delayMs += _random.Next(1200, 2500);

                // Error ratio.
                failRatio ??= 0.10;
                if (_random.NextDouble() < failRatio.Value)
                {
                    // Choose a plausible failure code.
                    var codes = new[] { 400, 401, 403, 404, 409, 429, 500, 502, 503 };
                    status ??= codes[_random.Next(0, codes.Length)];
                }

                // Default body size so response length metrics have something to show.
                bytes ??= 128;
            }

            if (delayMs.HasValue && delayMs.Value > 0)
                await Task.Delay(delayMs.Value, cancellationToken);

            if (failRatio.HasValue && failRatio.Value > 0)
            {
                var ratio = failRatio.Value;
                if (ratio > 1) ratio = 1;

                if (_random.NextDouble() < ratio)
                    status ??= 500;
            }

            if (status.HasValue)
            {
                var code = status.Value;
                if (code < 100) code = 100;
                if (code > 599) code = 599;

                if (bytes.HasValue && bytes.Value > 0)
                    return StatusCode(code, new string('x', bytes.Value));

                return StatusCode(code);
            }

            if (bytes.HasValue && bytes.Value > 0)
                return Ok(new string('x', bytes.Value));

            return Ok();
        }
    }
}
