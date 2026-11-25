using Haldus;
using Microsoft.EntityFrameworkCore;
using Tracker;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Default")));
builder.Services.AddHaldus<AppDbContext>();
builder.Services.AddOpenApi();

var app = builder.Build();

app.UseHaldus();
app.MapOpenApi();

app.MapCrud<Project>()
    .Auditable()
    .Counts<Issue>()
    .Sortable(project => project.Name, project => project.CreatedAt);

var issues = app.MapCrud<Issue>()
    .Under<Project>()
    .Auditable()
    .ConcurrencyChecked()
    .Workflow<IssueStatus>(initial: IssueStatus.Open)
    .EditableIn(IssueStatus.Open, IssueStatus.InProgress)
    .DeletableIn(IssueStatus.Open)
    .Numbered("ISS-{seq:5}")
    .Filterable<IssueFilter>()
    .Sortable(issue => issue.Priority, issue => issue.CreatedAt);

issues.MapTransition("start", from: IssueStatus.Open, to: IssueStatus.InProgress);
issues.MapTransition<ResolveIssue>(from: [IssueStatus.Open, IssueStatus.InProgress], to: IssueStatus.Resolved);
issues.MapTransition("reopen", from: IssueStatus.Resolved, to: IssueStatus.Open);

app.Run();
