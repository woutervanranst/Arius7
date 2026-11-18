namespace Arius.Api.AppData;

/// <summary>The single source of truth for job status sets. Terminal = a finished row that must never be
/// re-transitioned; non-terminal = an active row the single-active-job guard counts. Kept here so the SQL
/// guards, the unique index, and the endpoint filters can't drift apart.</summary>
public static class JobStatuses
{
    public static readonly string[] Terminal    = ["completed", "failed", "cancelled", "interrupted"];
    public static readonly string[] NonTerminal = ["running", "awaiting-cost", "rehydrating"];

    // Compile-time constants for inlining into SQL WHERE clauses (no user input — safe from injection).
    public const string TerminalSqlList    = "'completed','failed','cancelled','interrupted'";
    public const string NonTerminalSqlList = "'running','awaiting-cost','rehydrating'";
}
