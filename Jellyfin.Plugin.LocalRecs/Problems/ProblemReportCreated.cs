namespace Jellyfin.Plugin.LocalRecs.Problems
{
    /// <summary>
    /// Answer to a new report.
    /// </summary>
    public class ProblemReportCreated
    {
        /// <summary>Gets or sets the report ID.</summary>
        public string Id { get; set; } = string.Empty;
    }
}
