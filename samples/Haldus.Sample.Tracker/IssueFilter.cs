using Haldus;

namespace Tracker;

// Liste ucunun sorgu parametreleri: ?assignee=ayse&search=giriş&minPriority=4
public sealed class IssueFilter : IFilter<Issue>
{
    public string? Assignee { get; set; }

    public string? Search { get; set; }

    public int? MinPriority { get; set; }

    public IQueryable<Issue> Apply(IQueryable<Issue> query) => query
        .WhereIf(Assignee is not null, issue => issue.Assignee == Assignee)
        .WhereIf(Search is not null, issue => issue.Title.ToLower().Contains(Search!.ToLower()))
        .WhereIf(MinPriority is not null, issue => issue.Priority >= MinPriority);
}
