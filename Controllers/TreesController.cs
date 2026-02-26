

using Microsoft.AspNetCore.Mvc;
using Microsoft.VisualBasic;

public class Tree
{
  public string name {set; get;}
  public int id {set;get;}
  public string species {set;get;}
}

[ApiController]
[Route("api/[controller]")] // Routes to 'api/trees'
public class TreesController : ControllerBase
{

  List<Tree> mockData = new List<Tree>
  { new Tree { name = "Oak", id = 1, species = "Quercus" },
    new Tree { name = "Pine", id = 2, species = "Pinus" },
    new Tree { name = "Maple", id = 3, species = "Acer" } 
    };


  [HttpGet]
  public ActionResult<Tree> GetAllTrees()
  {
    return Ok(mockData);
  }
}