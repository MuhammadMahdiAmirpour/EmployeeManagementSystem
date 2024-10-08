using System.Text.Json.Serialization;

namespace BaseLibrary.Entities;

public class BaseEntity {
	//Relationship : One to Many
	[JsonIgnore] public List<Employee>? Employees { get; set; }
	public int     Id   { get; set; }
	public string? Name { get; set; }
}
