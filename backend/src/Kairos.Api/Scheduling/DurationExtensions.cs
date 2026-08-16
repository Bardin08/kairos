namespace Kairos.Api.Scheduling;

public static class DurationExtensions
{
    extension(IReadOnlyList<int> durations)
    {
        public bool Offers(int minutes) => durations.Contains(minutes);

        public int Default => durations[0];

        public bool IsSingle => durations.Count == 1;
    }
}
