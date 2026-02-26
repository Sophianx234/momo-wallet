using Microsoft.AspNetCore.Mvc;

[ApiController]
[Route("api/[controller]")] // This makes the URL: localhost:5000/api/hello
public class HelloController : ControllerBase
{
    [HttpGet]
    public string Get()
    {
        return "Hello from ASP.NET Core!";
    }
}


