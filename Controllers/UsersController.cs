
using Microsoft.AspNetCore.Mvc;

[ApiController]
[Route("api/[controller]")] // Routes to 'api/users'
public class UsersController : ControllerBase
{
  [HttpGet] 
    public string GetAllUsers()
    {
        return "This will return a list of all users in the MoMo wallet.";
    }
    // The "{id}" captures whatever comes after the slash
    [HttpGet("{id}")] 
    public IActionResult GetUser(string id)
    {

    if (id != "128b")
    {
      return BadRequest("User ID is required.");
    }
        // ASP.NET automatically takes "ei2283912" from the URL 
        // and plugs it into the 'id' parameter here.
        return Ok($"You requested the user with ID: {id}") ;
    }
}