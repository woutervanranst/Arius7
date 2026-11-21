namespace Arius.Api.Shared.AppData;

/// <summary>Job status sets shared by the SQL guards, the single-active-job unique index and the endpoint filters.
/// A terminal job is never transitioned again; a non-terminal job is the repository's active job.</summary>
public static class JobStatuses
{
    public static readonly string[] Terminal    = ["completed", "failed", "cancelled", "interrupted"];
    public static readonly string[] NonTerminal = ["running", "awaiting-cost", "rehydrating"];

    // SQL literal lists for interpolating into WHERE clauses; constants, so safe from injection.
    public const string TerminalSqlList    = "'completed','failed','cancelled','interrupted'";
    public const string NonTerminalSqlList = "'running','awaiting-cost','rehydrating'";
}
