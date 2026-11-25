using System.ComponentModel.DataAnnotations;
using Haldus;

namespace Tracker;

public enum IssueStatus
{
    Open,
    InProgress,
    Resolved,
}

public partial class Issue
{
    [Required]
    [MaxLength(200)]
    public string Title { get; set; } = "";

    [MaxLength(4000)]
    public string? Description { get; set; }

    [Range(1, 5)]
    public int Priority { get; set; } = 3;

    [MaxLength(100)]
    public string? Assignee { get; set; }

    // Herkese açık setter'ı olmadığı için yalnızca yanıtta görünür; onu ResolveIssue geçişi yazar.
    public string? Resolution { get; private set; }

    public void Resolve(string resolution) => Resolution = resolution;
}

// POST /projects/{projectId}/issues/{id}/resolve isteğinin gövdesi ve etkisi.
public sealed class ResolveIssue : ITransition<Issue>
{
    [Required]
    [MaxLength(500)]
    public string Resolution { get; init; } = "";

    public void Apply(Issue issue) => issue.Resolve(Resolution);
}
