using System.ComponentModel.DataAnnotations;

namespace Tracker;

public partial class Project
{
    [Required]
    [MaxLength(100)]
    public string Name { get; set; } = "";

    [MaxLength(1000)]
    public string? Description { get; set; }
}
