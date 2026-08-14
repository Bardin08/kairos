namespace Kairos.Api.Scheduling;

public static class LimitsExtensions
{
    extension(Limits global)
    {
        /// <summary>Resolve a link's overrides against this global. Null inherits; zero does not.</summary>
        public Limits With(in LimitOverrides link) => new(
            link.MarginMinutes ?? global.MarginMinutes,
            link.MinNoticeMinutes ?? global.MinNoticeMinutes,
            link.DailyCap ?? global.DailyCap,
            link.StartIncrementMinutes ?? global.StartIncrementMinutes);
    }
}
