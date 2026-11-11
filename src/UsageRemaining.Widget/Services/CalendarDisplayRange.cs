namespace UsageRemaining.Widget.Services;

public readonly record struct CalendarDisplayRange(DateTime Start, int DayCount)
{
    public int RowCount => DayCount / 7;
    public DateTime EndExclusive => Start.AddDays(DayCount);

    public bool Contains(DateTime date) => date.Date >= Start && date.Date < EndExclusive;

    public static CalendarDisplayRange FromMonth(DateTime month)
    {
        var first = new DateTime(month.Year, month.Month, 1);
        return new(first.AddDays(-(int)first.DayOfWeek), 42);
    }

    public static CalendarDisplayRange FromWeek(DateTime anchor)
    {
        var day = anchor.Date;
        return new(day.AddDays(-(int)day.DayOfWeek), 7);
    }
}
