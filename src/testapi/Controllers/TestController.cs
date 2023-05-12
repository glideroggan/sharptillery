using Microsoft.AspNetCore.Mvc;

namespace testapi.Controllers
{
    [ApiController]
    [Route("[controller]")]
    public class TestController : ControllerBase
    {

        private Random random = new();
        [HttpGet]
        [Route("get")]
        public async Task<IActionResult> Get()
        {
            return Ok();
        }
    }
}
